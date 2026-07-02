namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// A Keeta account: a signer derived from a seed, or a read-only account parsed
/// from an address. The key material lives inside the wasm core; this wrapper
/// holds only the handle and releases it on <see cref="Dispose"/>.
/// </summary>
public sealed class Account : IDisposable
{
	private readonly WasmRuntime _runtime;
	private bool _disposed;

	/// <summary>The core-module handle backing this account.</summary>
	internal int Handle { get; }

	private Account(WasmRuntime runtime, int handle)
	{
		_runtime = runtime;
		Handle = handle;
	}

	/// <summary>Derive a signing account from a hex <paramref name="seed"/>.</summary>
	/// <remarks><paramref name="algorithm"/> is <c>ed25519</c>, <c>ecdsa_secp256k1</c>, or <c>ecdsa_secp256r1</c>.</remarks>
	public static Account FromSeed(WasmRuntime runtime, string seed, uint index, string algorithm) =>
		new(runtime, runtime.AccountFromSeed(seed, index, algorithm));

	/// <summary>Build a read-only account from its textual address.</summary>
	public static Account FromAddress(WasmRuntime runtime, string address) =>
		new(runtime, runtime.AccountFromAddress(address));

	/// <summary>Derive a signing account from a hex <paramref name="privateKey"/>.</summary>
	/// <remarks><paramref name="algorithm"/> is <c>ed25519</c>, <c>ecdsa_secp256k1</c>, or <c>ecdsa_secp256r1</c>.</remarks>
	public static Account FromPrivateKey(WasmRuntime runtime, string privateKey, string algorithm) =>
		new(runtime, runtime.AccountFromPrivateKey(privateKey, algorithm));

	/// <summary>Derive a signing account from a BIP39 mnemonic <paramref name="words"/>.</summary>
	public static Account FromPassphrase(WasmRuntime runtime, IEnumerable<string> words, uint index, string algorithm) =>
		new(runtime, runtime.AccountFromPassphrase(string.Join('\n', words), index, algorithm));

	/// <summary>Build a read-only account from a hex <paramref name="publicKey"/>.</summary>
	public static Account FromPublicKey(WasmRuntime runtime, string publicKey, string algorithm) =>
		new(runtime, runtime.AccountFromPublicKey(publicKey, algorithm));

	/// <summary>Generate a random hex seed.</summary>
	public static string GenerateSeed(WasmRuntime runtime) => runtime.AccountGenerateSeed();

	/// <summary>Generate a random BIP39 mnemonic.</summary>
	public static IReadOnlyList<string> GeneratePassphrase(WasmRuntime runtime) =>
		runtime.AccountGeneratePassphrase().Split('\n', StringSplitOptions.RemoveEmptyEntries);

	/// <summary>The account's textual <c>keeta_...</c> address.</summary>
	public string Address => _runtime.AccountAddress(Handle);

	/// <summary>The account's algorithm name.</summary>
	public string Algorithm => _runtime.AccountAlgorithm(Handle);

	/// <summary>The account's type-prefixed public key (hex).</summary>
	public string PublicKey => _runtime.AccountPublicKey(Handle);

	/// <summary>Sign <paramref name="message"/> with the account's private key.</summary>
	public byte[] Sign(byte[] message) => _runtime.AccountSign(Handle, message);

	/// <summary>Verify <paramref name="signature"/> over <paramref name="message"/>.</summary>
	public bool Verify(byte[] message, byte[] signature) => _runtime.AccountVerify(Handle, message, signature);

	/// <summary>Encrypt <paramref name="plaintext"/> to the account's public key.</summary>
	public byte[] Encrypt(byte[] plaintext) => _runtime.AccountEncrypt(Handle, plaintext);

	/// <summary>Decrypt <paramref name="ciphertext"/> with the account's private key.</summary>
	public byte[] Decrypt(byte[] ciphertext) => _runtime.AccountDecrypt(Handle, ciphertext);

	/// <summary>Release the core-module account handle.</summary>
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_runtime.AccountFree(Handle);
	}
}
