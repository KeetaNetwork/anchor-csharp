using System.Globalization;
using System.Numerics;
using System.Text.Json;

using KeetaNet.Anchor.Generated.Node;

using GeneratedCertificate = KeetaNet.Anchor.Generated.Node.Certificate;
using GeneratedRepresentative = KeetaNet.Anchor.Generated.Node.Representative;

namespace KeetaNet.Anchor;

/// <summary>
/// A lite, read-only client for the KeetaNet node API: the ledger reads the
/// reference node client performs, over the transport generated from the
/// canonical OpenAPI spec.
/// </summary>
public sealed class NodeClient : IDisposable
{
	private readonly WasmRuntime _runtime;

	/// <summary>The client-owned transport. Null when an injected one is borrowed.</summary>
	private readonly HttpClient? _ownedHttp;

	private readonly NodeApi _api;

	/// <summary>
	/// A client for the node API at <paramref name="nodeUrl"/>. An injected
	/// <paramref name="http"/> (for example from <c>IHttpClientFactory</c>) is
	/// borrowed, not disposed.
	/// </summary>
	internal NodeClient(WasmRuntime runtime, string nodeUrl, HttpClient? http = null)
	{
		_runtime = runtime;
		if (http is null)
		{
			_ownedHttp = new HttpClient();
			http = _ownedHttp;
		}

		_api = new NodeApi(http) { BaseUrl = nodeUrl };
	}

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

	/// <summary>Release the HTTP resources the client owns. An injected <see cref="HttpClient"/> is left alone.</summary>
	public void Dispose() => _ownedHttp?.Dispose();

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
	/// <see cref="KeetaException"/> with the stable <c>NODE_STATUS</c> code.
	/// </summary>
	private static async Task<T> Attempt<T>(Func<Task<T>> operation)
	{
		try
		{
			return await operation().ConfigureAwait(false);
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
