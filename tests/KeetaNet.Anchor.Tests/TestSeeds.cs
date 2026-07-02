using Xunit;

namespace KeetaNet.Anchor.Tests;

/// <summary>The deterministic seeds and algorithms the baseline suites share.</summary>
internal static class TestSeeds
{
	public const string Subject = "1111111111111111111111111111111111111111111111111111111111111111";
	public const string Issuer = "2222222222222222222222222222222222222222222222222222222222222222";
	public const string Recipient = "3333333333333333333333333333333333333333333333333333333333333333";

	public static TheoryData<string> Algorithms => new()
	{
		"ed25519",
		"ecdsa_secp256k1",
		"ecdsa_secp256r1",
	};
}
