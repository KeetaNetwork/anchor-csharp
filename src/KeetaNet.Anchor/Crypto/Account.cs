namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// A Keeta account: a signer derived from a seed, or a read-only account parsed
/// from an address. The key material lives inside the wasm core.
/// </summary>
public sealed class Account : WasmObject
{
	private Account(WasmRuntime runtime, int handle)
		: base(runtime, handle)
	{
	}

	/// <summary>Derive a signing account from a hex <paramref name="seed"/>.</summary>
	/// <remarks><paramref name="algorithm"/> is <c>ed25519</c>, <c>ecdsa_secp256k1</c>, or <c>ecdsa_secp256r1</c>.</remarks>
	public static Account FromSeed(WasmRuntime runtime, string seed, uint index, string algorithm)
	{
		int handle = runtime.AccountFromSeed(seed, index, algorithm);
		return new(runtime, handle);
	}

	/// <summary>Build a read-only account from its textual address.</summary>
	public static Account FromAddress(WasmRuntime runtime, string address)
	{
		int handle = runtime.AccountFromAddress(address);
		return new(runtime, handle);
	}

	/// <summary>Derive a signing account from a hex <paramref name="privateKey"/>.</summary>
	/// <remarks><paramref name="algorithm"/> is <c>ed25519</c>, <c>ecdsa_secp256k1</c>, or <c>ecdsa_secp256r1</c>.</remarks>
	public static Account FromPrivateKey(WasmRuntime runtime, string privateKey, string algorithm)
	{
		int handle = runtime.AccountFromPrivateKey(privateKey, algorithm);
		return new(runtime, handle);
	}

	/// <summary>Derive a signing account from a BIP39 mnemonic <paramref name="words"/>.</summary>
	public static Account FromPassphrase(WasmRuntime runtime, IEnumerable<string> words, uint index, string algorithm)
	{
		string mnemonic = string.Join('\n', words);
		int handle = runtime.AccountFromPassphrase(mnemonic, index, algorithm);
		return new(runtime, handle);
	}

	/// <summary>Build a read-only account from a hex <paramref name="publicKey"/>.</summary>
	public static Account FromPublicKey(WasmRuntime runtime, string publicKey, string algorithm)
	{
		int handle = runtime.AccountFromPublicKey(publicKey, algorithm);
		return new(runtime, handle);
	}

	/// <summary>Generate a random hex seed.</summary>
	public static string GenerateSeed(WasmRuntime runtime) => runtime.AccountGenerateSeed();

	/// <summary>Generate a random BIP39 mnemonic.</summary>
	public static IReadOnlyList<string> GeneratePassphrase(WasmRuntime runtime)
	{
		string words = runtime.AccountGeneratePassphrase();
		return words.Split('\n', StringSplitOptions.RemoveEmptyEntries);
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
