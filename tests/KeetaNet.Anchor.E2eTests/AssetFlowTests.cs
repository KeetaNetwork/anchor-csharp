using System.Text.Json;
using KeetaNet.Anchor.Crypto;
using Xunit;

namespace KeetaNet.Anchor.E2eTests;

/// <summary>
/// The full asset-movement client path against a live reference anchor:
/// discover the provider from on-chain metadata through the real node API,
/// then drive every advertised operation end to end.
/// </summary>
public sealed class AssetFlowTests
{
	private const string BankLocation = "bank-account:us";
	private const string EvmLocation = "chain:evm:100";
	private const string KeetaLocation = "chain:keeta:100";
	private const string EvmAsset = "evm:0xc0634090F2Fe6c6d75e61Be2b949464aBB498973";

	private static readonly string[] BlockedAttributes = { "fullName", "dateOfBirth" };

	[Fact]
	public async Task DiscoveryReadsThePublishedProvider()
	{
		using var session = AssetSession.Open();
		(AssetMovementClient client, AssetAnchor anchor, CancellationToken cancellationToken) = session;

		IReadOnlyList<AssetProvider> providers = await client.GetProviders(cancellationToken);
		AssetProvider provider = Assert.Single(providers);
		Assert.Equal(anchor.ProviderId, provider.Id);
		Assert.True(client.IsOperationSupported(provider, "simulateTransfer"));

		// The Account overload resolves the public-key string itself, so one
		// call covers both lookup forms.
		using Account metadataSigner = session.Runtime.Accounts.FromPublicKeyString(anchor.Signer);
		AssetProvider? byAccount = await client.GetProviderByAccount(metadataSigner, cancellationToken);
		Assert.NotNull(byAccount);
		Assert.Equal(provider.Id, byAccount!.Id);

		var advertised = new AssetProviderSearch(anchor.Asset, EvmLocation, KeetaLocation);
		IReadOnlyList<AssetProvider> matches = await client.GetProvidersForTransfer(advertised, cancellationToken);
		Assert.Single(matches);

		var unadvertised = new AssetProviderSearch("evm:0xdeadbeef");
		IReadOnlyList<AssetProvider> none = await client.GetProvidersForTransfer(unadvertised, cancellationToken);
		Assert.Empty(none);

		session.Shutdown();
	}

	[Fact]
	public async Task TransfersRunEndToEndAgainstTheLiveAnchor()
	{
		using var session = AssetSession.Open();
		(AssetMovementClient client, AssetAnchor anchor, CancellationToken cancellationToken) = session;
		AssetProvider provider = await session.DiscoveredProviderAsync();

		AssetAccountStatus status = await client.GetAccountStatus(provider, cancellationToken);
		Assert.False(status.ActionRequired);

		AssetSimulatedTransfer simulated = await client.SimulateTransfer(provider, PushTransfer(anchor, anchor.SendToAddress), cancellationToken);
		JsonElement simulatedInstruction = Assert.Single(simulated.InstructionChoices);
		Assert.Equal("KEETA_SEND", simulatedInstruction.GetProperty("type").GetString());

		// Promoting the simulation initiates with the simulated request: the
		// provider reports the recipient that request carried.
		AssetTransfer promoted = await simulated.CreateTransfer(cancellationToken: cancellationToken);
		Assert.Equal("123", promoted.Id);
		Assert.Equal(
			$"123:{anchor.SendToAddress}",
			Assert.Single(promoted.InstructionChoices).GetProperty("external").GetString());

		// A recipient supplied at promotion overrides the simulated one.
		AssetTransfer redirected = await simulated.CreateTransfer(anchor.Signer, "integration", cancellationToken);
		Assert.Equal(
			$"123:{anchor.Signer}",
			Assert.Single(redirected.InstructionChoices).GetProperty("external").GetString());

		AssetTransfer transfer = await client.InitiateTransfer(provider, PushTransfer(anchor, anchor.SendToAddress), cancellationToken);
		Assert.Equal("123", transfer.Id);
		Assert.Equal(
			anchor.SendToAddress,
			transfer.InstructionChoices[0].GetProperty("sendToAddress").GetString());

		await Assert.ThrowsAsync<KeetaException>(
			() => client.InitiateTransfer(provider, PushTransfer(anchor, recipient: null), cancellationToken));

		AssetTransferStatus completed = await transfer.GetTransferStatus(cancellationToken);
		Assert.Equal("123", completed.Transaction.GetProperty("id").GetString());
		Assert.Equal("COMPLETED", completed.Transaction.GetProperty("status").GetString());

		AssetTransfer pull = await client.InitiateTransfer(provider, PullTransfer(anchor), cancellationToken);
		JsonElement pullInstruction = Assert.Single(pull.InstructionChoices);
		Assert.Equal("ACH_DEBIT", pullInstruction.GetProperty("type").GetString());

		var instruction = new AssetPullInstruction("ACH_DEBIT", pullInstruction.GetProperty("pullFrom"));
		AssetTransferStatus executed = await pull.ExecuteTransfer(instruction, cancellationToken);
		Assert.Equal("EXECUTED", executed.Transaction.GetProperty("status").GetString());

		session.Shutdown();
	}

