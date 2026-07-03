using Xunit;

namespace KeetaNet.Anchor.Tests;

/// <summary>The deterministic seeds, algorithms, and endpoints the baseline suites share.</summary>
internal static class TestSeeds
{
	public const string Subject = "1111111111111111111111111111111111111111111111111111111111111111";
	public const string Issuer = "2222222222222222222222222222222222222222222222222222222222222222";
	public const string Recipient = "3333333333333333333333333333333333333333333333333333333333333333";

	/// <summary>The account algorithm the suites default to when any one will do.</summary>
	public const string DefaultAlgorithm = "ed25519";

	/// <summary>An non-routable anchor endpoint for tests that must never reach a live service.</summary>
	public const string NonRoutableAnchor = "http://127.0.0.1:1";

	/// <summary>The validity window issued leaves share across the suites.</summary>
	public static readonly DateTimeOffset NotBefore = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
	public static readonly DateTimeOffset NotAfter = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);

	public static TheoryData<string> Algorithms => new()
	{
		"ed25519",
		"ecdsa_secp256k1",
		"ecdsa_secp256r1",
	};
}
