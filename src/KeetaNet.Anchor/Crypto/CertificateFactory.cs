namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// Parses base X.509 <see cref="Certificate"/> objects owned by one runtime.
/// Reached through <see cref="WasmRuntime.Certificates"/>.
/// </summary>
public sealed class CertificateFactory
{
	private readonly WasmRuntime _runtime;

	internal CertificateFactory(WasmRuntime runtime) => _runtime = runtime;

	/// <summary>Parse a PEM-encoded certificate.</summary>
	public Certificate Parse(string pem)
	{
		int handle = _runtime.CertificateParse(pem);
		return new(_runtime, handle);
	}

	/// <summary>Parse a DER-encoded certificate.</summary>
	public Certificate ParseDer(byte[] der)
	{
		int handle = _runtime.CertificateParseDer(der);
		return new(_runtime, handle);
	}
}
