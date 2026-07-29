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
/// The base client for the KeetaNet node API: ledger reads and the two-round
/// transmit flow, over the transport generated from the canonical OpenAPI
/// spec. Account-bound conveniences live on <see cref="UserClient"/>.
/// </summary>
public sealed class KeetaClient : IDisposable
{
	/// <summary>The block version the reference clients build.</summary>
	internal const int BlockVersion = 2;

	private readonly WasmRuntime _runtime;

	/// <summary>The client-owned transport. Null when an injected one is borrowed.</summary>
	private readonly HttpClient? _ownedHttp;

	private readonly NodeApi _api;

	private readonly long? _network;

	/// <summary>The network's base token; derived only when a network is bound.</summary>
	private readonly Crypto.Account? _baseToken;

	/// <summary>
	/// A client for the node API at <paramref name="nodeUrl"/>. An injected
	/// <paramref name="http"/> (for example from <c>IHttpClientFactory</c>) is
	/// borrowed, not disposed. A bound <paramref name="network"/> enables the
	/// write path; without one the client stays read-only.
	/// </summary>
	internal KeetaClient(WasmRuntime runtime, string nodeUrl, HttpClient? http = null, long? network = null)
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

		_api = new NodeApi(http) { BaseUrl = nodeUrl };
	}

	/// <summary>The bound network id, or null for a read-only client.</summary>
	public long? Network => _network;

	/// <summary>
	/// The bound network's base token (the implicit fee currency), or null for
	/// a read-only client. Owned by this client; do not dispose it.
	/// </summary>
	public Crypto.Account? BaseToken => _baseToken;

	/// <summary>The node software version string.</summary>
	public async Task<string> GetNodeVersion(CancellationToken cancellationToken = default)
	{
		GetNodeVersionResponse response = await Attempt(() => _api.GetNodeVersionAsync(cancellationToken)).ConfigureAwait(false);
		return response.Node ?? "";
	}

	/// <summary>
	/// The ledger state of <paramref name="account"/>: head block, delegated
	/// representative, published info, and token balances.
	/// </summary>
	public async Task<AccountState> GetAccountState(
		Crypto.Account account,
		CancellationToken cancellationToken = default)
	{
		GetAccountStateResponse state = await Attempt(() => _api.GetAccountStateAsync(account.PublicKeyString, cancellationToken)).ConfigureAwait(false);
		return DecodeState(state.CurrentHeadBlock, state.CurrentHeadBlockHeight, state.Representative, state.Info, state.Balances);
	}

	/// <summary>
	/// The ledger state of several <paramref name="accounts"/> in one call,
	/// one entry per account in request order.
	/// </summary>
	public async Task<IReadOnlyList<AccountState>> GetAccountStates(
		IReadOnlyList<Crypto.Account> accounts,
		CancellationToken cancellationToken = default)
	{
		string joined = string.Join(",", accounts.Select(account => account.PublicKeyString));
		ICollection<GetAccountStatesResponseItem> states = await Attempt(() => _api.GetAccountStatesAsync(joined, cancellationToken)).ConfigureAwait(false);

		return states
			.Select(item => DecodeState(item.CurrentHeadBlock, item.CurrentHeadBlockHeight, item.Representative, item.Info, item.Balances))
			.ToArray();
	}

	/// <summary>
	/// The total supply of <paramref name="token"/>, read from its account
	/// state. Null for an account that is not a token.
	/// </summary>
	public async Task<BigInteger?> GetTokenSupply(
		Crypto.Account token,
		CancellationToken cancellationToken = default)
	{
		AccountState state = await GetAccountState(token, cancellationToken).ConfigureAwait(false);
		return state.Info?.Supply;
	}

	/// <summary>The point-in-time XOR checksum of the node's ledger.</summary>
	public async Task<LedgerChecksum> GetLedgerChecksum(CancellationToken cancellationToken = default)
	{
		GetLedgerChecksumResponse checksum = await Attempt(() => _api.GetLedgerChecksumAsync(cancellationToken)).ConfigureAwait(false);

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

	/// <summary>The node's own representative.</summary>
	public async Task<NodeRepresentative> GetNodeRepresentative(CancellationToken cancellationToken = default)
	{
		GeneratedRepresentative representative = await Attempt(() => _api.GetNodeRepresentativeAsync(cancellationToken)).ConfigureAwait(false);
		return DecodeRepresentative(representative);
	}

	/// <summary>The named <paramref name="representative"/> and its voting weight.</summary>
	public async Task<NodeRepresentative> GetRepresentative(
		Crypto.Account representative,
		CancellationToken cancellationToken = default)
	{
		GeneratedRepresentative named = await Attempt(() => _api.GetRepresentativeAsync(representative.PublicKeyString, cancellationToken)).ConfigureAwait(false);
		return DecodeRepresentative(named);
	}

	/// <summary>Every representative the node knows, with advertised endpoints.</summary>
	public async Task<IReadOnlyList<NodeRepresentative>> GetAllRepresentatives(CancellationToken cancellationToken = default)
	{
		GetAllRepresentativesResponse response = await Attempt(() => _api.GetAllRepresentativesAsync(cancellationToken)).ConfigureAwait(false);
		ICollection<GeneratedRepresentative> representatives = response.Representatives ?? Array.Empty<GeneratedRepresentative>();

		return representatives.Select(DecodeRepresentative).ToArray();
	}

	/// <summary>Node statistics, as the opaque JSON the reference reports.</summary>
	public async Task<JsonElement> GetNodeStats(CancellationToken cancellationToken = default)
	{
		object stats = await Attempt(() => _api.GetNodeStatsAsync(cancellationToken)).ConfigureAwait(false);
		return (JsonElement)stats;
	}

	/// <summary>Connected peers, as the opaque JSON the reference reports.</summary>
	public async Task<JsonElement> GetNodePeers(CancellationToken cancellationToken = default)
	{
		object peers = await Attempt(() => _api.GetPeersAsync(cancellationToken)).ConfigureAwait(false);
		return (JsonElement)peers;
	}

	/// <summary>Every token balance <paramref name="account"/> holds.</summary>
	public async Task<IReadOnlyList<TokenBalance>> GetAccountBalances(
		Crypto.Account account,
		CancellationToken cancellationToken = default)
	{
		GetAccountBalancesResponse response = await Attempt(() => _api.GetAccountBalancesAsync(account.PublicKeyString, cancellationToken)).ConfigureAwait(false);
		return DecodeBalances(response.Balances);
	}

	/// <summary>The settled balance of <paramref name="account"/> in <paramref name="token"/> base units.</summary>
	public async Task<BigInteger> GetAccountBalance(
		Crypto.Account account,
		Crypto.Account token,
		CancellationToken cancellationToken = default)
	{
		GetAccountBalanceResponse response = await Attempt(() => _api.GetAccountBalanceAsync(account.PublicKeyString, token.PublicKeyString, cancellationToken)).ConfigureAwait(false);
		return OptionalHexAmount(response.Balance) ?? BigInteger.Zero;
	}

	/// <summary>The head block of <paramref name="account"/>'s chain, or null for a never-used account.</summary>
	public async Task<Crypto.Block?> GetHeadBlock(
		Crypto.Account account,
		CancellationToken cancellationToken = default)
	{
		GetAccountHeadResponse response = await Attempt(() => _api.GetAccountHeadAsync(account.PublicKeyString, cancellationToken)).ConfigureAwait(false);
		return DecodeBlock(response.Block);
	}

	/// <summary>The next pending (unreceived) block for <paramref name="account"/>, if any.</summary>
	public async Task<Crypto.Block?> GetPendingBlock(
		Crypto.Account account,
		CancellationToken cancellationToken = default)
	{
		GetPendingBlockResponse response = await Attempt(() => _api.GetPendingBlockAsync(account.PublicKeyString, cancellationToken)).ConfigureAwait(false);
		return DecodeBlock(response.Block);
	}

	/// <summary>The block identified by <paramref name="blockHash"/> on the given <paramref name="side"/>, if present.</summary>
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

		GetBlockResponse response = await Attempt(() => _api.GetBlockAsync(blockHash.ToString(), generated, cancellationToken)).ConfigureAwait(false);
		return DecodeBlock(response.Block);
	}

	/// <summary>The block following <paramref name="blockHash"/>, if one exists.</summary>
	public async Task<Crypto.Block?> GetSuccessorBlock(
		Crypto.BlockHash blockHash,
		CancellationToken cancellationToken = default)
	{
		GetSuccessorBlockResponse response = await Attempt(() => _api.GetSuccessorBlockAsync(blockHash.ToString(), cancellationToken)).ConfigureAwait(false);
		return DecodeBlock(response.SuccessorBlock);
	}

	/// <summary>
	/// The block produced by <paramref name="account"/> for the idempotent
	/// <paramref name="key"/>, if any, searching the given <paramref name="side"/>
	/// (the main ledger when omitted).
	/// </summary>
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

		GetBlockFromIdempotentResponse response = await Attempt(() => _api.GetBlockFromIdempotentAsync(account.PublicKeyString, key, generated, cancellationToken)).ConfigureAwait(false);
		return DecodeBlock(response.Block);
	}

	/// <summary>
	/// The verified votes the node holds for <paramref name="blockHash"/> on
	/// <paramref name="side"/>, or null when it holds none. The caller owns
	/// the votes and must dispose them.
	/// </summary>
	public async Task<IReadOnlyList<Crypto.Vote>?> GetBlockVotes(
		Crypto.BlockHash blockHash,
		LedgerSide side = LedgerSide.Main,
		CancellationToken cancellationToken = default)
	{
		Side generated = side == LedgerSide.Side ? Side.Side : Side.Main;
		GetBlockVotesResponse response = await Attempt(() => _api.GetBlockVotesAsync(blockHash.ToString(), generated, cancellationToken)).ConfigureAwait(false);
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
	/// A single page of <paramref name="account"/>'s block chain (most recent
	/// first), bounded by <paramref name="query"/>, with the cursor for the
	/// next page. The caller owns the blocks and must dispose them.
	/// </summary>
	public async Task<ChainPage> GetAccountChain(
		Crypto.Account account,
		ChainQuery? query = null,
		CancellationToken cancellationToken = default)
	{
		ChainQuery bounds = query ?? new ChainQuery();
		GetAccountChainResponse response = await Attempt(() => _api.GetAccountChainAsync(
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
	/// A single page of <paramref name="account"/>'s committed staple history,
	/// bounded by <paramref name="query"/>, with the cursor for the next page.
	/// </summary>
	public async Task<HistoryPage> GetAccountHistory(
		Crypto.Account account,
		HistoryQuery? query = null,
		CancellationToken cancellationToken = default)
	{
		HistoryQuery bounds = query ?? new HistoryQuery();
		GetAccountHistoryResponse response = await Attempt(() => _api.GetAccountHistoryAsync(
			account.PublicKeyString,
			bounds.Start?.ToString(),
			bounds.Limit,
			cancellationToken)).ConfigureAwait(false);

		return DecodeHistoryPage(response.History, response.NextKey);
	}

	/// <summary>
	/// A single page of the node's global staple history, bounded by
	/// <paramref name="query"/>, with the cursor for the next page.
	/// </summary>
	public async Task<HistoryPage> GetGlobalHistory(
		HistoryQuery? query = null,
		CancellationToken cancellationToken = default)
	{
		HistoryQuery bounds = query ?? new HistoryQuery();
		GetGlobalHistoryResponse response = await Attempt(() => _api.GetGlobalHistoryAsync(
			bounds.Start?.ToString(),
			bounds.Limit,
			cancellationToken)).ConfigureAwait(false);

		return DecodeHistoryPage(response.History, response.NextKey);
	}

	/// <summary>
	/// ACL entries where <paramref name="account"/> is the principal. The
	/// caller owns the returned accounts and permission sets.
	/// </summary>
	public async Task<IReadOnlyList<Acl>> GetAclsByPrincipal(
		Crypto.Account account,
		CancellationToken cancellationToken = default)
	{
		ListAclsByPrincipalResponse response = await Attempt(() => _api.ListAclsByPrincipalAsync(account.PublicKeyString, cancellationToken)).ConfigureAwait(false);
		return DecodeAcls(response.Permissions);
	}

	/// <summary>
	/// ACL entries granted to <paramref name="account"/> as an entity. The
	/// caller owns the returned accounts and permission sets.
	/// </summary>
	public async Task<IReadOnlyList<Acl>> GetAclsByEntity(
		Crypto.Account account,
		CancellationToken cancellationToken = default)
	{
		ListAclsByEntityResponse response = await Attempt(() => _api.ListAclsByEntityAsync(account.PublicKeyString, cancellationToken)).ConfigureAwait(false);
		return DecodeAcls(response.Permissions);
	}

	/// <summary>
	/// A builder pre-set with the reference block version, the bound network,
	/// <paramref name="account"/> as originator, <paramref name="signer"/>
	/// (the account itself when null) signing, and the current moment. The
	/// caller positions it, appends operations, and builds. Requires a bound
	/// network.
	/// </summary>
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

	/// <summary>Publish one signed block as its own staple. See the list overload.</summary>
	public Task<bool> Transmit(
		Crypto.Block block,
		TransmitOptions? options = null,
		CancellationToken cancellationToken = default) =>
		Transmit(new[] { block }, options, cancellationToken);

	/// <summary>
	/// Publish <paramref name="blocks"/> as one atomic staple, the port of the
	/// reference two-round transmit. When the temporary round's votes require
	/// a fee, the factory in <paramref name="options"/> is invoked with that
	/// round and its block joins the permanent round and the staple.
	/// Requires a bound network.
	/// </summary>
	public async Task<bool> Transmit(
		IReadOnlyList<Crypto.Block> blocks,
		TransmitOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		// The whole write path is gated, not just fee construction, so an
		// unbound client keeps its documented read-only guarantee.
		_ = RequireNetwork();

		TransmitOptions resolved = options ?? new TransmitOptions();
		List<string> encoded = blocks.Select(EncodeBlock).ToList();
		string temporary = await RequestVote(encoded, priorVote: null, resolved.Quote, cancellationToken).ConfigureAwait(false);

		Crypto.Block? feeBlock = null;
		try
		{
			if (VoteRequiresFee(temporary))
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

			string permanent = await RequestVote(encoded, temporary, quote: null, cancellationToken).ConfigureAwait(false);
			return await PublishStaple(all, permanent, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			feeBlock?.Dispose();
		}
	}

	/// <summary>
	/// Build and sign the fee block <paramref name="staple"/>'s votes require:
	/// <paramref name="account"/>'s balance pays, <paramref name="signer"/>
	/// signs (distinct under delegated signing). Chains atop the account's
	/// block in the staple, else its ledger head, so the payer need not appear
	/// in the round. Null when no fee is owed. Requires a bound network.
	/// </summary>
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
				AccountState state = await GetAccountState(account, cancellationToken).ConfigureAwait(false);
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
	/// Every certificate <paramref name="account"/> has published on-chain, each
	/// with the intermediates recorded alongside it. An account with no published
	/// certificates yields an empty list.
	/// </summary>
	public async Task<IReadOnlyList<Certificate>> GetAllCertificates(
		Crypto.Account account,
		CancellationToken cancellationToken = default)
	{
		GetAccountCertificatesResponse response = await Attempt(() => _api.GetAccountCertificatesAsync(account.PublicKeyString, cancellationToken)).ConfigureAwait(false);
		ICollection<GeneratedCertificate> records = response.Certificates ?? Array.Empty<GeneratedCertificate>();

		// A record with no certificate body is the node's "not found" shape.
		// Drop it rather than surface an empty entry, as the reference does.
		return records
			.Where(record => record.Certificate1 is not null)
			.Select(DecodeCertificate)
			.ToArray();
	}

	/// <summary>
	/// The certificate <paramref name="account"/> published under
	/// <paramref name="certificateHash"/> (its <see cref="Crypto.Certificate.Hash"/>),
	/// with its recorded intermediates. Null when the account never published it.
	/// </summary>
	public async Task<Certificate?> GetCertificateByHash(
		Crypto.Account account,
		Crypto.CertificateHash certificateHash,
		CancellationToken cancellationToken = default)
	{
		GetCertificateByHashResponse record = await Attempt(() => _api.GetCertificateByHashAsync(account.PublicKeyString, certificateHash.ToString(), cancellationToken)).ConfigureAwait(false);
		if (record.Certificate1 is null)
		{
			return null;
		}

		return DecodeCertificate(record);
	}

	/// <summary>
	/// Read <paramref name="account"/>'s published certificates and evaluate
	/// them against <paramref name="trustedIssuers"/> at <paramref name="moment"/>,
	/// the port of the reference <c>verifyAccountCertificateChain</c>. The
	/// issuers are the only trust anchors. A record's own intermediates just
	/// help bridge the chain.
	/// </summary>
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
	/// Evaluate already-fetched <paramref name="records"/> against
	/// <paramref name="trustedIssuers"/> at <paramref name="moment"/>.
	/// A record that does not parse is skipped, never trusted. Skipped records
	/// still count as published, so an account whose every record is malformed
	/// reports <see cref="CertificateChainStatus.Untrusted"/>, not
	/// <see cref="CertificateChainStatus.NoCerts"/>.
	/// </summary>
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
	/// Release the resources the client owns: its base token account and, when
	/// not injected, its <see cref="HttpClient"/>.
	/// </summary>
	public void Dispose()
	{
		_baseToken?.Dispose();
		_ownedHttp?.Dispose();
	}

	/// <summary>The bound network and its base token, required by the write path.</summary>
	private (long Network, Crypto.Account BaseToken) RequireNetwork()
	{
		if (_network is not { } network || _baseToken is null)
		{
			throw new KeetaException("NETWORK_REQUIRED", "bind a network id when creating the node client to build or transmit blocks");
		}

		return (network, _baseToken);
	}

	/// <summary>A block's transport bytes in the base64 form the vote endpoint carries.</summary>
	private static string EncodeBlock(Crypto.Block block) => Convert.ToBase64String(block.ToBytes());

	/// <summary>
	/// Request a non-binding vote quote for <paramref name="blocks"/>, locking
	/// in the fee the node would charge. Attach it to a transmit through
	/// <see cref="TransmitOptions.Quote"/>.
	/// </summary>
	public async Task<byte[]> GetVoteQuote(
		IReadOnlyList<Crypto.Block> blocks,
		CancellationToken cancellationToken = default)
	{
		var body = new Body2 { Blocks = blocks.Select(EncodeBlock).ToList() };
		CreateVoteQuoteResponse response = await Attempt(() => _api.CreateVoteQuoteAsync(body, cancellationToken)).ConfigureAwait(false);

		string? quote = response.Quote?.Binary;
		if (string.IsNullOrEmpty(quote))
		{
			throw new KeetaException("VOTE_DECLINED", "the node returned no vote quote");
		}

		return Convert.FromBase64String(quote);
	}

	/// <summary>
	/// Request one vote over <paramref name="blocksBase64"/>. Round one leaves
	/// <paramref name="priorVote"/> null so the body omits <c>votes</c> entirely,
	/// and may attach a pre-fetched <paramref name="quote"/>. Round two attaches
	/// the temporary vote so the representative escalates it.
	/// </summary>
	private async Task<string> RequestVote(
		IReadOnlyList<string> blocksBase64,
		string? priorVote,
		byte[]? quote,
		CancellationToken cancellationToken)
	{
		var body = new Body { Blocks = blocksBase64.ToList() };
		if (priorVote is not null)
		{
			body.Votes = new List<string> { priorVote };
		}

		if (quote is not null)
		{
			body.Quote = Convert.ToBase64String(quote);
		}

		CreateVoteResponse response = await Attempt(() => _api.CreateVoteAsync(body, cancellationToken)).ConfigureAwait(false);
		string? vote = response.Vote?.Binary;
		if (string.IsNullOrEmpty(vote))
		{
			throw new KeetaException("VOTE_DECLINED", "the node returned no vote");
		}

		return vote;
	}

	/// <summary>Materialize a base64 vote from the vote endpoint.</summary>
	private Crypto.Vote DecodeVote(string voteBase64) =>
		new(_runtime, _runtime.VoteFromBytes(Convert.FromBase64String(voteBase64)));

	/// <summary>Whether the base64 vote obliges a fee block.</summary>
	private bool VoteRequiresFee(string voteBase64)
	{
		using Crypto.Vote vote = DecodeVote(voteBase64);
		return vote.RequiresFee;
	}

	/// <summary>
	/// Produce the fee block the temporary round requires through the
	/// caller's factory, handing it the validated staple over
	/// <paramref name="blocks"/> and <paramref name="temporaryVote"/>.
	/// </summary>
	private async Task<Crypto.Block> FeeBlockFor(
		IReadOnlyList<Crypto.Block> blocks,
		string temporaryVote,
		TransmitOptions options,
		CancellationToken cancellationToken)
	{
		if (options.FeeBlockFactory is not { } factory)
		{
			throw new KeetaException("FEE_REQUIRED", "the votes require a fee but no fee-block factory is set");
		}

		IReadOnlyList<Crypto.Account> priority = options.FeeTokenPriority.ToArray();
		using Crypto.VoteStaple staple = StapleFor(blocks, temporaryVote);
		Crypto.Block? feeBlock = await factory(this, staple, priority, cancellationToken).ConfigureAwait(false);
		if (feeBlock is null)
		{
			throw new KeetaException("FEE_REQUIRED", "the votes require a fee but the fee-block factory produced none");
		}

		return feeBlock;
	}

	/// <summary>
	/// A validated staple over <paramref name="blocks"/> and the base64 vote
	/// endorsing them, enforcing the staple invariants.
	/// </summary>
	private Crypto.VoteStaple StapleFor(IReadOnlyList<Crypto.Block> blocks, string voteBase64)
	{
		using Crypto.Vote vote = DecodeVote(voteBase64);
		int[] blockHandles = blocks.Select(block => block.Handle).ToArray();
		long moment = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

		return new Crypto.VoteStaple(_runtime, _runtime.VoteStapleNew(blockHandles, new[] { vote.Handle }, moment));
	}

	/// <summary>Assemble the staple over <paramref name="blocks"/> plus the permanent vote, and post it.</summary>
	private async Task<bool> PublishStaple(
		IReadOnlyList<Crypto.Block> blocks,
		string permanentVoteBase64,
		CancellationToken cancellationToken)
	{
		byte[] stapleBytes;
		using (Crypto.Vote vote = DecodeVote(permanentVoteBase64))
		{
			int[] blockHandles = blocks.Select(block => block.Handle).ToArray();
			long moment = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			stapleBytes = _runtime.VoteStapleBuild(blockHandles, new[] { vote.Handle }, moment);
		}

		var body = new Body3 { VotesAndBlocks = Convert.ToBase64String(stapleBytes) };
		await Attempt(() => _api.PublishVoteStapleAsync(body, cancellationToken)).ConfigureAwait(false);

		// A fulfilled publish means the node accepted the staple. Its
		// `publish` flag only reports whether the node also voted on it, so
		// the reference clients ignore it and so do we.
		return true;
	}

	/// <summary>
	/// Position <paramref name="builder"/> atop <paramref name="previous"/>, or
	/// as an opening block when the account has no chain yet.
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
	/// Whether one published record chains to a trusted issuer at the moment.
	/// A malformed certificate or intermediate makes the record fail closed.
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
	/// Run one generated transport call, projecting its failure to a
	/// <see cref="KeetaException"/>. A node error envelope surfaces its own
	/// code (for example <c>LEDGER_SUCCESSOR_VOTE_EXISTS</c>); anything else
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

	/// <summary>Map a generated certificate record to the SDK's shared record.</summary>
	private static Certificate DecodeCertificate(GeneratedCertificate record) =>
		new(record.Certificate1, record.Intermediates?.ToArray() ?? Array.Empty<string>());

	/// <summary>
	/// Materialize a transport block (base64 <c>$binary</c>) inside the core.
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

	/// <summary>Map generated history entries and the paging cursor to the typed page.</summary>
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
	/// Map generated ACL rows to typed entries: each principal by its declared
	/// type, the entity/target accounts, and the <c>[base, external]</c>
	/// permission bitmaps decoded through the core.
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
	/// Decode an ACL principal from its wire shape: an account address string
	/// when the type is <c>ACCOUNT</c>, or an object carrying the issuing
	/// certificate hash and its anchor account when <c>CERTIFICATE</c>.
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

	/// <summary>Parse an optional account address field, null when absent.</summary>
	private Crypto.Account? OptionalAccount(string? address) =>
		string.IsNullOrEmpty(address) ? null : _runtime.Accounts.FromPublicKeyString(address);

	/// <summary>Parse an optional hex hash field, null when absent.</summary>
	private static Crypto.BlockHash? OptionalBlockHash(string? hex) =>
		string.IsNullOrEmpty(hex) ? null : Crypto.BlockHash.Parse(hex);

	/// <summary>
	/// Map one account's generated state fields to the typed
	/// <see cref="AccountState"/>, shared by the single and batch reads.
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
	/// Map a generated representative to the typed model. The plural endpoint
	/// advertises endpoints; the singular lookup does not.
	/// </summary>
	private NodeRepresentative DecodeRepresentative(GeneratedRepresentative representative) =>
		new(
			_runtime.Accounts.FromPublicKeyString(representative.Representative1),
			OptionalHexAmount(representative.Weight) ?? BigInteger.Zero,
			representative.Endpoints?.Api);

	/// <summary>Map generated balance entries, treating absent amounts as zero.</summary>
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

	/// <summary>Parse a node amount (a 0x-prefixed hexadecimal BigInt), null when absent.</summary>
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
