using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

using KeetaNet.Anchor.Generated.Node;

using GeneratedBlock = KeetaNet.Anchor.Generated.Node.Block;
using GeneratedCertificate = KeetaNet.Anchor.Generated.Node.Certificate;
using GeneratedHistoryEntry = KeetaNet.Anchor.Generated.Node.HistoryEntry;
using GeneratedRepresentative = KeetaNet.Anchor.Generated.Node.Representative;
using GeneratedVote = KeetaNet.Anchor.Generated.Node.Vote;

namespace KeetaNet.Anchor;

/// <summary>
/// The base client for the KeetaNet node API.
/// </summary>
/// <remarks>
/// The client serves ledger reads and the two-round transmit flow over the
/// transport generated from the canonical OpenAPI spec. Account-bound
/// conveniences live on <see cref="UserClient"/>. This type ports the
/// reference multi-representative <c>Client</c>.
/// </remarks>
public sealed class KeetaClient : IDisposable
{
	/// <summary>The block version that the reference clients build.</summary>
	internal const int BlockVersion = 2;

	/// <summary>The maximum age of a representative-weight snapshot before a refresh.</summary>
	private static readonly TimeSpan RepresentativeRefreshInterval = TimeSpan.FromMinutes(5);

	private readonly WasmRuntime _runtime;

	/// <summary>The client-owned transport. Null when an injected one is borrowed.</summary>
	private readonly HttpClient? _ownedHttp;

	/// <summary>The transport shared by every representative's generated API.</summary>
	private readonly HttpClient _representativeHttp;

	/// <summary>The representative set in registry order. Each entry holds its own transport.</summary>
	private readonly List<Representative> _representatives;

	/// <summary>Guards <see cref="_representatives"/>. A transmit can run concurrently with a refresh.</summary>
	private readonly object _representativesLock = new();

	/// <summary>The time of the last representative-weight refresh. Null until the first refresh.</summary>
	private DateTimeOffset? _representativesRefreshedAt;

	/// <summary>The weight refresh started at construction. Voting rounds await it first.</summary>
	private readonly Task? _initialRefresh;

	private readonly long? _network;

	/// <summary>The base token of the bound network. Null when no network is bound.</summary>
	private readonly Crypto.Account? _baseToken;

	/// <summary>One representative with its registry entry, its transport, and its last-seen voting weight.</summary>
	private sealed class Representative
	{
		public Representative(RepresentativeEndpoint endpoint, NodeApi api)
		{
			Endpoint = endpoint;
			Api = api;
		}

		public RepresentativeEndpoint Endpoint { get; }

		public NodeApi Api { get; }

		public BigInteger? Weight { get; set; }
	}

	/// <summary>A vote paired with the representative that issued it.</summary>
	private readonly record struct RepresentativeVote(Representative Issuer, string VoteBase64);

	/// <summary>
	/// Creates a client for the node API at <paramref name="nodeUrl"/>.
	/// </summary>
	/// <remarks>
	/// The client treats the node as a single-representative network. An
	/// injected <paramref name="http"/> (for example from
	/// <c>IHttpClientFactory</c>) is borrowed and never disposed. A bound
	/// <paramref name="network"/> enables the write path. Without one the
	/// client stays read-only.
	/// </remarks>
	internal KeetaClient(WasmRuntime runtime, string nodeUrl, HttpClient? http = null, long? network = null)
		: this(runtime, new[] { new RepresentativeEndpoint(null, nodeUrl, null) }, http, network)
	{
	}

	/// <summary>
	/// Creates a client over <paramref name="representatives"/>.
	/// </summary>
	/// <remarks>
	/// Votes fan out to every representative. Reads go to the representative
	/// with the highest known weight.
	/// </remarks>
	internal KeetaClient(
		WasmRuntime runtime,
		IReadOnlyList<RepresentativeEndpoint> representatives,
		HttpClient? http = null,
		long? network = null)
	{
		_runtime = runtime;
		_network = network;
		if (network is { } bound)
		{
			_baseToken = runtime.Blocks.NetworkBaseToken(bound);
		}

		if (http is null)
		{
			_ownedHttp = new HttpClient();
			http = _ownedHttp;
		}

		_representativeHttp = http;
		_representatives = representatives
			.Select(endpoint => new Representative(endpoint, new NodeApi(http) { BaseUrl = endpoint.ApiUrl }))
			.ToList();

		// The reference client refreshes weights at construction so the first
		// transmit already orders by weight. A lone representative needs no
		// ordering.
		if (_representatives.Count > 1)
		{
			_initialRefresh = InitialRefresh();
		}
	}

