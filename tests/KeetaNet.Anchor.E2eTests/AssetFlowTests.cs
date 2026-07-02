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

	[Fact]
	public async Task DiscoveryReadsThePublishedProvider()
	{
		using var session = AssetSession.Open();
		(AssetMovementClient client, AssetAnchor anchor, CancellationToken cancellationToken) = session;

		IReadOnlyList<AssetProvider> providers = await client.ProvidersAsync(cancellationToken);
		AssetProvider provider = Assert.Single(providers);
		Assert.Equal(anchor.ProviderId, provider.Id);
		Assert.True(client.IsOperationSupported(provider, "simulateTransfer"));

		AssetProvider? byAccount = await client.ProviderByAccountAsync(anchor.Signer, cancellationToken);
		Assert.NotNull(byAccount);

		var advertised = new AssetProviderSearch(anchor.Asset, EvmLocation, KeetaLocation);
		IReadOnlyList<AssetProvider> matches = await client.GetProvidersForTransferAsync(advertised, cancellationToken);
		Assert.Single(matches);

		var unadvertised = new AssetProviderSearch("evm:0xdeadbeef");
		IReadOnlyList<AssetProvider> none = await client.GetProvidersForTransferAsync(unadvertised, cancellationToken);
		Assert.Empty(none);

		session.Shutdown();
	}

	[Fact]
	public async Task TransfersRunEndToEndAgainstTheLiveAnchor()
	{
		using var session = AssetSession.Open();
		(AssetMovementClient client, AssetAnchor anchor, CancellationToken cancellationToken) = session;
		AssetProvider provider = await session.DiscoveredProviderAsync();

		AssetAccountStatus status = await client.AccountStatusAsync(provider, cancellationToken);
		Assert.False(status.ActionRequired);

		AssetSimulatedTransfer simulated = await client.SimulateTransferAsync(provider, PushTransfer(anchor, anchor.SendToAddress), cancellationToken);
		JsonElement simulatedInstruction = Assert.Single(simulated.InstructionChoices);
		Assert.Equal("KEETA_SEND", simulatedInstruction.GetProperty("type").GetString());

		AssetTransfer transfer = await client.InitiateTransferAsync(provider, PushTransfer(anchor, anchor.SendToAddress), cancellationToken);
		Assert.Equal("123", transfer.Id);
		Assert.Equal(
			anchor.SendToAddress,
			transfer.InstructionChoices[0].GetProperty("sendToAddress").GetString());

		await Assert.ThrowsAsync<KeetaException>(
			() => client.InitiateTransferAsync(provider, PushTransfer(anchor, recipient: null), cancellationToken));

		AssetTransferStatus completed = await transfer.GetStatusAsync(cancellationToken);
		Assert.Equal("123", completed.Transaction.GetProperty("id").GetString());
		Assert.Equal("COMPLETED", completed.Transaction.GetProperty("status").GetString());

		AssetTransfer pull = await client.InitiateTransferAsync(provider, PullTransfer(anchor), cancellationToken);
		JsonElement pullInstruction = Assert.Single(pull.InstructionChoices);
		Assert.Equal("ACH_DEBIT", pullInstruction.GetProperty("type").GetString());

		var instruction = new AssetPullInstruction("ACH_DEBIT", pullInstruction.GetProperty("pullFrom"));
		AssetTransferStatus executed = await pull.ExecuteAsync(instruction, cancellationToken);
		Assert.Equal("EXECUTED", executed.Transaction.GetProperty("status").GetString());

		session.Shutdown();
	}

	[Fact]
	public async Task ForwardingAndListingRunAgainstTheLiveAnchor()
	{
		using var session = AssetSession.Open();
		(AssetMovementClient client, AssetAnchor anchor, CancellationToken cancellationToken) = session;
		AssetProvider provider = await session.DiscoveredProviderAsync();

		AssetTemplateSession templateSession = await client.InitiateForwardingTemplateAsync(
			provider, new AssetInitiateTemplateRequest(anchor.Asset, EvmLocation), cancellationToken);
		Assert.Equal("test-session-id", templateSession.Id);
		Assert.Equal("link-sandbox-test-token", templateSession.Data.GetProperty("plaidLinkToken").GetString());

		AssetForwardingTemplate template = await client.CreateForwardingTemplateAsync(
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
		AssetForwardingTemplate completed = await client.CreateForwardingTemplateAsync(
			provider, new AssetCreateTemplateRequest(Id: templateSession.Id, Data: completionData), cancellationToken);
		Assert.Equal("template-id", completed.Id);

		AssetTemplatePage templates = await client.ListForwardingTemplatesAsync(
			provider,
			new AssetListTemplatesRequest(new[] { anchor.Asset }, new[] { EvmLocation }),
			cancellationToken);
		Assert.Single(templates.Templates);
		Assert.Equal("1", templates.Total);

		JsonElement created = await client.CreateForwardingAddressAsync(
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

		JsonElement fromTemplate = await client.CreateForwardingAddressAsync(
			provider,
			new AssetCreateAddressRequest(EvmLocation, anchor.Asset, PersistentAddressTemplateId: template.Id),
			cancellationToken);
		Assert.Equal(anchor.SendToAddress, fromTemplate.GetProperty("address").GetString());

		AssetAddressPage addresses = await client.ListForwardingAddressesAsync(
			provider,
			new AssetListAddressesRequest(
				new[] { new AssetAddressFilter(SourceLocation: EvmLocation, Asset: anchor.Asset) },
				new AssetPagination(10, 0)),
			cancellationToken);
		Assert.Single(addresses.Addresses);
		Assert.Equal("1", addresses.Total);

		AssetTransactionPage transactions = await client.ListTransactionsAsync(
			provider,
			new AssetListTransactionsRequest(
				new[] { new AssetPersistentAddressFilter(EvmLocation, anchor.SendToAddress) },
				new AssetEndpointFilter(EvmLocation, anchor.SendToAddress, anchor.Asset),
				Pagination: new AssetPagination(10)),
			cancellationToken);
		JsonElement transaction = Assert.Single(transactions.Transactions);
		Assert.Equal("123", transaction.GetProperty("id").GetString());

		await client.DeactivateForwardingTemplateAsync(provider, template.Id, cancellationToken);
		await client.DeactivateForwardingAddressAsync(provider, template.Id, cancellationToken);

		await Assert.ThrowsAsync<KeetaException>(
			() => client.DeactivateForwardingTemplateAsync(provider, "does-not-exist", cancellationToken));

		// An operation the provider does not advertise must surface a typed
		// error before any request leaves the client.
		Dictionary<string, AssetEndpoint> narrowedOperations = provider.Operations
			.Where(operation => operation.Key != "listTransactions")
			.ToDictionary(operation => operation.Key, operation => operation.Value);
		AssetProvider narrowed = provider with { Operations = narrowedOperations };
		await Assert.ThrowsAsync<KeetaException>(
			() => client.ListTransactionsAsync(narrowed, new AssetListTransactionsRequest(), cancellationToken));

		session.Shutdown();
	}

	[Fact]
	public async Task ShareKycSettlesAndPollsAgainstTheLiveAnchor()
	{
		using var session = AssetSession.Open();
		(AssetMovementClient client, _, CancellationToken cancellationToken) = session;
		AssetProvider provider = await session.DiscoveredProviderAsync();

		AssetShareKycOutcome settled = await client.ShareKycAsync(
			provider, new AssetShareKycRequest("exported-attributes"), cancellationToken);
		Assert.False(settled.IsPending);

		AssetShareKycOutcome withoutPolling = await client.ShareKycAndWaitAsync(
			provider, new AssetShareKycRequest("exported-attributes"), cancellationToken: cancellationToken);
		Assert.False(withoutPolling.IsPending);

		// The promise route reports pending (202 + Retry-After) for the first
		// two polls and settles on the third.
		AssetShareKycOutcome polled = await client.ShareKycAndWaitAsync(
			provider,
			new AssetShareKycRequest("promise-flow"),
			pollInterval: TimeSpan.FromMilliseconds(1),
			timeout: TimeSpan.FromMinutes(1),
			cancellationToken: cancellationToken);
		Assert.False(polled.IsPending);

		await Assert.ThrowsAsync<KeetaException>(
			() => client.ShareKycAndWaitAsync(
				provider,
				new AssetShareKycRequest("promise-stall"),
				pollInterval: TimeSpan.FromSeconds(1),
				timeout: TimeSpan.FromMilliseconds(500),
				cancellationToken: cancellationToken));

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
			new { from = "USD", to = anchor.Asset },
			new AssetTransferSource(
				BankLocation,
				new { type = "persistent-address", persistentAddressId = "TEST_PERSISTENT_ADDRESS_ID" }),
			new AssetTransferDestination(KeetaLocation, anchor.SendToAddress, "integration"),
			"100");
}
