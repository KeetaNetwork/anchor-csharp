using System.Text.Json;

namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// A sealed, selectively disclosed subset of a KYC certificate's attributes.
/// The bundle and its derived state live inside the wasm core; this wrapper
/// holds only the handle and releases it on <see cref="Dispose"/>.
/// </summary>
public sealed class SharableCertificateAttributes : IDisposable
{
	private readonly WasmRuntime _runtime;
	private bool _disposed;

	/// <summary>The core-module handle backing this bundle.</summary>
	internal int Handle { get; }

	private SharableCertificateAttributes(WasmRuntime runtime, int handle)
	{
		_runtime = runtime;
		Handle = handle;
	}

	/// <summary>
	/// Prove or copy each attribute in <paramref name="names"/> from
	/// <paramref name="certificate"/> using the <paramref name="subject"/> account,
	/// bridging the trust chain with <paramref name="intermediates"/>, and seal the
	/// result. Grant a recipient before exporting.
	/// </summary>
	public static SharableCertificateAttributes FromCertificate(
		WasmRuntime runtime,
		KycCertificate certificate,
		Account subject,
		IEnumerable<Certificate>? intermediates = null,
		IEnumerable<string>? names = null)
	{
		int[] bridges = Handles.Of(intermediates);
		string[] labels = (names ?? Enumerable.Empty<string>()).ToArray();
		int handle = runtime.SharableFromCertificate(certificate.Handle, subject.Handle, bridges, labels);

		return new(runtime, handle);
	}

	/// <summary>Open a bundle from encoded container bytes, resolved with <paramref name="principals"/>.</summary>
	public static SharableCertificateAttributes FromEncoded(
		WasmRuntime runtime,
		byte[] data,
		IEnumerable<Account>? principals = null)
	{
		int[] handles = Handles.Of(principals);
		int handle = runtime.SharableFromEncoded(data, handles);

		return new(runtime, handle);
	}

	/// <summary>Open a bundle from a PEM envelope, resolved with <paramref name="principals"/>.</summary>
	public static SharableCertificateAttributes FromPem(
		WasmRuntime runtime,
		string pem,
		IEnumerable<Account>? principals = null)
	{
		int[] handles = Handles.Of(principals);
		int handle = runtime.SharableFromPem(pem, handles);

		return new(runtime, handle);
	}

	/// <summary>Grant <paramref name="accounts"/> access, invalidating the encoded form.</summary>
	public void GrantAccess(IEnumerable<Account> accounts)
	{
		int[] handles = Handles.Of(accounts);
		_runtime.SharableGrantAccess(Handle, handles);
	}

	/// <summary>Revoke the account identified by its type-prefixed <paramref name="publicKey"/>.</summary>
	public void RevokeAccess(byte[] publicKey) => _runtime.SharableRevokeAccess(Handle, publicKey);

	/// <summary>The type-prefixed public keys of the accounts that can open the bundle.</summary>
	public IReadOnlyList<byte[]> Principals()
	{
		byte[] payload = _runtime.SharablePrincipals(Handle);
		return PrincipalKeys.Decode(payload);
	}

	/// <summary>The bundle's DER-encoded container bytes, requiring a granted recipient.</summary>
	public byte[] Export() => _runtime.SharableExport(Handle);

	/// <summary>The bundle exported as a PEM envelope.</summary>
	public string ToPem() => _runtime.SharableToPem(Handle);

	/// <summary>The embedded leaf certificate, as an independently owned object.</summary>
	public KycCertificate LeafCertificate()
	{
		int handle = _runtime.SharableCertificate(Handle);
		return KycCertificate.Adopt(_runtime, handle);
	}

	/// <summary>The embedded intermediate certificate chain, as owned objects.</summary>
	public IReadOnlyList<Certificate> Intermediates()
	{
		byte[] payload = _runtime.SharableIntermediates(Handle);
		string[] pems = JsonSerializer.Deserialize<string[]>(payload) ?? Array.Empty<string>();

		return pems.Select(pem => Certificate.Parse(_runtime, pem)).ToList();
	}

	/// <summary>The names of the disclosed attributes.</summary>
	public IReadOnlyList<string> AttributeNames()
	{
		byte[] payload = _runtime.SharableAttributeNames(Handle);
		return JsonSerializer.Deserialize<string[]>(payload) ?? Array.Empty<string>();
	}

	/// <summary>The validated raw disclosed value for <paramref name="name"/>, or <c>null</c> when not disclosed.</summary>
	public byte[]? AttributeBuffer(string name)
	{
		byte[] value = _runtime.SharableAttributeBuffer(Handle, name);
		return NullWhenEmpty(value);
	}

	/// <summary>The schema-decoded semantic value for <paramref name="name"/>, or <c>null</c> when not disclosed.</summary>
	public byte[]? AttributeValue(string name)
	{
		byte[] value = _runtime.SharableAttributeValue(Handle, name);
		return NullWhenEmpty(value);
	}

	/// <summary>Map the core's empty not-disclosed sentinel to <c>null</c>.</summary>
	private static byte[]? NullWhenEmpty(byte[] value)
	{
		if (value.Length == 0)
		{
			return null;
		}

		return value;
	}

	/// <summary>Release the core-module bundle handle.</summary>
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_runtime.SharableFree(Handle);
	}
}
