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

	/// <summary>Build a read-only account from a raw hex <paramref name="publicKey"/> and its <paramref name="algorithm"/>.</summary>
	public Account FromPublicKey(string publicKey, string algorithm)
	{
		int handle = _runtime.AccountFromPublicKey(publicKey, algorithm);
		return new(_runtime, handle);
	}

	/// <summary>
	/// Build a read-only account from type-prefixed transport hex, as returned
	/// by <see cref="Account.PublicKeyAndType"/>.
	/// </summary>
	public Account FromPublicKeyAndType(string publicKeyAndType)
	{
		byte[] keyData;
		try
		{
			keyData = Convert.FromHexString(publicKeyAndType);
		}
		catch (FormatException)
		{
			throw new KeetaException("INVALID_PUBLIC_KEY", "public key must be hex");
		}

		if (keyData.Length < 2)
		{
			throw new KeetaException("INVALID_PUBLIC_KEY", "public key is too short to carry a type byte and key");
		}

		string algorithm = AlgorithmFromTypeByte(keyData[0]);
		string rawPublicKey = Convert.ToHexString(keyData.AsSpan(1));
		return FromPublicKey(rawPublicKey, algorithm);
	}

	/// <summary>Generate a random hex seed.</summary>
	public string GenerateRandomSeed() => _runtime.AccountGenerateSeed();

	/// <summary>Generate a random BIP39 mnemonic.</summary>
	public IReadOnlyList<string> GeneratePassphrase()
	{
		string words = _runtime.AccountGeneratePassphrase();
		return words.Split('\n', StringSplitOptions.RemoveEmptyEntries);
	}

	// The KeyPairType discriminants from the reference implementation. Identifier
	// account types (network 2, token 3, storage 4, multisig 7) carry no signing
	// key, so the core module cannot construct them from a public key.
	private static string AlgorithmFromTypeByte(byte typeByte) => typeByte switch
	{
		0 => "ecdsa_secp256k1",
		1 => "ed25519",
		6 => "ecdsa_secp256r1",
		_ => throw new KeetaException("INVALID_ALGORITHM", $"account type {typeByte} is not a supported signing algorithm"),
	};
}
