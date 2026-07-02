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
	internal int SharableFromCertificate(int certificateHandle, int subjectHandle, int[] intermediates, string[] names) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			byte[] labelsJson = JsonSerializer.SerializeToUtf8Bytes(names);
			Argument bridges = arguments.WriteHandles(intermediates);
			Argument labels = arguments.WriteBytes(labelsJson);

			int result = Invoke<int, int, int, int, int, int, int>("keeta_sharable_from_certificate", certificateHandle, subjectHandle, bridges.Pointer, bridges.Length, labels.Pointer, labels.Length);
			return TakeHandle(result);
		});

	internal int SharableFromEncoded(byte[] data, int[] principals) =>
		ParseBytesWithPrincipals("keeta_sharable_from_encoded", data, principals);

	internal int SharableFromPem(string pem, int[] principals) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument envelope = arguments.Write(pem);
			Argument keys = arguments.WriteHandles(principals);

			int result = Invoke<int, int, int, int, int>("keeta_sharable_from_pem", envelope.Pointer, envelope.Length, keys.Pointer, keys.Length);
			return TakeHandle(result);
		});

	internal void SharableGrantAccess(int handle, int[] principals) =>
		GrantAccess("keeta_sharable_grant_access", handle, principals);

	internal void SharableRevokeAccess(int handle, byte[] publicKey) =>
		RevokeAccess("keeta_sharable_revoke_access", handle, publicKey);

	internal byte[] SharablePrincipals(int handle) => BytesOf("keeta_sharable_principals", handle);

	internal byte[] SharableExport(int handle) => BytesOf("keeta_sharable_export", handle);

	internal string SharableToPem(int handle) => TextOf("keeta_sharable_to_pem", handle);

	internal int SharableCertificate(int handle) => HandleOf("keeta_sharable_certificate", handle);

	internal byte[] SharableIntermediates(int handle) => BytesOf("keeta_sharable_intermediates", handle);

	internal byte[] SharableAttributeNames(int handle) => BytesOf("keeta_sharable_attribute_names", handle);

	internal byte[] SharableAttributeBuffer(int handle, string name) =>
		Run(() => WithHandleAndText("keeta_sharable_attribute_buffer", handle, name));

	internal byte[] SharableAttributeValue(int handle, string name) =>
		Run(() => WithHandleAndText("keeta_sharable_attribute_value", handle, name));

	internal void SharableFree(int handle) => RunFree("keeta_sharable_free", handle);
}
