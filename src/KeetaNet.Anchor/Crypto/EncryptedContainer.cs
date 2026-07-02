namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// A hybrid-encrypted, optionally signed container. The blob and its derived
/// state live inside the wasm core.
/// </summary>
public sealed class EncryptedContainer : WasmObject
{
	private EncryptedContainer(WasmRuntime runtime, int handle)
		: base(runtime, handle)
	{
	}

	/// <summary>
	/// Build a plaintext container. A non-empty <paramref name="principals"/> set
	/// seals it to those accounts; <paramref name="signer"/> attaches a detached
	/// signature; <paramref name="locked"/> overrides the default plaintext policy.
	/// </summary>
	public static EncryptedContainer FromPlaintext(
		WasmRuntime runtime,
		byte[] data,
		IEnumerable<Account>? principals = null,
		bool? locked = null,
		Account? signer = null)
	{
		int[] handles = Handles.Of(principals);
		int lockedFlag = locked switch
		{
			null => -1,
			true => 1,
			false => 0,
		};
		int signerHandle = signer?.Handle ?? 0;

		int handle = runtime.EncryptedContainerFromPlaintext(data, handles, lockedFlag, signerHandle);
		return new(runtime, handle);
	}

	/// <summary>
	/// Build a container from an encoded blob that may be plaintext or encrypted,
	/// resolving an encrypted blob with the optional <paramref name="principals"/>.
	/// </summary>
	public static EncryptedContainer FromEncoded(
		WasmRuntime runtime,
		byte[] data,
		IEnumerable<Account>? principals = null)
	{
		int[] handles = Handles.Of(principals);
		int handle = runtime.EncryptedContainerFromEncoded(data, handles);

		return new(runtime, handle);
	}

	/// <summary>
	/// Build a container from a blob that must be encrypted, opened by one of
	/// <paramref name="principals"/>.
	/// </summary>
	public static EncryptedContainer FromEncrypted(WasmRuntime runtime, byte[] data, IEnumerable<Account> principals)
	{
		int[] handles = Handles.Of(principals);
		int handle = runtime.EncryptedContainerFromEncrypted(data, handles);

		return new(runtime, handle);
	}

	/// <summary>The decrypted, decompressed plaintext.</summary>
	public byte[] Plaintext() => Runtime.EncryptedContainerGetPlaintext(Handle);

	/// <summary>The container's DER encoding.</summary>
	public byte[] Encoded() => Runtime.EncryptedContainerGetEncoded(Handle);

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
	public byte[]? SigningAccount()
	{
		byte[] key = Runtime.EncryptedContainerSigningAccount(Handle);
		if (key.Length == 0)
		{
			return null;
		}

		return key;
	}

	/// <summary>The type-prefixed public keys of the accounts that can open it.</summary>
	public IReadOnlyList<byte[]> Principals()
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
