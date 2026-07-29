namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// A base X.509 certificate: a provider CA, a trust root, or an intermediate.
/// </summary>
public sealed class Certificate : WasmObject
{
	internal Certificate(WasmRuntime runtime, int handle)
		: base(runtime, handle)
	{
	}

	/// <summary>The PEM encoding of the certificate.</summary>
	public string ToPem() => Runtime.CertificatePem(Handle);

	/// <summary>The DER encoding of the certificate.</summary>
	public byte[] ToDer() => Runtime.CertificateDer(Handle);

	/// <summary>Whether the certificate is valid at <paramref name="moment"/>.</summary>
	public bool IsValidAt(DateTimeOffset moment)
	{
		long unixMillis = moment.ToUnixTimeMilliseconds();
		return Runtime.CertificateValidAt(Handle, unixMillis);
	}

	/// <summary>The subject distinguished name as an RFC 4514 string.</summary>
	public string Subject => Runtime.CertificateSubject(Handle);

	/// <summary>The issuer distinguished name as an RFC 4514 string.</summary>
	public string Issuer => Runtime.CertificateIssuer(Handle);

	/// <summary>The serial number as a base-10 string.</summary>
	public string Serial => Runtime.CertificateSerial(Handle);

	/// <summary>The start of the validity window.</summary>
	public DateTimeOffset NotBefore
	{
		get
		{
			long unixSeconds = Runtime.CertificateNotBefore(Handle);
			return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
		}
	}

	/// <summary>The end of the validity window.</summary>
	public DateTimeOffset NotAfter
	{
		get
		{
			long unixSeconds = Runtime.CertificateNotAfter(Handle);
			return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
		}
	}

	/// <summary>
	/// The subject public key, type-prefixed and hex-encoded to match
	/// <see cref="Account.PublicKeyAndType"/>, so a subject can be matched to an account.
	/// </summary>
	public string SubjectPublicKey => Runtime.CertificateSubjectPublicKey(Handle);

	/// <summary>
	/// The SHA3-256 of the certificate's DER: the key the ledger stores a
	/// published certificate under, so it feeds
	/// <see cref="KeetaClient.GetCertificateByHash(Account, CertificateHash, CancellationToken)"/>.
	/// </summary>
	public CertificateHash Hash => CertificateHash.Parse(Runtime.CertificateHash(Convert.ToHexString(ToDer())));

	private protected override void Release(WasmRuntime runtime, int handle) => runtime.CertificateFree(handle);
}