	[Fact]
	public async Task AccountStatusServesTypedBlockersForABlockedCaller()
	{
		using var session = AssetSession.Open(blockCaller: true);
		(AssetMovementClient client, AssetAnchor anchor, CancellationToken cancellationToken) = session;
		AssetProvider provider = await session.DiscoveredProviderAsync();

		AssetAccountStatus status = await client.GetAccountStatus(provider, cancellationToken);
		Assert.True(status.ActionRequired);
		Assert.Equal(2, status.Blockers!.Count);

		var share = Assert.IsType<AssetKycShareNeededBlocker>(status.Blockers[0]);
		Assert.Equal(JsonValueKind.Null, share.TosFlow.ValueKind);
		Assert.Equal(BlockedAttributes, share.NeededAttributes);
		Assert.Equal(new[] { anchor.SendToAddress }, share.ShareWithPrincipals);

		using JsonElement.ArrayEnumerator issuerSets = share.AcceptedIssuers.EnumerateArray();
		JsonElement issuerSet = Assert.Single(issuerSets);
		using JsonElement.ArrayEnumerator issuers = issuerSet.EnumerateArray();
		JsonElement issuer = Assert.Single(issuers);
		Assert.Equal("CN", issuer.GetProperty("name").GetString());
		Assert.Equal("Anchor Test CA", issuer.GetProperty("value").GetString());

		var unsupported = Assert.IsType<AssetOperationNotSupportedBlocker>(status.Blockers[1]);
		Assert.Equal(anchor.Asset, unsupported.ForAsset.GetString());
		Assert.Equal("ACH_DEBIT", unsupported.ForRail);

		session.Shutdown();
	}

	[Fact]
	public async Task PublishedLegalAndTokenMetadataRoundTrip()
	{
		using var session = AssetSession.Open();
		(AssetMovementClient client, AssetAnchor anchor, CancellationToken cancellationToken) = session;
		AssetProvider provider = await session.DiscoveredProviderAsync();

		IReadOnlyList<AssetDisclaimer>? disclaimers = client.GetLegalDisclaimers(provider);
		Assert.NotNull(disclaimers);
		AssetDisclaimer disclaimer = Assert.Single(disclaimers!);
		Assert.Equal(AssetDisclaimerPurpose.General, disclaimer.Purpose);
		Assert.Equal(AssetContentType.Markdown, disclaimer.Content.Type);
		Assert.Equal("Test disclaimer: use at your own risk.", disclaimer.Content.Content);

		// Looking the provider up by id serves the same disclaimers.
		IReadOnlyList<AssetDisclaimer>? byId = await client.GetProviderLegalDisclaimersById(anchor.ProviderId, cancellationToken);
		Assert.Equal(disclaimers, byId);

		AssetTokenMetadata? metadata = client.GetAssetMetadataForLocation(provider, EvmLocation, EvmAsset);
		Assert.NotNull(metadata);
		Assert.Equal(18u, metadata!.DecimalPlaces);
		Assert.Equal("Test Token", metadata.DisplayName);
		Assert.Equal("$TEST", metadata.Ticker);
		Assert.Equal("https://token.test/logo.png", metadata.LogoUri);

		// An asset the anchor publishes no display metadata for reports absent,
		// not an error.
		Assert.Null(client.GetAssetMetadataForLocation(provider, EvmLocation, "evm:0xdeadbeef"));

		session.Shutdown();
	}

