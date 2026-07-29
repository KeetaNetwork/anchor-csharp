namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// A Keeta account: a signer derived from a seed, or a read-only account parsed
/// from a public-key string. The key material lives inside the wasm core.
/// </summary>
public sealed class Account : WasmObject
{
	internal Account(WasmRuntime runtime, int handle)
		: base(runtime, handle)
	{
	}

	/// <summary>The account's textual <c>keeta_...</c> public-key string.</summary>
	public string PublicKeyString => Runtime.AccountPublicKeyString(Handle);

	/// <summary>The account's algorithm name.</summary>
	public string Algorithm => Runtime.AccountAlgorithm(Handle);

	/// <summary>
	/// The account's type-prefixed public key transport hex: the lead byte
	/// names the algorithm, the rest is the raw public key. Feeds
	/// <see cref="AccountFactory.FromPublicKeyAndType"/>.
	/// </summary>
	public string PublicKeyAndType => Runtime.AccountPublicKey(Handle);

	/// <summary>Sign <paramref name="message"/> with the account's private key.</summary>
	public byte[] Sign(byte[] message) => Runtime.AccountSign(Handle, message);

	/// <summary>Verify <paramref name="signature"/> over <paramref name="message"/>.</summary>
	public bool Verify(byte[] message, byte[] signature) => Runtime.AccountVerify(Handle, message, signature);

	/// <summary>Encrypt <paramref name="plaintext"/> to the account's public key.</summary>
	public byte[] Encrypt(byte[] plaintext) => Runtime.AccountEncrypt(Handle, plaintext);

	/// <summary>Decrypt <paramref name="ciphertext"/> with the account's private key.</summary>
	public byte[] Decrypt(byte[] ciphertext) => Runtime.AccountDecrypt(Handle, ciphertext);

	/// <summary>
	/// Derive the <paramref name="kind"/> identifier account this account claims
	/// at block <paramref name="previous"/> (its opening block when omitted) and
	/// operation <paramref name="index"/>. The caller owns the returned handle.
	/// </summary>
	public Account GenerateIdentifier(IdentifierKind kind, BlockHash? previous = null, int index = 0)
	{
		byte[] hash = previous?.ToBytes() ?? Array.Empty<byte>();
		int handle = Runtime.GenerateIdentifier(Handle, CoreNames.Of(kind), hash, index);

		return new Account(Runtime, handle);
	}

	private protected override void Release(WasmRuntime runtime, int handle) => runtime.AccountFree(handle);
}
