namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// A hybrid-encrypted, optionally signed container. The blob and its derived
/// state live inside the wasm core.
/// </summary>
public sealed class EncryptedContainer : WasmObject
{
	internal EncryptedContainer(WasmRuntime runtime, int handle)
		: base(runtime, handle)
	{
	}

	/// <summary>The decrypted, decompressed plaintext.</summary>
	public byte[] GetPlaintext() => Runtime.EncryptedContainerGetPlaintext(Handle);

	/// <summary>The container's DER encoding.</summary>
	public byte[] GetEncoded() => Runtime.EncryptedContainerGetEncoded(Handle);

	/// <summary>Whether the container is sealed to a principal set.</summary>
	public bool IsEncrypted => Runtime.EncryptedContainerIsEncrypted(Handle);

	/// <summary>Whether a signer is attached or a signature is present.</summary>
	public bool IsSigned => Runtime.EncryptedContainerIsSigned(Handle);

	/// <summary>Verify the detached signature over the compressed payload.</summary>
	public bool VerifySignature() => Runtime.EncryptedContainerVerifySignature(Handle);

	/// <summary>
	/// The type-prefixed public key of the signing account, or <c>null</c> when
	/// the container is unsigned.
	/// </summary>
	public byte[]? GetSigningAccount()
	{
		byte[] key = Runtime.EncryptedContainerSigningAccount(Handle);
		if (key.Length == 0)
		{
			return null;
		}

		return key;
	}

	/// <summary>The type-prefixed public keys of the accounts that can open it.</summary>
	public IReadOnlyList<byte[]> GetPrincipals()
	{
		byte[] payload = Runtime.EncryptedContainerPrincipals(Handle);
		return PrincipalKeys.Decode(payload);
	}

	/// <summary>Grant <paramref name="accounts"/> access, invalidating the encoded form.</summary>
	public void GrantAccess(IEnumerable<Account> accounts)
	{
		int[] handles = Handles.Of(accounts);
		Runtime.EncryptedContainerGrantAccess(Handle, handles);
	}

	/// <summary>Revoke the account identified by its type-prefixed <paramref name="publicKey"/>.</summary>
	public void RevokeAccess(byte[] publicKey) => Runtime.EncryptedContainerRevokeAccess(Handle, publicKey);

	private protected override void Release(WasmRuntime runtime, int handle) => runtime.EncryptedContainerFree(handle);
}
