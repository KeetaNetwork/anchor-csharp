namespace KeetaNet.Anchor;

/// <summary>
/// One KYC provider bound to its discovering client (the reference provider
/// handle): a metadata snapshot in <see cref="Info"/> plus the verification
/// operations, signed and retried by the client it came from. Obtained from
/// <see cref="KycClient.GetProviders"/> or re-bound from a stored snapshot
/// with <see cref="KycClient.Provider"/>.
/// </summary>
public sealed class KycProvider
{
	private readonly KycClient _client;

	internal KycProvider(KycClient client, KycProviderInfo info)
	{
		_client = client;
		Info = info;
	}

	/// <summary>The provider's advertised metadata snapshot.</summary>
	public KycProviderInfo Info { get; }

	/// <summary>The provider's id.</summary>
	public string Id => Info.Id;

	/// <summary>
	/// Start a verification for <paramref name="countries"/>, optionally
	/// redirecting the user to <paramref name="redirect"/> when the flow ends.
	/// </summary>
	public Task<VerificationOutcome> StartVerification(
		IEnumerable<string> countries,
		string? redirect = null,
		CancellationToken cancellationToken = default) =>
		_client.StartVerification(Info, countries, redirect, cancellationToken);

	/// <summary>Fetch the certificates issued for verification <paramref name="id"/>.</summary>
	public Task<CertificatesOutcome> GetCertificates(string id, CancellationToken cancellationToken = default) =>
		_client.GetCertificates(Info, id, cancellationToken);

	/// <summary>Read the status of verification <paramref name="id"/>.</summary>
	public Task<StatusOutcome> GetVerificationStatus(string id, CancellationToken cancellationToken = default) =>
		_client.GetVerificationStatus(Info, id, cancellationToken);

	/// <summary>Parse this provider's advertised issuer CA certificate.</summary>
	/// <remarks>Use it as a trusted root when verifying an issued <see cref="Crypto.KycCertificate"/>.</remarks>
	public Crypto.Certificate GetCA() => _client.GetCA(Info);
}
