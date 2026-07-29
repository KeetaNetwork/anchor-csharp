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

		// Decoding the transport bytes yields the identical block, and the
		// accessors expose its originator and hex form.
		using Block decoded = runtime.Blocks.ParseHex(Convert.ToHexString(bytes));
		Assert.Equal(block.Hash, decoded.Hash);
		Assert.Equal(Convert.ToHexString(bytes), block.ToHex(), ignoreCase: true);

		using Account originator = block.GetAccount();
		Assert.Equal(sender.PublicKeyString, originator.PublicKeyString);

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

	[Theory]
	[InlineData(BaseFlag.Access)]
	[InlineData(BaseFlag.Access, BaseFlag.SendOnBehalf)]
	[InlineData(BaseFlag.Access, BaseFlag.UpdateInfo, BaseFlag.ManageCertificate)]
	public void PermissionsRoundTripTheirFlagsThroughTheBitmapTransport(params BaseFlag[] flags)
	{
		using var runtime = WasmRuntime.Load();
		using Permissions permissions = runtime.Blocks.PermissionsFromFlags(flags);

		Assert.Equal(flags.OrderBy(flag => flag), permissions.Flags.OrderBy(flag => flag));
		Assert.Empty(permissions.ExternalOffsets);

		IReadOnlyList<string> bitmaps = permissions.Bitmaps;
		Assert.Equal(2, bitmaps.Count);

		using Permissions decoded = runtime.Blocks.PermissionsFromBitmaps(bitmaps[0], bitmaps[1]);
		Assert.Equal(permissions.Flags, decoded.Flags);
		Assert.Equal(bitmaps, decoded.Bitmaps);
	}

	[Fact]
	public void AnyGrantImpliesAccessExactlyAsTheReferenceRules()
	{
		using var runtime = WasmRuntime.Load();

		// The reference injects ACCESS into every non-empty flag set, so the
		// set reads back with both flags.
		using Permissions owner = runtime.Blocks.PermissionsFromFlags(OwnerFlag);
		Assert.Contains(BaseFlag.Owner, owner.Flags);
		Assert.Contains(BaseFlag.Access, owner.Flags);
	}

	/// <summary>The one base flag the permission-bearing tests grant.</summary>
	private static readonly BaseFlag[] AccessFlag = { BaseFlag.Access };

	/// <summary>The composite flag whose reference expansion the tests assert.</summary>
	private static readonly BaseFlag[] OwnerFlag = { BaseFlag.Owner };

	[Fact]
	public void TheWiderOperationSurfaceBuildsIntoASignedBlock()
	{
		using var runtime = WasmRuntime.Load();
		using Account sender = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, TestSeeds.DefaultAlgorithm);
		using Account counterparty = runtime.Accounts.FromSeed(TestSeeds.Recipient, 0, TestSeeds.DefaultAlgorithm);
		using Account token = runtime.Blocks.NetworkBaseToken(Network);
		using Permissions access = runtime.Blocks.PermissionsFromFlags(AccessFlag);

		using BlockOperation receive = runtime.Blocks.Receive(counterparty, 7, token);
		using BlockOperation setInfo = runtime.Blocks.SetInfo("NAME", "description", "metadata");
		using BlockOperation modify = runtime.Blocks.ModifyPermissions(counterparty, access, AdjustMethod.Add);

		using var builder = runtime.Blocks.NewBuilder();
		builder
			.WithVersion(2)
			.WithNetwork(Network)
			.WithAccount(sender)
			.WithSigner(sender)
			.WithDate(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000))
			.AsOpening()
			.AddOperation(receive)
			.AddOperation(setInfo)
			.AddOperation(modify);

		using Block block = builder.Build();
		Assert.NotEmpty(block.ToBytes());
	}

	[Fact]
	public void IdentifierOperationsDeriveDeterministicIdentifierAccounts()
	{
		using var runtime = WasmRuntime.Load();
		using Account owner = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, TestSeeds.DefaultAlgorithm);
		using Account signerA = runtime.Accounts.FromSeed(TestSeeds.Recipient, 0, TestSeeds.DefaultAlgorithm);

		// Identifier derivation is a pure function of account, kind, chain
		// position, and operation index.
		using Account tokenId = owner.GenerateIdentifier(IdentifierKind.Token);
		using Account tokenIdAgain = owner.GenerateIdentifier(IdentifierKind.Token);
		using Account laterTokenId = owner.GenerateIdentifier(IdentifierKind.Token, index: 1);
		using Account storageId = owner.GenerateIdentifier(IdentifierKind.Storage);

		Assert.Equal(tokenId.PublicKeyString, tokenIdAgain.PublicKeyString);
		Assert.NotEqual(tokenId.PublicKeyString, laterTokenId.PublicKeyString);
		Assert.NotEqual(tokenId.PublicKeyString, storageId.PublicKeyString);

		using BlockOperation claim = runtime.Blocks.CreateIdentifier(tokenId);
		using BlockOperation multisig = runtime.Blocks.CreateMultisig(storageId, new[] { owner, signerA }, quorum: 2);
		using BlockOperation supply = runtime.Blocks.TokenAdminSupply(1_000, AdjustMethod.Add);
	}

	[Fact]
	public async Task TransmitRefusesAClientWithoutABoundNetwork()
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

		// The anchor URL is non-routable, so reaching the transport would
		// surface NODE_STATUS instead: the gate must trip first.
		using KeetaClient client = runtime.CreateKeetaClient(TestSeeds.NonRoutableAnchor);
		KeetaException refused = await Assert.ThrowsAsync<KeetaException>(
			() => client.Transmit(block, cancellationToken: TestContext.Current.CancellationToken));
		Assert.Equal("NETWORK_REQUIRED", refused.Code);
	}

	[Fact]
	public async Task TransmitRefusesAReadOnlyUserClientEvenWithAFeeFactory()
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

		// A custom fee-block factory bypasses the default fee-payer branch,
		// so the signer gate itself must refuse the prebuilt block.
		var options = new TransmitOptions
		{
			FeeBlockFactory = (_, _, _, _) => Task.FromResult<Block?>(null),
		};

		using UserClient readOnly = runtime.CreateUserClient(
			TestSeeds.NonRoutableAnchor, signer: null, network: Network, account: sender);
		KeetaException refused = await Assert.ThrowsAsync<KeetaException>(
			() => readOnly.Transmit(block, options, TestContext.Current.CancellationToken));
		Assert.Equal("SIGNER_REQUIRED", refused.Code);
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
