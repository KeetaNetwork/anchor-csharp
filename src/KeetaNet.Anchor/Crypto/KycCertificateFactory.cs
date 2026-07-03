namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// Parses and issues <see cref="KycCertificate"/> leaves owned by one runtime.
/// Reached through <see cref="WasmRuntime.KycCertificates"/>.
/// </summary>
public sealed class KycCertificateFactory
{
	private readonly WasmRuntime _runtime;

	internal KycCertificateFactory(WasmRuntime runtime) => _runtime = runtime;

	/// <summary>Parse a PEM-encoded KYC certificate.</summary>
	public KycCertificate Parse(string pem)
	{
		int handle = _runtime.KycCertificateParse(pem);
		return KycCertificate.Adopt(_runtime, handle);
	}

	/// <summary>Begin issuing a new KYC leaf certificate.</summary>
	public KycCertificateBuilder Builder() => new(_runtime);
}
