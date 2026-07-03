namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// Creates <see cref="EncryptedContainer"/> objects owned by one runtime.
/// Reached through <see cref="WasmRuntime.Containers"/>.
/// </summary>
public sealed class EncryptedContainerFactory
{
	private readonly WasmRuntime _runtime;

	internal EncryptedContainerFactory(WasmRuntime runtime) => _runtime = runtime;

	/// <summary>
	/// Build a plaintext container. A non-empty <paramref name="principals"/> set
	/// seals it to those accounts; <paramref name="signer"/> attaches a detached
	/// signature; <paramref name="locked"/> overrides the default plaintext policy.
	/// </summary>
	public EncryptedContainer FromPlaintext(
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

		int handle = _runtime.EncryptedContainerFromPlaintext(data, handles, lockedFlag, signerHandle);
		return new(_runtime, handle);
	}

	/// <summary>
	/// Build a container from an encoded blob that may be plaintext or encrypted,
	/// resolving an encrypted blob with the optional <paramref name="principals"/>.
	/// </summary>
	public EncryptedContainer FromEncoded(byte[] data, IEnumerable<Account>? principals = null)
	{
		int[] handles = Handles.Of(principals);
		int handle = _runtime.EncryptedContainerFromEncoded(data, handles);

		return new(_runtime, handle);
	}

	/// <summary>
	/// Build a container from a blob that must be encrypted, opened by one of
	/// <paramref name="principals"/>.
	/// </summary>
	public EncryptedContainer FromEncrypted(byte[] data, IEnumerable<Account> principals)
	{
		int[] handles = Handles.Of(principals);
		int handle = _runtime.EncryptedContainerFromEncrypted(data, handles);

		return new(_runtime, handle);
	}
}
