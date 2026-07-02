namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// A Keeta account: a signer derived from a seed, or a read-only account parsed
/// from an address. The key material lives inside the wasm core.
/// </summary>
public sealed class Account : WasmObject
{
	internal Account(WasmRuntime runtime, int handle)
		: base(runtime, handle)
	{
	}

	/// <summary>The account's textual <c>keeta_...</c> address.</summary>
	public string Address => Runtime.AccountAddress(Handle);

	/// <summary>The account's algorithm name.</summary>
	public string Algorithm => Runtime.AccountAlgorithm(Handle);

	/// <summary>The account's type-prefixed public key (hex).</summary>
	public string PublicKey => Runtime.AccountPublicKey(Handle);

	/// <summary>Sign <paramref name="message"/> with the account's private key.</summary>
	public byte[] Sign(byte[] message) => Runtime.AccountSign(Handle, message);

	/// <summary>Verify <paramref name="signature"/> over <paramref name="message"/>.</summary>
	public bool Verify(byte[] message, byte[] signature) => Runtime.AccountVerify(Handle, message, signature);

	/// <summary>Encrypt <paramref name="plaintext"/> to the account's public key.</summary>
	public byte[] Encrypt(byte[] plaintext) => Runtime.AccountEncrypt(Handle, plaintext);

	/// <summary>Decrypt <paramref name="ciphertext"/> with the account's private key.</summary>
	public byte[] Decrypt(byte[] ciphertext) => Runtime.AccountDecrypt(Handle, ciphertext);

	private protected override void Release(WasmRuntime runtime, int handle) => runtime.AccountFree(handle);
}
