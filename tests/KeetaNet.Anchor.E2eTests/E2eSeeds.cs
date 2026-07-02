using Xunit;

namespace KeetaNet.Anchor.E2eTests;

/// <summary>The deterministic seeds and algorithm names the e2e suites share.</summary>
internal static class E2eSeeds
{
	/// <summary>The subject both implementations derive on the reference's default curve.</summary>
	public const string Subject = "1111111111111111111111111111111111111111111111111111111111111111";
	public const string Issuer = "2222222222222222222222222222222222222222222222222222222222222222";
	public const string Recipient = "3333333333333333333333333333333333333333333333333333333333333333";
	/// <summary>The account that signs anchor requests.</summary>
	public const string Caller = "4444444444444444444444444444444444444444444444444444444444444444";

	/// <summary>The reference implementation's default curve (`Account.fromSeed` without an algorithm).</summary>
	public const string Secp256k1 = "ecdsa_secp256k1";

	/// <summary>The validity window C#-issued leaves use.</summary>
	public static readonly DateTimeOffset NotBefore = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
	public static readonly DateTimeOffset NotAfter = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);

	public static TheoryData<string> Algorithms => new()
	{
		"ed25519",
		"ecdsa_secp256k1",
		"ecdsa_secp256r1",
	};

	/// <summary>Map a core algorithm name to the harness's curve name.</summary>
	public static string HarnessAlgorithm(string algorithm) => algorithm switch
	{
		"ecdsa_secp256k1" => "secp256k1",
		"ecdsa_secp256r1" => "secp256r1",
		"ed25519" => "ed25519",
		_ => throw new ArgumentException($"unsupported algorithm `{algorithm}`", nameof(algorithm)),
	};
}
