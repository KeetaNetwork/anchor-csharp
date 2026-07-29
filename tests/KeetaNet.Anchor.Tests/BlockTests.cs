using KeetaNet.Anchor.Crypto;
using Xunit;

namespace KeetaNet.Anchor.Tests;

/// <summary>
/// The offline block surface: building and signing a block, its transport
/// round-trip, and the network's derived base token. The networked transmit
/// flow lives in the E2E suite.
/// </summary>
public sealed class BlockTests
{
	/// <summary>The reference TEST network id; signing rejects unknown networks.</summary>
	private const long Network = 0x5445_5354;

	/// <summary>A neighboring known network (DEV), for the derivation contrast.</summary>
	private const long OtherNetwork = 0x44_4556;

	[Fact]
	public void ASignedOpeningBlockRoundTripsAndConsumesItsBuilder()
	{
		using var runtime = WasmRuntime.Load();
		using Account sender = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, TestSeeds.DefaultAlgorithm);
		using Account recipient = runtime.Accounts.FromSeed(TestSeeds.Recipient, 0, TestSeeds.DefaultAlgorithm);
		using Account token = runtime.Blocks.NetworkBaseToken(Network);

		using BlockOperation send = runtime.Blocks.Send(recipient, 42, token);
		using var builder = runtime.Blocks.NewBuilder();
		builder
			.WithVersion(2)
			.WithNetwork(Network)
			.WithAccount(sender)
			.WithSigner(sender)
			.WithDate(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000))
			.AsOpening()
			.AddOperation(send);

		using Block block = builder.Build();

		byte[] bytes = block.ToBytes();
		Assert.NotEmpty(bytes);

		// Decoding the transport bytes yields the identical block.
		using Block decoded = runtime.Blocks.ParseHex(Convert.ToHexString(bytes));
		Assert.Equal(block.Hash, decoded.Hash);

		// Building consumed the builder, so a second build refuses.
		KeetaException refused = Assert.Throws<KeetaException>(builder.Build);
		Assert.Equal("BUILDER_CONSUMED", refused.Code);
	}

	[Fact]
	public void TheUserBuilderPreSetsTheSigningDefaults()
	{
		using var runtime = WasmRuntime.Load();
		using Account sender = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, TestSeeds.DefaultAlgorithm);
		using Account recipient = runtime.Accounts.FromSeed(TestSeeds.Recipient, 0, TestSeeds.DefaultAlgorithm);
		using UserClient user = runtime.CreateUserClient(TestSeeds.NonRoutableAnchor, sender, network: Network);

		using BlockOperation send = runtime.Blocks.Send(recipient, 42, user.Client.BaseToken!);
		using BlockBuilder builder = user.InitBuilder();
		using Block block = builder.AsOpening().AddOperation(send).Build();
		Assert.NotEmpty(block.ToBytes());

		// Without a bound network there is nothing to pre-set.
		using UserClient unbound = runtime.CreateUserClient(TestSeeds.NonRoutableAnchor, sender);
		KeetaException refused = Assert.Throws<KeetaException>(() =>
		{
			using BlockBuilder unreachable = unbound.InitBuilder();
		});
		Assert.Equal("NETWORK_REQUIRED", refused.Code);

		// Without a signer there is nothing to sign with.
		using UserClient readOnly = runtime.CreateUserClient(TestSeeds.NonRoutableAnchor, signer: null, network: Network);
		Assert.True(readOnly.IsReadOnly);
		KeetaException unsigned = Assert.Throws<KeetaException>(() =>
		{
			using BlockBuilder unreachable = readOnly.InitBuilder();
		});
		Assert.Equal("SIGNER_REQUIRED", unsigned.Code);
	}

	[Fact]
	public void TheBaseTokenDerivesDeterministicallyFromTheNetwork()
	{
		using var runtime = WasmRuntime.Load();
		using Account first = runtime.Blocks.NetworkBaseToken(Network);
		using Account again = runtime.Blocks.NetworkBaseToken(Network);
		using Account other = runtime.Blocks.NetworkBaseToken(OtherNetwork);

		Assert.StartsWith("keeta_", first.PublicKeyString, StringComparison.Ordinal);
		Assert.Equal(first.PublicKeyString, again.PublicKeyString);
		Assert.NotEqual(first.PublicKeyString, other.PublicKeyString);
	}
}
