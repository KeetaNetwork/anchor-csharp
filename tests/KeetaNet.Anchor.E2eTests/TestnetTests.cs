using System.Numerics;

using KeetaNet.Anchor.Crypto;

using Xunit;

namespace KeetaNet.Anchor.E2eTests;

/// <summary>
/// Opt-in tests against the live public test network.
/// </summary>
/// <remarks>
/// These tests exercise the multi-representative transmit path that no local
/// test node can, because a local node runs a single representative that
/// holds all the weight. Set <c>KEETA_TESTNET_SEED</c> to a funded secp256k1
/// seed to enable them. CI leaves them skipped.
/// </remarks>
public sealed class TestnetTests
{
	/// <summary>The environment variable that carries the funded seed.</summary>
	private const string SeedVariable = "KEETA_TESTNET_SEED";

	[Fact]
	public async Task SendRoundTripsAgainstTheLiveTestnet()
	{
		string seed = RequireSeed();
		CancellationToken cancellationToken = TestContext.Current.CancellationToken;

		using var runtime = WasmRuntime.Load();
		using Account holder = runtime.Accounts.FromSeed(seed, 0, "ecdsa_secp256k1");
		using Account recipient = runtime.Accounts.FromSeed(seed, 1, "ecdsa_secp256k1");
		using UserClient user = runtime.CreateUserClient(KeetaNetwork.Test, holder);
		KeetaClient client = user.Client;

		// The registry set answers reads and refreshes voting weights.
		string version = await client.GetVersion(cancellationToken);
		Assert.NotEmpty(version);
		await client.UpdateReps(cancellationToken: cancellationToken);

		Account baseToken = client.BaseToken!;
		BigInteger before = await user.Balance(baseToken, cancellationToken);
		Assert.True(before > BigInteger.Zero, $"fund {holder.PublicKeyString} on the testnet first");

		// The send must gather votes across representatives. No single
		// testnet representative holds quorum weight on its own.
		BigInteger sent = await client.GetBalance(recipient, baseToken, cancellationToken);
		Assert.True(await user.Send(recipient, 1, baseToken, cancellationToken: cancellationToken));

		BigInteger credited = await client.GetBalance(recipient, baseToken, cancellationToken);
		Assert.Equal(sent + 1, credited);
	}

	/// <summary>Returns the configured seed and skips the test when it is absent.</summary>
	private static string RequireSeed()
	{
		string? seed = Environment.GetEnvironmentVariable(SeedVariable);
		Assert.SkipWhen(string.IsNullOrEmpty(seed), $"set {SeedVariable} to a funded testnet seed to run");
		return seed!;
	}
}
