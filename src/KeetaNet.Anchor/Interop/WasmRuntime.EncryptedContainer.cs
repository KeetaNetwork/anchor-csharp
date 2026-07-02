namespace KeetaNet.Anchor;

/// <summary>
/// The encrypted-container surface of the P1 core module: handle-based,
/// optionally encrypted and signed blobs. Principal sets and signers are passed
/// as account handles from the shared <c>crypto</c> registry.
/// </summary>
public sealed partial class WasmRuntime
{
	internal int EncryptedContainerFromPlaintext(byte[] data, int[] principals, int locked, int signerHandle) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument payload = arguments.WriteBytes(data);
			Argument keys = arguments.WriteHandles(principals);

			int result = Invoke<int, int, int, int, int, int, int>("keeta_encrypted_container_from_plaintext", payload.Pointer, payload.Length, keys.Pointer, keys.Length, locked, signerHandle);
			return TakeHandle(result);
		});

	internal int EncryptedContainerFromEncoded(byte[] data, int[] principals) =>
		ParseBytesWithPrincipals("keeta_encrypted_container_from_encoded", data, principals);

	internal int EncryptedContainerFromEncrypted(byte[] data, int[] principals) =>
		ParseBytesWithPrincipals("keeta_encrypted_container_from_encrypted", data, principals);

	internal byte[] EncryptedContainerGetPlaintext(int handle) =>
		BytesOf("keeta_encrypted_container_get_plaintext", handle);

	internal byte[] EncryptedContainerGetEncoded(int handle) =>
		BytesOf("keeta_encrypted_container_get_encoded", handle);

	internal bool EncryptedContainerIsEncrypted(int handle) =>
		FlagOf("keeta_encrypted_container_is_encrypted", handle);

	internal bool EncryptedContainerIsSigned(int handle) =>
		FlagOf("keeta_encrypted_container_is_signed", handle);

	internal bool EncryptedContainerVerifySignature(int handle) =>
		FlagOf("keeta_encrypted_container_verify_signature", handle);

	internal byte[] EncryptedContainerSigningAccount(int handle) =>
		BytesOf("keeta_encrypted_container_signing_account", handle);

	internal byte[] EncryptedContainerPrincipals(int handle) =>
		BytesOf("keeta_encrypted_container_principals", handle);

	internal void EncryptedContainerGrantAccess(int handle, int[] principals) =>
		GrantAccess("keeta_encrypted_container_grant_access", handle, principals);

	internal void EncryptedContainerRevokeAccess(int handle, byte[] publicKey) =>
		RevokeAccess("keeta_encrypted_container_revoke_access", handle, publicKey);

	internal void EncryptedContainerFree(int handle) => RunFree("keeta_encrypted_container_free", handle);
}
