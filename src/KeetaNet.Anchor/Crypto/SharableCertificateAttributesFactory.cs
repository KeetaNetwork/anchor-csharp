namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// Creates <see cref="SharableCertificateAttributes"/> bundles owned by one
/// runtime. Reached through <see cref="WasmRuntime.Sharables"/>.
/// </summary>
public sealed class SharableCertificateAttributesFactory
{
	private readonly WasmRuntime _runtime;

	internal SharableCertificateAttributesFactory(WasmRuntime runtime) => _runtime = runtime;

	/// <summary>
	/// Prove or copy each attribute in <paramref name="names"/> from
	/// <paramref name="certificate"/> using the <paramref name="subject"/> account,
	/// bridging the trust chain with <paramref name="intermediates"/>, and seal the
	/// result. Grant a recipient before exporting.
	/// </summary>
	public SharableCertificateAttributes FromCertificate(
		KycCertificate certificate,
		Account subject,
		IEnumerable<Certificate>? intermediates = null,
		IEnumerable<string>? names = null)
	{
		int[] bridges = Handles.Of(intermediates);
		string[] labels = (names ?? Enumerable.Empty<string>()).ToArray();
		int handle = _runtime.SharableFromCertificate(certificate.Handle, subject.Handle, bridges, labels);

		return new(_runtime, handle);
	}

	/// <summary>Open a bundle from encoded container bytes, resolved with <paramref name="principals"/>.</summary>
	public SharableCertificateAttributes FromEncoded(byte[] data, IEnumerable<Account>? principals = null)
	{
		int[] handles = Handles.Of(principals);
		int handle = _runtime.SharableFromEncoded(data, handles);

		return new(_runtime, handle);
	}

	/// <summary>Open a bundle from a PEM envelope, resolved with <paramref name="principals"/>.</summary>
	public SharableCertificateAttributes FromPem(string pem, IEnumerable<Account>? principals = null)
	{
		int[] handles = Handles.Of(principals);
		int handle = _runtime.SharableFromPem(pem, handles);

		return new(_runtime, handle);
	}
}