	[Fact]
	public async Task ForwardingAndListingRunAgainstTheLiveAnchor()
	{
		using var session = AssetSession.Open();
		(AssetMovementClient client, AssetAnchor anchor, CancellationToken cancellationToken) = session;
		AssetProvider provider = await session.DiscoveredProviderAsync();

		AssetTemplateSession templateSession = await client.InitiatePersistentForwardingTemplate(
			provider, new AssetInitiateTemplateRequest(anchor.Asset, EvmLocation), cancellationToken);
		Assert.Equal("test-session-id", templateSession.Id);
		Assert.Equal("link-sandbox-test-token", templateSession.Data.GetProperty("plaidLinkToken").GetString());

		AssetForwardingTemplate template = await client.CreatePersistentForwardingTemplate(
			provider,
			new AssetCreateTemplateRequest(Asset: anchor.Asset, Location: EvmLocation, Address: anchor.SendToAddress),
			cancellationToken);
		Assert.Equal("template-id", template.Id);

		var completionData = new
		{
			type = "plaid",
			plaidPublicToken = "public-sandbox-token",
			plaidAccountId = "account-1",
		};
		AssetForwardingTemplate completed = await client.CreatePersistentForwardingTemplate(
			provider, new AssetCreateTemplateRequest(Id: templateSession.Id, Data: completionData), cancellationToken);
		Assert.Equal("template-id", completed.Id);

		AssetTemplatePage templates = await client.ListForwardingAddressTemplates(
			provider,
			new AssetListTemplatesRequest(new[] { anchor.Asset }, new[] { EvmLocation }),
			cancellationToken);
		Assert.Single(templates.Templates);
		Assert.Equal("1", templates.Total);

		JsonElement created = await client.CreatePersistentForwardingAddress(
			provider,
			new AssetCreateAddressRequest(
				EvmLocation,
				anchor.Asset,
				OutgoingRail: "KEETA_SEND",
				DestinationLocation: KeetaLocation,
				DestinationAddress: anchor.SendToAddress),
			cancellationToken);
		Assert.Equal(anchor.SendToAddress, created.GetProperty("address").GetString());
		Assert.Equal("10", created.GetProperty("fees").GetProperty("total").GetString());

		JsonElement fromTemplate = await client.CreatePersistentForwardingAddress(
			provider,
			new AssetCreateAddressRequest(EvmLocation, anchor.Asset, PersistentAddressTemplateId: template.Id),
			cancellationToken);
		Assert.Equal(anchor.SendToAddress, fromTemplate.GetProperty("address").GetString());

		AssetAddressPage addresses = await client.ListForwardingAddresses(
			provider,
			new AssetListAddressesRequest(
				new[] { new AssetAddressFilter(SourceLocation: EvmLocation, Asset: anchor.Asset) },
				new AssetPagination(10, 0)),
			cancellationToken);
		Assert.Single(addresses.Addresses);
		Assert.Equal("1", addresses.Total);

		AssetTransactionPage transactions = await client.ListTransactions(
			provider,
			new AssetListTransactionsRequest(
				new[] { new AssetPersistentAddressFilter(EvmLocation, anchor.SendToAddress) },
				new AssetEndpointFilter(EvmLocation, anchor.SendToAddress, anchor.Asset),
				Pagination: new AssetPagination(10)),
			cancellationToken);
		JsonElement transaction = Assert.Single(transactions.Transactions);
		Assert.Equal("123", transaction.GetProperty("id").GetString());

		await client.DeactivatePersistentForwardingTemplate(provider, template.Id, cancellationToken);
		await client.DeactivatePersistentForwardingAddress(provider, template.Id, cancellationToken);

		await Assert.ThrowsAsync<KeetaException>(
			() => client.DeactivatePersistentForwardingTemplate(provider, "does-not-exist", cancellationToken));

		// An operation the provider does not advertise must surface a typed
		// error before any request leaves the client.
		Dictionary<string, AssetEndpoint> narrowedOperations = provider.Operations
			.Where(operation => operation.Key != "listTransactions")
			.ToDictionary(operation => operation.Key, operation => operation.Value);
		AssetProvider narrowed = provider with { Operations = narrowedOperations };
		await Assert.ThrowsAsync<KeetaException>(
			() => client.ListTransactions(narrowed, new AssetListTransactionsRequest(), cancellationToken));

		session.Shutdown();
	}

