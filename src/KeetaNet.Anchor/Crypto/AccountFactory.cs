namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// Creates <see cref="Account"/> objects owned by one runtime. Reached through
/// <see cref="WasmRuntime.Accounts"/>; every account it creates must be
/// disposed before that runtime.
/// </summary>
public sealed class AccountFactory
{
	private readonly WasmRuntime _runtime;

	internal AccountFactory(WasmRuntime runtime) => _runtime = runtime;

	/// <summary>Derive a signing account from a hex <paramref name="seed"/>.</summary>
	/// <remarks><paramref name="algorithm"/> is <c>ed25519</c>, <c>ecdsa_secp256k1</c>, or <c>ecdsa_secp256r1</c>.</remarks>
	public Account FromSeed(string seed, uint index, string algorithm)
	{
		int handle = _runtime.AccountFromSeed(seed, index, algorithm);
		return new(_runtime, handle);
	}

	/// <summary>Build a read-only account from its textual <c>keeta_...</c> account string.</summary>
	public Account FromAccount(string account)
	{
		int handle = _runtime.AccountFromAddress(account);
		return new(_runtime, handle);
	}

	/// <summary>Derive a signing account from a hex <paramref name="privateKey"/>.</summary>
	/// <remarks><paramref name="algorithm"/> is <c>ed25519</c>, <c>ecdsa_secp256k1</c>, or <c>ecdsa_secp256r1</c>.</remarks>
	public Account FromPrivateKey(string privateKey, string algorithm)
	{
		int handle = _runtime.AccountFromPrivateKey(privateKey, algorithm);
		return new(_runtime, handle);
	}

	/// <summary>Derive a signing account from a BIP39 mnemonic <paramref name="words"/>.</summary>
	public Account FromPassphrase(IEnumerable<string> words, uint index, string algorithm)
	{
		string mnemonic = string.Join('\n', words);
		int handle = _runtime.AccountFromPassphrase(mnemonic, index, algorithm);
		return new(_runtime, handle);
	}

	/// <summary>Build a read-only account from a hex <paramref name="publicKey"/>.</summary>
	public Account FromPublicKey(string publicKey, string algorithm)
	{
		int handle = _runtime.AccountFromPublicKey(publicKey, algorithm);
		return new(_runtime, handle);
	}

	/// <summary>Generate a random hex seed.</summary>
	public string GenerateRandomSeed() => _runtime.AccountGenerateSeed();

	/// <summary>Generate a random BIP39 mnemonic.</summary>
	public IReadOnlyList<string> GeneratePassphrase()
	{
		string words = _runtime.AccountGeneratePassphrase();
		return words.Split('\n', StringSplitOptions.RemoveEmptyEntries);
	}
}
