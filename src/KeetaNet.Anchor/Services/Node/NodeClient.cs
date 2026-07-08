using System.Globalization;
using System.Numerics;

using KeetaNet.Anchor.Generated.Node;

using GeneratedCertificate = KeetaNet.Anchor.Generated.Node.Certificate;

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
		Response4 response = await Attempt(() => _api.GetNodeVersionAsync(cancellationToken)).ConfigureAwait(false);
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
		Response5 state = await Attempt(() => _api.GetAccountStateAsync(account.Address, cancellationToken)).ConfigureAwait(false);

		NodeAccountInfo? info = null;
		if (state.Info is not null)
		{
			info = new NodeAccountInfo(
				state.Info.Name,
				state.Info.Description,
				state.Info.Metadata,
				OptionalHexAmount(state.Info.Supply));
		}

		Crypto.Account? representative = null;
		if (state.Representative is not null)
		{
			representative = _runtime.Accounts.FromAccount(state.Representative);
		}

		Crypto.BlockHash? headBlock = null;
		if (state.CurrentHeadBlock is not null)
		{
			headBlock = Crypto.BlockHash.Parse(state.CurrentHeadBlock);
		}

		return new AccountState(
			headBlock,
			OptionalHexAmount(state.CurrentHeadBlockHeight),
			representative,
			info,
			DecodeBalances(state.Balances));
	}

	/// <summary>Every token balance <paramref name="account"/> holds.</summary>
	public async Task<IReadOnlyList<TokenBalance>> GetAccountBalances(
		Crypto.Account account,
		CancellationToken cancellationToken = default)
	{
		Response6 response = await Attempt(() => _api.GetAccountBalancesAsync(account.Address, cancellationToken)).ConfigureAwait(false);
		return DecodeBalances(response.Balances);
	}

	/// <summary>The settled balance of <paramref name="account"/> in <paramref name="token"/> base units.</summary>
	public async Task<BigInteger> GetAccountBalance(
		Crypto.Account account,
		Crypto.Account token,
		CancellationToken cancellationToken = default)
	{
		Response7 response = await Attempt(() => _api.GetAccountBalanceAsync(account.Address, token.Address, cancellationToken)).ConfigureAwait(false);
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
		Response19 response = await Attempt(() => _api.GetAccountCertificatesAsync(account.Address, cancellationToken)).ConfigureAwait(false);
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
		Response20 record = await Attempt(() => _api.GetCertificateByHashAsync(account.Address, certificateHash.ToString(), cancellationToken)).ConfigureAwait(false);
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
				_runtime.Accounts.FromAccount(entry.Token),
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