	[Fact]
	public async Task ShareKycSettlesAndPollsAgainstTheLiveAnchor()
	{
		using var session = AssetSession.Open();
		(AssetMovementClient client, _, CancellationToken cancellationToken) = session;
		AssetProvider provider = await session.DiscoveredProviderAsync();

		AssetShareKycOutcome settled = await client.ShareKycAttributes(
			provider, new AssetShareKycRequest("exported-attributes"), cancellationToken);
		Assert.False(settled.IsPending);

		AssetShareKycOutcome withoutPolling = await client.ShareKycAttributesAndWait(
			provider, new AssetShareKycRequest("exported-attributes"), cancellationToken: cancellationToken);
		Assert.False(withoutPolling.IsPending);

		// The promise route reports pending (202 + Retry-After) for the first
		// two polls and settles on the third.
		AssetShareKycOutcome polled = await client.ShareKycAttributesAndWait(
			provider,
			new AssetShareKycRequest("promise-flow"),
			pollInterval: TimeSpan.FromMilliseconds(1),
			timeout: TimeSpan.FromMinutes(1),
			cancellationToken: cancellationToken);
		Assert.False(polled.IsPending);

		await Assert.ThrowsAsync<KeetaException>(
			() => client.ShareKycAttributesAndWait(
				provider,
				new AssetShareKycRequest("promise-stall"),
				pollInterval: TimeSpan.FromSeconds(1),
				timeout: TimeSpan.FromMilliseconds(500),
				cancellationToken: cancellationToken));

		session.Shutdown();
	}

	[Fact]
	public async Task ARefusedShareSurfacesTheTypedKycBlocker()
	{
		using var session = AssetSession.Open();
		(AssetMovementClient client, AssetAnchor anchor, CancellationToken cancellationToken) = session;
		AssetProvider provider = await session.DiscoveredProviderAsync();

		// The anchor refuses the magic attributes with a 403 blocker envelope
		KeetaBlockerException refusal = await Assert.ThrowsAsync<KeetaBlockerException>(
			() => client.ShareKycAttributes(provider, new AssetShareKycRequest("blocked"), cancellationToken));

		Assert.Equal("KEETA_ANCHOR_ASSET_MOVEMENT_KYC_SHARE_NEEDED", refusal.Code);
		var share = Assert.IsType<AssetKycShareNeededBlocker>(refusal.Blocker);
		Assert.Equal(BlockedAttributes, share.NeededAttributes);
		Assert.Equal(new[] { anchor.SendToAddress }, share.ShareWithPrincipals);

		session.Shutdown();
	}

	/// <summary>A push transfer moving the base token from the EVM location to Keeta.</summary>
	private static AssetTransferRequest PushTransfer(AssetAnchor anchor, object? recipient) =>
		new(
			anchor.Asset,
			new AssetTransferSource(EvmLocation),
			new AssetTransferDestination(KeetaLocation, recipient),
			"100");

	/// <summary>A pull transfer debiting a persistent bank address into the base token.</summary>
	private static AssetTransferRequest PullTransfer(AssetAnchor anchor) =>
		new(
			AssetOrPair.Pair("USD", anchor.Asset),
			new AssetTransferSource(
				BankLocation,
				new { type = "persistent-address", persistentAddressId = "TEST_PERSISTENT_ADDRESS_ID" }),
			new AssetTransferDestination(KeetaLocation, anchor.SendToAddress, "integration"),
			"100");
}
