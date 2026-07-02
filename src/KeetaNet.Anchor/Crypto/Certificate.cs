namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// A base X.509 certificate: a provider CA, a trust root, or an intermediate.
/// </summary>
public sealed class Certificate : IDisposable
{
	private readonly WasmRuntime _runtime;
	private bool _disposed;

	/// <summary>The core-module handle backing this certificate.</summary>
	internal int Handle { get; }

	internal Certificate(WasmRuntime runtime, int handle)
	{
		_runtime = runtime;
		Handle = handle;
	}

	/// <summary>Parse a PEM-encoded certificate.</summary>
	public static Certificate Parse(WasmRuntime runtime, string pem)
	{
		int handle = runtime.CertificateParse(pem);
		return new(runtime, handle);
	}

	/// <summary>Parse a DER-encoded certificate.</summary>
	public static Certificate ParseDer(WasmRuntime runtime, byte[] der)
	{
		int handle = runtime.CertificateParseDer(der);
		return new(runtime, handle);
	}

	/// <summary>The PEM encoding of the certificate.</summary>
	public string Pem() => _runtime.CertificatePem(Handle);

	/// <summary>The DER encoding of the certificate.</summary>
	public byte[] Der() => _runtime.CertificateDer(Handle);

	/// <summary>Whether the certificate is valid at <paramref name="moment"/>.</summary>
	public bool ValidAt(DateTimeOffset moment)
	{
		long unixMillis = moment.ToUnixTimeMilliseconds();
		return _runtime.CertificateValidAt(Handle, unixMillis);
	}

	/// <summary>The subject distinguished name as an RFC 4514 string.</summary>
	public string Subject => _runtime.CertificateSubject(Handle);

	/// <summary>The issuer distinguished name as an RFC 4514 string.</summary>
	public string Issuer => _runtime.CertificateIssuer(Handle);

	/// <summary>The serial number as a base-10 string.</summary>
	public string Serial => _runtime.CertificateSerial(Handle);

	/// <summary>The start of the validity window.</summary>
	public DateTimeOffset NotBefore
	{
		get
		{
			long unixSeconds = _runtime.CertificateNotBefore(Handle);
			return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
		}
	}

	/// <summary>The end of the validity window.</summary>
	public DateTimeOffset NotAfter
	{
		get
		{
			long unixSeconds = _runtime.CertificateNotAfter(Handle);
			return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
		}
	}

	/// <summary>
	/// The subject public key, type-prefixed and hex-encoded to match
	/// <see cref="Account.PublicKey"/>, so a subject can be matched to an account.
	/// </summary>
	public string SubjectPublicKey => _runtime.CertificateSubjectPublicKey(Handle);

	/// <summary>Release the core-module certificate handle.</summary>
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_runtime.CertificateFree(Handle);
	}
}