	/// <summary>
	/// Runs the construction-time weight refresh.
	/// </summary>
	/// <remarks>
	/// The refresh is best-effort, as in the reference client. A failure
	/// leaves the registry order in place, and the next voting round retries.
	/// </remarks>
	private async Task InitialRefresh()
	{
		try
		{
			await UpdateReps(addNewRepresentatives: false, CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception)
		{
			// Stale weights only affect ordering. The client stays usable.
		}
	}

	/// <summary>Returns a point-in-time copy of the representative set that is safe to enumerate.</summary>
	private Representative[] SnapshotRepresentatives()
	{
		lock (_representativesLock)
		{
			return _representatives.ToArray();
		}
	}

	/// <summary>
	/// The representative for single-target requests. This is the
	/// representative with the highest known weight, or the first one before
	/// any refresh.
	/// </summary>
	private Representative Primary =>
		SnapshotRepresentatives().OrderByDescending(rep => rep.Weight ?? BigInteger.MinusOne).First();

	/// <summary>The transport of the primary representative. Every single-target read uses it.</summary>
	private NodeApi Api => Primary.Api;

	/// <summary>The advertised P2P endpoint of the primary representative, if any.</summary>
	internal string? PrimaryP2pUrl => Primary.Endpoint.P2pUrl;

	/// <summary>The bound network id, or null for a read-only client.</summary>
	public long? Network => _network;

	/// <summary>
	/// The base token of the bound network, or null for a read-only client.
	/// </summary>
	/// <remarks>
	/// The base token is the implicit fee currency. The client owns the
	/// account. Do not dispose it.
	/// </remarks>
	public Crypto.Account? BaseToken => _baseToken;

	/// <summary>Gets the node software version string.</summary>
	public async Task<string> GetVersion(CancellationToken cancellationToken = default)
	{
		GetNodeVersionResponse response = await Attempt(() => Api.GetNodeVersionAsync(cancellationToken)).ConfigureAwait(false);
		return response.Node ?? "";
	}

	/// <summary>
	/// Gets the ledger state of <paramref name="account"/>.
	/// </summary>
	/// <returns>
	/// The head block, the delegated representative, the published info, and
	/// the token balances.
	/// </returns>
	public async Task<AccountState> GetAccountInfo(
		Crypto.Account account,
		CancellationToken cancellationToken = default)
	{
		GetAccountStateResponse state = await Attempt(() => Api.GetAccountStateAsync(account.PublicKeyString, cancellationToken)).ConfigureAwait(false);
		return DecodeState(state.CurrentHeadBlock, state.CurrentHeadBlockHeight, state.Representative, state.Info, state.Balances);
	}

	/// <summary>
	/// Gets the ledger state of several <paramref name="accounts"/> in one call.
	/// </summary>
	/// <returns>One entry per account in request order.</returns>
	public async Task<IReadOnlyList<AccountState>> GetAccountsInfo(
		IReadOnlyList<Crypto.Account> accounts,
		CancellationToken cancellationToken = default)
	{
		string joined = string.Join(",", accounts.Select(account => account.PublicKeyString));
		ICollection<GetAccountStatesResponseItem> states = await Attempt(() => Api.GetAccountStatesAsync(joined, cancellationToken)).ConfigureAwait(false);

		return states
			.Select(item => DecodeState(item.CurrentHeadBlock, item.CurrentHeadBlockHeight, item.Representative, item.Info, item.Balances))
			.ToArray();
	}

	/// <summary>
	/// Gets the total supply of <paramref name="token"/> from its account state.
	/// </summary>
	/// <returns>The supply, or null for an account that is not a token.</returns>
	public async Task<BigInteger?> GetTokenSupply(
		Crypto.Account token,
		CancellationToken cancellationToken = default)
	{
		AccountState state = await GetAccountInfo(token, cancellationToken).ConfigureAwait(false);
		return state.Info?.Supply;
	}

	/// <summary>Gets the point-in-time XOR checksum of the node's ledger.</summary>
	public async Task<LedgerChecksum> GetLedgerChecksum(CancellationToken cancellationToken = default)
	{
		GetLedgerChecksumResponse checksum = await Attempt(() => Api.GetLedgerChecksumAsync(cancellationToken)).ConfigureAwait(false);

		DateTimeOffset? moment = null;
		if (!string.IsNullOrEmpty(checksum.Moment))
		{
			moment = DateTimeOffset.Parse(checksum.Moment, CultureInfo.InvariantCulture);
		}

		return new LedgerChecksum(
			OptionalHexAmount(checksum.Checksum) ?? BigInteger.Zero,
			moment,
			checksum.MomentRange);
	}

	/// <summary>
	/// Gets the named <paramref name="representative"/> and its voting weight.
	/// </summary>
	/// <remarks>
	/// When <paramref name="representative"/> is omitted, the method returns
	/// the contacted node's own representative.
	/// </remarks>
	public async Task<NodeRepresentative> GetRepresentativeInfo(
		Crypto.Account? representative = null,
		CancellationToken cancellationToken = default)
	{
		if (representative is null)
		{
			GeneratedRepresentative own = await Attempt(() => Api.GetNodeRepresentativeAsync(cancellationToken)).ConfigureAwait(false);
			return DecodeRepresentative(own);
		}

		GeneratedRepresentative named = await Attempt(() => Api.GetRepresentativeAsync(representative.PublicKeyString, cancellationToken)).ConfigureAwait(false);
		return DecodeRepresentative(named);
	}

	/// <summary>Gets every representative that the node knows, with the advertised endpoints.</summary>
	public async Task<IReadOnlyList<NodeRepresentative>> GetAllRepresentativeInfo(CancellationToken cancellationToken = default)
	{
		GetAllRepresentativesResponse response = await Attempt(() => Api.GetAllRepresentativesAsync(cancellationToken)).ConfigureAwait(false);
		ICollection<GeneratedRepresentative> representatives = response.Representatives ?? Array.Empty<GeneratedRepresentative>();

		return representatives.Select(DecodeRepresentative).ToArray();
	}

	/// <summary>Gets the node statistics as opaque JSON.</summary>
	public async Task<JsonElement> GetNodeStats(CancellationToken cancellationToken = default)
	{
		object stats = await Attempt(() => Api.GetNodeStatsAsync(cancellationToken)).ConfigureAwait(false);
		return (JsonElement)stats;
	}

	/// <summary>Gets the connected peers as opaque JSON.</summary>
	public async Task<JsonElement> GetPeers(CancellationToken cancellationToken = default)
	{
		object peers = await Attempt(() => Api.GetPeersAsync(cancellationToken)).ConfigureAwait(false);
		return (JsonElement)peers;
	}

	/// <summary>Gets every token balance that <paramref name="account"/> holds.</summary>
	public async Task<IReadOnlyList<TokenBalance>> GetAllBalances(
		Crypto.Account account,
		CancellationToken cancellationToken = default)
	{
		GetAccountBalancesResponse response = await Attempt(() => Api.GetAccountBalancesAsync(account.PublicKeyString, cancellationToken)).ConfigureAwait(false);
		return DecodeBalances(response.Balances);
	}

	/// <summary>Gets the settled balance of <paramref name="account"/> in base units of <paramref name="token"/>.</summary>
	public async Task<BigInteger> GetBalance(
		Crypto.Account account,
		Crypto.Account token,
		CancellationToken cancellationToken = default)
	{
		GetAccountBalanceResponse response = await Attempt(() => Api.GetAccountBalanceAsync(account.PublicKeyString, token.PublicKeyString, cancellationToken)).ConfigureAwait(false);
		return OptionalHexAmount(response.Balance) ?? BigInteger.Zero;
	}

	/// <summary>Gets the head block of the chain of <paramref name="account"/>, or null for a never-used account.</summary>
	public async Task<Crypto.Block?> GetHeadBlock(
		Crypto.Account account,
		CancellationToken cancellationToken = default)
	{
		GetAccountHeadResponse response = await Attempt(() => Api.GetAccountHeadAsync(account.PublicKeyString, cancellationToken)).ConfigureAwait(false);
		return DecodeBlock(response.Block);
	}

	/// <summary>Gets the next pending (unreceived) block for <paramref name="account"/>, if any.</summary>
	public async Task<Crypto.Block?> GetPendingBlock(
		Crypto.Account account,
		CancellationToken cancellationToken = default)
	{
		GetPendingBlockResponse response = await Attempt(() => Api.GetPendingBlockAsync(account.PublicKeyString, cancellationToken)).ConfigureAwait(false);
		return DecodeBlock(response.Block);
	}

	/// <summary>Gets the block identified by <paramref name="blockHash"/> on the given <paramref name="side"/>, if present.</summary>
	public async Task<Crypto.Block?> GetBlock(
		Crypto.BlockHash blockHash,
		LedgerSide? side = null,
		CancellationToken cancellationToken = default)
	{
		Side2? generated = side switch
		{
			LedgerSide.Main => Side2.Main,
			LedgerSide.Side => Side2.Side,
			_ => null,
		};

		GetBlockResponse response = await Attempt(() => Api.GetBlockAsync(blockHash.ToString(), generated, cancellationToken)).ConfigureAwait(false);
		return DecodeBlock(response.Block);
	}

	/// <summary>Gets the block that follows <paramref name="blockHash"/>, if one exists.</summary>
	public async Task<Crypto.Block?> GetSuccessorBlock(
		Crypto.BlockHash blockHash,
		CancellationToken cancellationToken = default)
	{
		GetSuccessorBlockResponse response = await Attempt(() => Api.GetSuccessorBlockAsync(blockHash.ToString(), cancellationToken)).ConfigureAwait(false);
		return DecodeBlock(response.SuccessorBlock);
	}

	/// <summary>
	/// Gets the block that <paramref name="account"/> produced for the
	/// idempotent <paramref name="key"/>, if any.
	/// </summary>
	/// <remarks>
	/// The search covers the given <paramref name="side"/>, or the main
	/// ledger when omitted.
	/// </remarks>
	public async Task<Crypto.Block?> GetBlockFromIdempotent(
		Crypto.Account account,
		string key,
		LedgerSide? side = null,
		CancellationToken cancellationToken = default)
	{
		Side3? generated = side switch
		{
			LedgerSide.Main => Side3.Main,
			LedgerSide.Side => Side3.Side,
			_ => null,
		};

		GetBlockFromIdempotentResponse response = await Attempt(() => Api.GetBlockFromIdempotentAsync(account.PublicKeyString, key, generated, cancellationToken)).ConfigureAwait(false);
		return DecodeBlock(response.Block);
	}

	/// <summary>
	/// Gets the verified votes that the node holds for
	/// <paramref name="blockHash"/> on <paramref name="side"/>.
	/// </summary>
	/// <returns>The votes, or null when the node holds none.</returns>
	/// <remarks>The caller owns the votes and must dispose them.</remarks>
	public async Task<IReadOnlyList<Crypto.Vote>?> GetBlockVotes(
		Crypto.BlockHash blockHash,
		LedgerSide side = LedgerSide.Main,
		CancellationToken cancellationToken = default)
	{
		Side generated = side == LedgerSide.Side ? Side.Side : Side.Main;
		GetBlockVotesResponse response = await Attempt(() => Api.GetBlockVotesAsync(blockHash.ToString(), generated, cancellationToken)).ConfigureAwait(false);
		if (response.Votes is null)
		{
			return null;
		}

		var votes = new List<Crypto.Vote>(response.Votes.Count);
		try
		{
			foreach (GeneratedVote vote in response.Votes)
			{
				votes.Add(DecodeVote(vote.Binary));
			}
		}
		catch
		{
			foreach (Crypto.Vote vote in votes)
			{
				vote.Dispose();
			}

			throw;
		}

		return votes;
	}

	/// <summary>
	/// Gets one page of the block chain of <paramref name="account"/>, most
	/// recent first, bounded by <paramref name="query"/>.
	/// </summary>
	/// <returns>The page and the cursor for the next page.</returns>
	/// <remarks>The caller owns the blocks and must dispose them.</remarks>
	public async Task<ChainPage> GetChain(
		Crypto.Account account,
		ChainQuery? query = null,
		CancellationToken cancellationToken = default)
	{
		ChainQuery bounds = query ?? new ChainQuery();
		GetAccountChainResponse response = await Attempt(() => Api.GetAccountChainAsync(
			account.PublicKeyString,
			bounds.Start?.ToString(),
			bounds.End?.ToString(),
			bounds.Limit,
			cancellationToken)).ConfigureAwait(false);

		ICollection<GetAccountChainResponseBlocksItem> items = response.Blocks ?? Array.Empty<GetAccountChainResponseBlocksItem>();
		var blocks = new List<Crypto.Block>(items.Count);
		try
		{
			foreach (GetAccountChainResponseBlocksItem item in items)
			{
				if (DecodeBlock(item.Block) is { } block)
				{
					blocks.Add(block);
				}
			}
		}
		catch
		{
			foreach (Crypto.Block block in blocks)
			{
				block.Dispose();
			}

			throw;
		}

		return new ChainPage(blocks, OptionalBlockHash(response.NextKey));
	}

	/// <summary>
	/// Gets one page of the committed staple history of
	/// <paramref name="account"/>, bounded by <paramref name="query"/>.
	/// </summary>
	/// <returns>The page and the cursor for the next page.</returns>
	public async Task<HistoryPage> GetHistory(
		Crypto.Account account,
		HistoryQuery? query = null,
		CancellationToken cancellationToken = default)
	{
		HistoryQuery bounds = query ?? new HistoryQuery();
		GetAccountHistoryResponse response = await Attempt(() => Api.GetAccountHistoryAsync(
			account.PublicKeyString,
			bounds.Start?.ToString(),
			bounds.Limit,
			cancellationToken)).ConfigureAwait(false);

		return DecodeHistoryPage(response.History, response.NextKey);
	}

	/// <summary>
	/// Gets one page of the node's global staple history, bounded by
	/// <paramref name="query"/>.
	/// </summary>
	/// <returns>The page and the cursor for the next page.</returns>
	public async Task<HistoryPage> GetGlobalHistory(
		HistoryQuery? query = null,
		CancellationToken cancellationToken = default)
	{
		HistoryQuery bounds = query ?? new HistoryQuery();
		GetGlobalHistoryResponse response = await Attempt(() => Api.GetGlobalHistoryAsync(
			bounds.Start?.ToString(),
			bounds.Limit,
			cancellationToken)).ConfigureAwait(false);

		return DecodeHistoryPage(response.History, response.NextKey);
	}

	/// <summary>
	/// Lists the ACL entries where <paramref name="account"/> is the principal.
	/// </summary>
	/// <remarks>The caller owns the returned accounts and permission sets.</remarks>
	public async Task<IReadOnlyList<Acl>> ListAclsByPrincipal(
		Crypto.Account account,
		CancellationToken cancellationToken = default)
	{
		ListAclsByPrincipalResponse response = await Attempt(() => Api.ListAclsByPrincipalAsync(account.PublicKeyString, cancellationToken)).ConfigureAwait(false);
		return DecodeAcls(response.Permissions);
	}

	/// <summary>
	/// Lists the ACL entries granted to <paramref name="account"/> as an entity.
	/// </summary>
	/// <remarks>The caller owns the returned accounts and permission sets.</remarks>
	public async Task<IReadOnlyList<Acl>> ListAclsByEntity(
		Crypto.Account account,
		CancellationToken cancellationToken = default)
	{
		ListAclsByEntityResponse response = await Attempt(() => Api.ListAclsByEntityAsync(account.PublicKeyString, cancellationToken)).ConfigureAwait(false);
		return DecodeAcls(response.Permissions);
	}

	/// <summary>
	/// Creates a builder pre-set with the block version, the bound network,
	/// <paramref name="account"/> as the originator, and the current moment.
	/// </summary>
	/// <remarks>
	/// <paramref name="signer"/> signs, or the account itself when null. The
	/// caller positions the builder, appends operations, and builds. The
	/// method requires a bound network.
	/// </remarks>
	internal Crypto.BlockBuilder InitBuilder(Crypto.Account account, Crypto.Account? signer = null)
	{
		(long network, _) = RequireNetwork();

		return _runtime.Blocks.NewBuilder()
			.WithVersion(BlockVersion)
			.WithNetwork(network)
			.WithAccount(account)
			.WithSigner(signer ?? account)
			.WithDate(DateTimeOffset.UtcNow);
	}

	/// <summary>Publishes one signed block as its own staple. See the list overload.</summary>
	public Task<bool> Transmit(
		Crypto.Block block,
		TransmitOptions? options = null,
		CancellationToken cancellationToken = default) =>
		Transmit(new[] { block }, options, cancellationToken);

	/// <summary>
	/// Publishes <paramref name="blocks"/> as one atomic staple through the
	/// two-round vote flow.
	/// </summary>
	/// <remarks>
	/// The temporary round fans out to every representative. When its votes
	/// require a fee, the factory in <paramref name="options"/> receives that
	/// round and its block joins the permanent round and the staple. The
	/// permanent round contacts only the representatives that issued a
	/// temporary vote. The staple carries every permanent vote.
	/// </remarks>
	public async Task<bool> Transmit(
		IReadOnlyList<Crypto.Block> blocks,
		TransmitOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		TransmitOptions resolved = options ?? new TransmitOptions();
		List<string> encoded = blocks.Select(EncodeBlock).ToList();

		await RefreshRepresentatives(cancellationToken).ConfigureAwait(false);
		IReadOnlyList<RepresentativeVote> temporary =
			await RequestVotes(encoded, priorVotes: null, resolved.Quotes.ToArray(), cancellationToken).ConfigureAwait(false);

		Crypto.Block? feeBlock = null;
		try
		{
			if (VotesRequireFee(temporary))
			{
				feeBlock = await FeeBlockFor(blocks, temporary, resolved, cancellationToken).ConfigureAwait(false);
			}

			IReadOnlyList<Crypto.Block> all = blocks;
			if (feeBlock is not null)
			{
				// The fee block joins the permanent round last. The node
				// recognizes it by its FEE purpose and escalates the temporary
				// votes over the original blocks.
				all = blocks.Append(feeBlock).ToArray();
				encoded.Add(EncodeBlock(feeBlock));
			}

			IReadOnlyList<RepresentativeVote> permanent =
				await RequestVotes(encoded, temporary, quotes: null, cancellationToken).ConfigureAwait(false);
			return await PublishStaple(all, permanent, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			feeBlock?.Dispose();
		}
	}

	/// <summary>
	/// Refreshes the voting weights of the known representatives from the ledger.
	/// </summary>
	/// <remarks>
	/// Unknown representatives join the set when
	/// <paramref name="addNewRepresentatives"/> is set. Reads and votes
	/// prefer the representatives with higher weights afterward.
	/// </remarks>
	[SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP007:Don't dispose injected",
		Justification = "GetAllRepresentativeInfo transfers ownership of the returned accounts to the caller; this method is that caller.")]
	public async Task UpdateReps(bool addNewRepresentatives = false, CancellationToken cancellationToken = default)
	{
		IReadOnlyList<NodeRepresentative> known = await GetAllRepresentativeInfo(cancellationToken).ConfigureAwait(false);
		foreach (NodeRepresentative info in known)
		{
			using (info.Account)
			{
				string key = info.Account.PublicKeyString;
				lock (_representativesLock)
				{
					// A URL-only client has no key for its representative, so
					// the advertised API URL identifies it instead.
					Representative? match = _representatives.Find(rep =>
						rep.Endpoint.Key == key
						|| (rep.Endpoint.Key is null && rep.Endpoint.ApiUrl == info.ApiUrl));
					if (match is not null)
					{
						match.Weight = info.Weight;
						continue;
					}

					// Two entries with one URL would double-contact the same
					// node, so an already-known endpoint never joins again.
					bool knownUrl = _representatives.Exists(rep => rep.Endpoint.ApiUrl == info.ApiUrl);
					if (addNewRepresentatives && !knownUrl && !string.IsNullOrEmpty(info.ApiUrl))
					{
						var endpoint = new RepresentativeEndpoint(key, info.ApiUrl, null);
						_representatives.Add(new Representative(endpoint, new NodeApi(_representativeHttp) { BaseUrl = info.ApiUrl })
						{
							Weight = info.Weight,
						});
					}
				}
			}
		}

		_representativesRefreshedAt = DateTimeOffset.UtcNow;
	}

	/// <summary>
	/// Ensures that the representative weights are fresh before a voting round.
	/// </summary>
	/// <remarks>A failed refresh leaves the previous snapshot in place.</remarks>
	private async Task RefreshRepresentatives(CancellationToken cancellationToken)
	{
		if (_initialRefresh is { } initial)
		{
			await initial.ConfigureAwait(false);
		}

		bool fresh = _representativesRefreshedAt is { } at
			&& DateTimeOffset.UtcNow - at < RepresentativeRefreshInterval;
		if (fresh || SnapshotRepresentatives().Length == 1)
		{
			return;
		}

		try
		{
			await UpdateReps(addNewRepresentatives: false, cancellationToken).ConfigureAwait(false);
		}
		catch (KeetaException)
		{
			// Stale weights only affect ordering. Voting proceeds regardless.
		}
	}

	/// <summary>
	/// Builds and signs the fee block that the votes in
	/// <paramref name="staple"/> require.
	/// </summary>
	/// <returns>The signed fee block, or null when no fee is owed.</returns>
	/// <remarks>
	/// The balance of <paramref name="account"/> pays the fee, and
	/// <paramref name="signer"/> signs. The two differ under delegated
	/// signing. The block chains atop the account's block in the staple, or
	/// atop its ledger head, so the payer need not appear in the round. The
	/// method requires a bound network.
	/// </remarks>
	public async Task<Crypto.Block?> BuildFeeBlock(
		Crypto.VoteStaple staple,
		Crypto.Account account,
		Crypto.Account signer,
		IReadOnlyList<Crypto.Account>? feeTokenPriority = null,
		CancellationToken cancellationToken = default)
	{
		(long network, Crypto.Account baseToken) = RequireNetwork();

		int[] feeOps = _runtime.StapleFeeSends(staple.Handle, baseToken.Handle, Crypto.Handles.Of(feeTokenPriority));
		if (feeOps.Length == 0)
		{
			return null;
		}

		// Adopt every operation handle up front so a failure anywhere below
		// releases them all.
		var feeOperations = new List<Crypto.BlockOperation>(feeOps.Length);
		foreach (int handle in feeOps)
		{
			feeOperations.Add(new Crypto.BlockOperation(_runtime, handle));
		}

		try
		{
			string? previous = _runtime.StapleTipFor(staple.Handle, account.Handle);
			if (previous is null)
			{
				AccountState state = await GetAccountInfo(account, cancellationToken).ConfigureAwait(false);
				previous = state.HeadBlock?.ToString();
			}

			using var builder = _runtime.Blocks.NewBuilder();
			builder
				.WithVersion(BlockVersion)
				.WithNetwork(network)
				.WithAccount(account)
				.WithSigner(signer)
				.WithPurpose(Crypto.BlockPurpose.Fee)
				.WithDate(DateTimeOffset.UtcNow);
			PositionAfter(builder, previous);

			foreach (Crypto.BlockOperation feeOp in feeOperations)
			{
				builder.AddOperation(feeOp);
			}

			return builder.Build();
		}
		finally
		{
			foreach (Crypto.BlockOperation feeOp in feeOperations)
			{
				feeOp.Dispose();
			}
		}
	}

	/// <summary>
	/// Gets every certificate that <paramref name="account"/> has published
	/// on-chain, each with its recorded intermediates.
	/// </summary>
	/// <returns>An empty list for an account with no published certificates.</returns>
	public async Task<IReadOnlyList<Certificate>> GetAllCertificates(
		Crypto.Account account,
		CancellationToken cancellationToken = default)
	{
		GetAccountCertificatesResponse response = await Attempt(() => Api.GetAccountCertificatesAsync(account.PublicKeyString, cancellationToken)).ConfigureAwait(false);
		ICollection<GeneratedCertificate> records = response.Certificates ?? Array.Empty<GeneratedCertificate>();

		// A record with no certificate body is the node's "not found" shape.
		// Drop it rather than surface an empty entry.
		return records
			.Where(record => record.Certificate1 is not null)
			.Select(DecodeCertificate)
			.ToArray();
	}

	/// <summary>
	/// Gets the certificate that <paramref name="account"/> published under
	/// <paramref name="certificateHash"/> (its
	/// <see cref="Crypto.Certificate.Hash"/>), with its recorded intermediates.
	/// </summary>
	/// <returns>The record, or null when the account never published it.</returns>
	public async Task<Certificate?> GetCertificateByHash(
		Crypto.Account account,
		Crypto.CertificateHash certificateHash,
		CancellationToken cancellationToken = default)
	{
		GetCertificateByHashResponse record = await Attempt(() => Api.GetCertificateByHashAsync(account.PublicKeyString, certificateHash.ToString(), cancellationToken)).ConfigureAwait(false);
		if (record.Certificate1 is null)
		{
			return null;
		}

		return DecodeCertificate(record);
	}

	/// <summary>
	/// Reads the published certificates of <paramref name="account"/> and
	/// evaluates them against <paramref name="trustedIssuers"/> at
	/// <paramref name="moment"/>.
	/// </summary>
	/// <remarks>
	/// The issuers are the only trust anchors. A record's own intermediates
	/// only help complete the chain.
	/// </remarks>
	public async Task<CertificateChainStatus> VerifyAccountCertificateChain(
		Crypto.Account account,
		IReadOnlyList<Crypto.Certificate> trustedIssuers,
		DateTimeOffset moment,
		CancellationToken cancellationToken = default)
	{
		IReadOnlyList<Certificate> records = await GetAllCertificates(account, cancellationToken).ConfigureAwait(false);
		return EvaluateCertificateChain(records, trustedIssuers, moment);
	}

	/// <summary>
	/// Evaluates already-fetched <paramref name="records"/> against
	/// <paramref name="trustedIssuers"/> at <paramref name="moment"/>.
	/// </summary>
	/// <remarks>
	/// A record that does not parse is skipped and never trusted. Skipped
	/// records still count as published, so an account whose every record is
	/// malformed reports <see cref="CertificateChainStatus.Untrusted"/>, not
	/// <see cref="CertificateChainStatus.NoCerts"/>.
	/// </remarks>
	public CertificateChainStatus EvaluateCertificateChain(
		IReadOnlyList<Certificate> records,
		IReadOnlyList<Crypto.Certificate> trustedIssuers,
		DateTimeOffset moment)
	{
		if (records.Count == 0)
		{
			return CertificateChainStatus.NoCerts;
		}

		if (records.Any(record => RecordChainsToRoot(record, trustedIssuers, moment)))
		{
			return CertificateChainStatus.Trusted;
		}

		return CertificateChainStatus.Untrusted;
	}

	/// <summary>
	/// Releases the base token account and, when not injected, the owned
	/// <see cref="HttpClient"/>.
	/// </summary>
	public void Dispose()
	{
		_baseToken?.Dispose();
		_ownedHttp?.Dispose();
	}

	/// <summary>Returns the bound network and its base token, which the write path requires.</summary>
	private (long Network, Crypto.Account BaseToken) RequireNetwork()
	{
		if (_network is not { } network || _baseToken is null)
		{
			throw new KeetaException("NETWORK_REQUIRED", "bind a network id when creating the node client to build or transmit blocks");
		}

		return (network, _baseToken);
	}

	/// <summary>Returns the block's transport bytes in the base64 form that the vote endpoint carries.</summary>
	private static string EncodeBlock(Crypto.Block block) => Convert.ToBase64String(block.ToBytes());

	/// <summary>
	/// Requests non-binding vote quotes for <paramref name="blocks"/> from
	/// every representative.
	/// </summary>
	/// <returns>One quote per representative that answered.</returns>
	/// <remarks>
	/// Each quote locks in the fee that its issuer would charge. Attach the
	/// quotes to a transmit through <see cref="TransmitOptions.Quotes"/>.
	/// Individual failures are tolerated. When no representative answers,
	/// the failure of the highest-weight one surfaces.
	/// </remarks>
	public async Task<IReadOnlyList<VoteQuote>> GetVoteQuotes(
		IReadOnlyList<Crypto.Block> blocks,
		CancellationToken cancellationToken = default)
	{
		await RefreshRepresentatives(cancellationToken).ConfigureAwait(false);

		var body = new Body2 { Blocks = blocks.Select(EncodeBlock).ToList() };
		var requests = SnapshotRepresentatives()
			.Select(rep => RequestQuoteFrom(rep, body, cancellationToken))
			.ToArray();
		(Representative Rep, byte[]? Value, KeetaException? Error)[] outcomes =
			await Task.WhenAll(requests).ConfigureAwait(false);

		return SuccessesOrThrow(outcomes, "no representative returned a vote quote")
			.Select(success => new VoteQuote(success.Value, success.Rep.Endpoint.ApiUrl))
			.ToArray();
	}

	/// <summary>Requests one representative's quote and captures its failure rather than throwing.</summary>
	private static async Task<(Representative Rep, byte[]? Value, KeetaException? Error)> RequestQuoteFrom(
		Representative representative,
		Body2 body,
		CancellationToken cancellationToken)
	{
		try
		{
			CreateVoteQuoteResponse response = await Attempt(() => representative.Api.CreateVoteQuoteAsync(body, cancellationToken)).ConfigureAwait(false);
			string? quote = response.Quote?.Binary;
			if (string.IsNullOrEmpty(quote))
			{
				throw new KeetaException("VOTE_DECLINED", "the node returned no vote quote");
			}

			return (representative, Convert.FromBase64String(quote), null);
		}
		catch (KeetaException error)
		{
			return (representative, null, error);
		}
	}

	/// <summary>
	/// Requests votes over <paramref name="blocksBase64"/> from the
	/// representative set concurrently.
	/// </summary>
	/// <remarks>
	/// Round one leaves <paramref name="priorVotes"/> null and fans out to
	/// every representative. Round two attaches the full temporary set and
	/// contacts only its issuers, which the node requires to escalate.
	/// Individual failures are tolerated. When no representative votes, the
	/// failure of the highest-weight one surfaces. Each quote in
	/// <paramref name="quotes"/> goes only to the representative that issued
	/// it, matched by its API URL.
	/// </remarks>
	private async Task<IReadOnlyList<RepresentativeVote>> RequestVotes(
		IReadOnlyList<string> blocksBase64,
		IReadOnlyList<RepresentativeVote>? priorVotes,
		IReadOnlyList<VoteQuote>? quotes,
		CancellationToken cancellationToken)
	{
		IReadOnlyList<Representative> targets = priorVotes is null
			? SnapshotRepresentatives()
			: priorVotes.Select(prior => prior.Issuer).ToArray();

		var quotesByIssuer = new Dictionary<string, VoteQuote>();
		foreach (VoteQuote quote in quotes ?? Array.Empty<VoteQuote>())
		{
			quotesByIssuer[quote.IssuerApiUrl] = quote;
		}

		var requests = targets
			.Select(rep => RequestVoteFrom(
				rep,
				blocksBase64,
				priorVotes,
				quotesByIssuer.GetValueOrDefault(rep.Endpoint.ApiUrl)?.Bytes,
				cancellationToken))
			.ToArray();
		(Representative Rep, string? Value, KeetaException? Error)[] outcomes =
			await Task.WhenAll(requests).ConfigureAwait(false);

		return SuccessesOrThrow(outcomes, "no representative returned a vote")
			.Select(success => new RepresentativeVote(success.Rep, success.Value))
			.ToArray();
	}

	/// <summary>
	/// Splits fan-out outcomes into their successes.
	/// </summary>
	/// <remarks>
	/// When every request failed, the failure of the highest-weight
	/// representative surfaces, or a <c>VOTE_DECLINED</c> with
	/// <paramref name="emptyMessage"/> when no failure was captured.
	/// </remarks>
	private static List<(Representative Rep, T Value)> SuccessesOrThrow<T>(
		IReadOnlyList<(Representative Rep, T? Value, KeetaException? Error)> outcomes,
		string emptyMessage)
		where T : class
	{
		var successes = new List<(Representative, T)>(outcomes.Count);
		KeetaException? highestError = null;
		BigInteger highestErrorWeight = BigInteger.MinusOne;
		foreach ((Representative rep, T? value, KeetaException? error) in outcomes)
		{
			if (value is not null)
			{
				successes.Add((rep, value));
				continue;
			}

			BigInteger weight = rep.Weight ?? BigInteger.MinusOne;
			if (error is not null && (highestError is null || weight > highestErrorWeight))
			{
				highestError = error;
				highestErrorWeight = weight;
			}
		}

		if (successes.Count == 0)
		{
			throw highestError ?? new KeetaException("VOTE_DECLINED", emptyMessage);
		}

		return successes;
	}

	/// <summary>Requests one representative's vote and captures its failure rather than throwing.</summary>
	private static async Task<(Representative Rep, string? Vote, KeetaException? Error)> RequestVoteFrom(
		Representative representative,
		IReadOnlyList<string> blocksBase64,
		IReadOnlyList<RepresentativeVote>? priorVotes,
		byte[]? quote,
		CancellationToken cancellationToken)
	{
		var body = new Body { Blocks = blocksBase64.ToList() };
		if (priorVotes is not null)
		{
			// Every contacted representative receives the full temporary set,
			// including its own vote, which the node requires to escalate.
			body.Votes = priorVotes.Select(prior => prior.VoteBase64).ToList();
		}

		if (quote is not null)
		{
			body.Quote = Convert.ToBase64String(quote);
		}

		try
		{
			CreateVoteResponse response = await Attempt(() => representative.Api.CreateVoteAsync(body, cancellationToken)).ConfigureAwait(false);
			string? vote = response.Vote?.Binary;
			if (string.IsNullOrEmpty(vote))
			{
				throw new KeetaException("VOTE_DECLINED", "the node returned no vote");
			}

			return (representative, vote, null);
		}
		catch (KeetaException error)
		{
			return (representative, null, error);
		}
	}

	/// <summary>Materializes a base64 vote from the vote endpoint.</summary>
	private Crypto.Vote DecodeVote(string voteBase64) =>
		new(_runtime, _runtime.VoteFromBytes(Convert.FromBase64String(voteBase64)));

	/// <summary>
	/// Returns whether any of the round's votes obliges a fee block. A vote
	/// with a zero-amount option does not.
	/// </summary>
	private bool VotesRequireFee(IReadOnlyList<RepresentativeVote> votes)
	{
		foreach (RepresentativeVote entry in votes)
		{
			using Crypto.Vote vote = DecodeVote(entry.VoteBase64);
			if (vote.RequiresFee)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Produces the fee block that the temporary round requires through the
	/// caller's factory. The factory receives the validated staple over
	/// <paramref name="blocks"/> and the round's <paramref name="temporaryVotes"/>.
	/// </summary>
	private async Task<Crypto.Block> FeeBlockFor(
		IReadOnlyList<Crypto.Block> blocks,
		IReadOnlyList<RepresentativeVote> temporaryVotes,
		TransmitOptions options,
		CancellationToken cancellationToken)
	{
		if (options.FeeBlockFactory is not { } factory)
		{
			throw new KeetaException("FEE_REQUIRED", "the votes require a fee but no fee-block factory is set");
		}

		IReadOnlyList<Crypto.Account> priority = options.FeeTokenPriority.ToArray();
		using Crypto.VoteStaple staple = StapleFor(blocks, temporaryVotes);
		Crypto.Block? feeBlock = await factory(this, staple, priority, cancellationToken).ConfigureAwait(false);
		if (feeBlock is null)
		{
			throw new KeetaException("FEE_REQUIRED", "the votes require a fee but the fee-block factory produced none");
		}

		return feeBlock;
	}

	/// <summary>
	/// Builds a validated staple over <paramref name="blocks"/> and the
	/// base64 votes that endorse them. The build enforces the staple invariants.
	/// </summary>
	private Crypto.VoteStaple StapleFor(IReadOnlyList<Crypto.Block> blocks, IReadOnlyList<RepresentativeVote> votes)
	{
		int[] blockHandles = blocks.Select(block => block.Handle).ToArray();
		long moment = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

		var decoded = new List<Crypto.Vote>(votes.Count);
		try
		{
			foreach (RepresentativeVote entry in votes)
			{
				decoded.Add(DecodeVote(entry.VoteBase64));
			}

			int[] voteHandles = decoded.Select(vote => vote.Handle).ToArray();
			return new Crypto.VoteStaple(_runtime, _runtime.VoteStapleNew(blockHandles, voteHandles, moment));
		}
		finally
		{
			foreach (Crypto.Vote vote in decoded)
			{
				vote.Dispose();
			}
		}
	}

	/// <summary>
	/// Assembles the staple over <paramref name="blocks"/> plus every
	/// permanent vote, and posts it to the representative with the highest
	/// weight.
	/// </summary>
	private async Task<bool> PublishStaple(
		IReadOnlyList<Crypto.Block> blocks,
		IReadOnlyList<RepresentativeVote> permanentVotes,
		CancellationToken cancellationToken)
	{
		byte[] stapleBytes;
		var decoded = new List<Crypto.Vote>(permanentVotes.Count);
		try
		{
			foreach (RepresentativeVote entry in permanentVotes)
			{
				decoded.Add(DecodeVote(entry.VoteBase64));
			}

			int[] blockHandles = blocks.Select(block => block.Handle).ToArray();
			int[] voteHandles = decoded.Select(vote => vote.Handle).ToArray();
			long moment = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			stapleBytes = _runtime.VoteStapleBuild(blockHandles, voteHandles, moment);
		}
		finally
		{
			foreach (Crypto.Vote vote in decoded)
			{
				vote.Dispose();
			}
		}

		var body = new Body3 { VotesAndBlocks = Convert.ToBase64String(stapleBytes) };
		await Attempt(() => Api.PublishVoteStapleAsync(body, cancellationToken)).ConfigureAwait(false);

		// A fulfilled publish means the node accepted the staple. The
		// response's `publish` flag only reports whether the node also voted
		// on it, so the client ignores the flag.
		return true;
	}

	/// <summary>
	/// Positions <paramref name="builder"/> atop <paramref name="previous"/>,
	/// or as an opening block when the account has no chain yet.
	/// </summary>
	internal static void PositionAfter(Crypto.BlockBuilder builder, string? previous)
	{
		if (string.IsNullOrEmpty(previous))
		{
			builder.AsOpening();
			return;
		}

		builder.WithPrevious(Crypto.BlockHash.Parse(previous));
	}

	/// <summary>
	/// Returns whether one published record chains to a trusted issuer at the
	/// moment. A malformed certificate or intermediate makes the record fail closed.
	/// </summary>
	private bool RecordChainsToRoot(
		Certificate record,
		IReadOnlyList<Crypto.Certificate> trustedIssuers,
		DateTimeOffset moment)
	{
		try
		{
			using Crypto.KycCertificate leaf = _runtime.KycCertificates.Parse(record.Value);

			var intermediates = new List<Crypto.Certificate>(record.Intermediates.Count);
			try
			{
				foreach (string pem in record.Intermediates)
				{
					intermediates.Add(_runtime.Certificates.Parse(pem));
				}

				return leaf.Verify(trustedIssuers, intermediates, moment);
			}
			finally
			{
				foreach (Crypto.Certificate intermediate in intermediates)
				{
					intermediate.Dispose();
				}
			}
		}
		catch (KeetaException)
		{
			return false;
		}
	}

	/// <summary>
	/// Runs one generated transport call and projects its failure to a
	/// <see cref="KeetaException"/>. A node error envelope surfaces its own
	/// code (for example <c>LEDGER_SUCCESSOR_VOTE_EXISTS</c>). Anything else
	/// collapses to the stable <c>NODE_STATUS</c> code.
	/// </summary>
	private static async Task<T> Attempt<T>(Func<Task<T>> operation)
	{
		try
		{
			return await operation().ConfigureAwait(false);
		}
		catch (NodeApiException<Error> error) when (!string.IsNullOrEmpty(error.Result?.Code))
		{
			throw new KeetaException(error.Result.Code, error.Result.Message ?? "the node rejected the request", error);
		}
		catch (NodeApiException error)
		{
			throw new KeetaException("NODE_STATUS", $"node request failed with status {error.StatusCode}", error);
		}
	}

	/// <summary>Maps a generated certificate record to the SDK's shared record.</summary>
	private static Certificate DecodeCertificate(GeneratedCertificate record) =>
		new(record.Certificate1, record.Intermediates?.ToArray() ?? Array.Empty<string>());

	/// <summary>
	/// Materializes a transport block (base64 <c>$binary</c>) inside the core.
	/// An absent block field is the node's "none" shape.
	/// </summary>
	private Crypto.Block? DecodeBlock(GeneratedBlock? block)
	{
		if (string.IsNullOrEmpty(block?.Binary))
		{
			return null;
		}

		string hex = Convert.ToHexString(Convert.FromBase64String(block.Binary));
		return _runtime.Blocks.ParseHex(hex);
	}

	/// <summary>Maps generated history entries and the paging cursor to the typed page.</summary>
	private static HistoryPage DecodeHistoryPage(ICollection<GeneratedHistoryEntry>? history, string? nextKey)
	{
		ICollection<GeneratedHistoryEntry> items = history ?? Array.Empty<GeneratedHistoryEntry>();
		var entries = new List<NodeHistoryEntry>(items.Count);
		foreach (GeneratedHistoryEntry item in items)
		{
			string? binary = item.VoteStaple?.Binary;
			if (string.IsNullOrEmpty(binary))
			{
				continue;
			}

			DateTimeOffset? timestamp = null;
			if (!string.IsNullOrEmpty(item.Timestamp))
			{
				timestamp = DateTimeOffset.Parse(item.Timestamp, CultureInfo.InvariantCulture);
			}

			entries.Add(new NodeHistoryEntry(Convert.FromBase64String(binary), OptionalBlockHash(item.Id), timestamp));
		}

		return new HistoryPage(entries, OptionalBlockHash(nextKey));
	}

	/// <summary>
	/// Maps generated ACL rows to typed entries. Each entry carries the
	/// principal by its declared type, the entity and target accounts, and
	/// the <c>[base, external]</c> permission bitmaps decoded through the core.
	/// </summary>
	[SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created",
		Justification = "Ownership of the permission set transfers to the returned Acl entry; the caller disposes it with the entry's accounts, as with every model carrying live handles.")]
	private Acl[] DecodeAcls(ICollection<ACLRow>? rows) =>
		(rows ?? Array.Empty<ACLRow>())
			.Select(row =>
			{
				Crypto.Permissions granted = _runtime.Blocks.PermissionsFromBitmaps(
					row.Permissions?.FirstOrDefault() ?? "0x0",
					row.Permissions?.Skip(1).FirstOrDefault() ?? "0x0");

				return new Acl(
					DecodeAclPrincipal(row.PrincipalType, row.Principal),
					OptionalAccount(row.Entity),
					OptionalAccount(row.Target),
					granted);
			})
			.ToArray();

	/// <summary>
	/// Decodes an ACL principal from its wire shape. The shape is an account
	/// address string when the type is <c>ACCOUNT</c>, or an object with the
	/// issuing certificate hash and its anchor account when <c>CERTIFICATE</c>.
	/// </summary>
	private AclPrincipal? DecodeAclPrincipal(ACLRowPrincipalType kind, object? principal)
	{
		if (principal is not JsonElement value)
		{
			return null;
		}

		if (kind == ACLRowPrincipalType.CERTIFICATE)
		{
			string? hash = value.GetProperty("certificate").GetString();
			string? anchor = value.GetProperty("certificateAccount").GetString();
			if (hash is null || anchor is null)
			{
				throw new KeetaException("ACL_PRINCIPAL", "a certificate principal requires 'certificate' and 'certificateAccount'");
			}

			return new AclCertificatePrincipal(
				Crypto.CertificateHash.Parse(hash),
				_runtime.Accounts.FromPublicKeyString(anchor));
		}

		string? address = value.GetString();
		if (address is null)
		{
			throw new KeetaException("ACL_PRINCIPAL", "an account principal must be an address string");
		}

		return new AclAccountPrincipal(_runtime.Accounts.FromPublicKeyString(address));
	}

	/// <summary>Parses an optional account address field. Returns null when the field is absent.</summary>
	private Crypto.Account? OptionalAccount(string? address) =>
		string.IsNullOrEmpty(address) ? null : _runtime.Accounts.FromPublicKeyString(address);

	/// <summary>Parses an optional hex hash field. Returns null when the field is absent.</summary>
	private static Crypto.BlockHash? OptionalBlockHash(string? hex) =>
		string.IsNullOrEmpty(hex) ? null : Crypto.BlockHash.Parse(hex);

	/// <summary>
	/// Maps one account's generated state fields to the typed
	/// <see cref="AccountState"/>. The single and batch reads share this path.
	/// </summary>
	private AccountState DecodeState(
		string? headBlock,
		string? headHeight,
		string? representative,
		AccountInfo? generatedInfo,
		ICollection<BalanceEntry>? balances)
	{
		NodeAccountInfo? info = null;
		if (generatedInfo is not null)
		{
			info = new NodeAccountInfo(
				generatedInfo.Name,
				generatedInfo.Description,
				generatedInfo.Metadata,
				OptionalHexAmount(generatedInfo.Supply));
		}

		Crypto.Account? delegated = null;
		if (representative is not null)
		{
			delegated = _runtime.Accounts.FromPublicKeyString(representative);
		}

		Crypto.BlockHash? head = null;
		if (headBlock is not null)
		{
			head = Crypto.BlockHash.Parse(headBlock);
		}

		return new AccountState(head, OptionalHexAmount(headHeight), delegated, info, DecodeBalances(balances));
	}

	/// <summary>
	/// Maps a generated representative to the typed model. The plural
	/// endpoint advertises endpoints. The singular lookup does not.
	/// </summary>
	private NodeRepresentative DecodeRepresentative(GeneratedRepresentative representative) =>
		new(
			_runtime.Accounts.FromPublicKeyString(representative.Representative1),
			OptionalHexAmount(representative.Weight) ?? BigInteger.Zero,
			representative.Endpoints?.Api);

	/// <summary>Maps generated balance entries and treats absent amounts as zero.</summary>
	private TokenBalance[] DecodeBalances(ICollection<BalanceEntry>? balances)
	{
		if (balances is null)
		{
			return Array.Empty<TokenBalance>();
		}

		return balances
			.Where(entry => entry.Token is not null)
			.Select(entry => new TokenBalance(
				_runtime.Accounts.FromPublicKeyString(entry.Token),
				OptionalHexAmount(entry.Balance) ?? BigInteger.Zero,
				OptionalHexAmount(entry.Pending) ?? BigInteger.Zero))
			.ToArray();
	}

	/// <summary>Parses a node amount (a 0x-prefixed hexadecimal BigInt). Returns null when the value is absent.</summary>
	private static BigInteger? OptionalHexAmount(string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return null;
		}

		string hex = value;
		if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			hex = hex[2..];
		}

		// A leading zero keeps the parse unsigned: without it a high first
		// nibble would flip BigInteger's two's-complement sign.
		return BigInteger.Parse("0" + hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
	}
}
