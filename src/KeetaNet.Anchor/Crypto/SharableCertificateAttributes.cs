using System.Text.Json;

namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// A sealed, selectively disclosed subset of a KYC certificate's attributes.
/// The bundle and its derived state live inside the wasm core.
/// </summary>
public sealed class SharableCertificateAttributes : WasmObject
{
	internal SharableCertificateAttributes(WasmRuntime runtime, int handle)
		: base(runtime, handle)
	{
	}

	/// <summary>Grant <paramref name="accounts"/> access, invalidating the encoded form.</summary>
	public void GrantAccess(IEnumerable<Account> accounts)
	{
		int[] handles = Handles.Of(accounts);
		Runtime.SharableGrantAccess(Handle, handles);
	}

	/// <summary>Revoke the account identified by its type-prefixed <paramref name="publicKey"/>.</summary>
	public void RevokeAccess(byte[] publicKey) => Runtime.SharableRevokeAccess(Handle, publicKey);

	/// <summary>The type-prefixed public keys of the accounts that can open the bundle.</summary>
	public IReadOnlyList<byte[]> GetPrincipals()
	{
		byte[] payload = Runtime.SharablePrincipals(Handle);
		return PrincipalKeys.Decode(payload);
	}

	/// <summary>The bundle's DER-encoded container bytes, requiring a granted recipient.</summary>
	public byte[] Export() => Runtime.SharableExport(Handle);

	/// <summary>The bundle exported as a PEM envelope.</summary>
	public string ToPem() => Runtime.SharableToPem(Handle);

	/// <summary>The embedded leaf certificate, as an independently owned object.</summary>
	public KycCertificate GetCertificate()
	{
		int handle = Runtime.SharableCertificate(Handle);
		return KycCertificate.Adopt(Runtime, handle);
	}

	/// <summary>The embedded intermediate certificate chain, as owned objects.</summary>
	public IReadOnlyList<Certificate> GetIntermediates()
	{
		byte[] payload = Runtime.SharableIntermediates(Handle);
		string[] pems = JsonSerializer.Deserialize<string[]>(payload) ?? Array.Empty<string>();

		return pems.Select(Runtime.Certificates.Parse).ToList();
	}

	/// <summary>The names of the disclosed attributes.</summary>
	public IReadOnlyList<string> GetAttributeNames()
	{
		byte[] payload = Runtime.SharableAttributeNames(Handle);
		return JsonSerializer.Deserialize<string[]>(payload) ?? Array.Empty<string>();
	}

	/// <summary>The validated raw disclosed value for <paramref name="name"/>, or <c>null</c> when not disclosed.</summary>
	public byte[]? GetAttributeBuffer(string name)
	{
		byte[] value = Runtime.SharableAttributeBuffer(Handle, name);
		return NullWhenEmpty(value);
	}

	/// <summary>The schema-decoded semantic value for <paramref name="name"/>, or <c>null</c> when not disclosed.</summary>
	public byte[]? GetAttributeValue(string name)
	{
		byte[] value = Runtime.SharableAttributeValue(Handle, name);
		return NullWhenEmpty(value);
	}

	/// <summary>
	/// The inlined, digest-verified blob for reference <paramref name="id"/> on
	/// the disclosed attribute <paramref name="name"/>, or <c>null</c> when the
	/// attribute, entry, or matching reference node is absent.
	/// </summary>
	public byte[]? GetReferenceBlob(string name, string id)
	{
		byte[] value = Runtime.SharableReferenceBlob(Handle, name, id);
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

	private protected override void Release(WasmRuntime runtime, int handle) => runtime.SharableFree(handle);
}
