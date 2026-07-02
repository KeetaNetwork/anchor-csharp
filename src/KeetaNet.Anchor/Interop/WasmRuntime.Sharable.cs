using System.Text.Json;

namespace KeetaNet.Anchor;

/// <summary>
/// The sharable certificate-attributes surface of the P1 core module: a sealed,
/// selectively disclosed subset of a leaf's attributes, handle-based. Leaf,
/// account, and base-certificate handles are reused from the shared
/// <c>crypto</c> and KYC registries.
/// </summary>
public sealed partial class WasmRuntime
{
	internal int SharableFromCertificate(int certificateHandle, int subjectHandle, int[] intermediates, string[] names)
	{
		using var arguments = new ArgumentScope(this);
		Argument bridges = arguments.WriteHandles(intermediates);
		byte[] labelsJson = JsonSerializer.SerializeToUtf8Bytes(names);
		Argument labels = arguments.WriteBytes(labelsJson);

		int result = Invoke<int, int, int, int, int, int, int>("keeta_sharable_from_certificate", certificateHandle, subjectHandle, bridges.Pointer, bridges.Length, labels.Pointer, labels.Length);
		return TakeHandle(result);
	}

	internal int SharableFromEncoded(byte[] data, int[] principals) =>
		ParseBytesWithPrincipals("keeta_sharable_from_encoded", data, principals);

	internal int SharableFromPem(string pem, int[] principals)
	{
		using var arguments = new ArgumentScope(this);
		Argument envelope = arguments.Write(pem);
		Argument keys = arguments.WriteHandles(principals);

		int result = Invoke<int, int, int, int, int>("keeta_sharable_from_pem", envelope.Pointer, envelope.Length, keys.Pointer, keys.Length);
		return TakeHandle(result);
	}

	internal void SharableGrantAccess(int handle, int[] principals) =>
		GrantAccess("keeta_sharable_grant_access", handle, principals);

	internal void SharableRevokeAccess(int handle, byte[] publicKey) =>
		RevokeAccess("keeta_sharable_revoke_access", handle, publicKey);

	internal byte[] SharablePrincipals(int handle)
	{
		int result = Invoke<int, int>("keeta_sharable_principals", handle);
		return TakeBytes(result);
	}

	internal byte[] SharableExport(int handle)
	{
		int result = Invoke<int, int>("keeta_sharable_export", handle);
		return TakeBytes(result);
	}

	internal string SharableToPem(int handle)
	{
		int result = Invoke<int, int>("keeta_sharable_to_pem", handle);
		return Text(result);
	}

	internal int SharableCertificate(int handle)
	{
		int result = Invoke<int, int>("keeta_sharable_certificate", handle);
		return TakeHandle(result);
	}

	internal byte[] SharableIntermediates(int handle)
	{
		int result = Invoke<int, int>("keeta_sharable_intermediates", handle);
		return TakeBytes(result);
	}

	internal byte[] SharableAttributeNames(int handle)
	{
		int result = Invoke<int, int>("keeta_sharable_attribute_names", handle);
		return TakeBytes(result);
	}

	internal byte[] SharableAttributeBuffer(int handle, string name) =>
		WithHandleAndText("keeta_sharable_attribute_buffer", handle, name);

	internal byte[] SharableAttributeValue(int handle, string name) =>
		WithHandleAndText("keeta_sharable_attribute_value", handle, name);

	internal void SharableFree(int handle) => Free("keeta_sharable_free", handle);
}
