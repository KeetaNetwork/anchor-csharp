using System.Text.Json;
using KeetaNet.Anchor.Crypto;
using Xunit;

namespace KeetaNet.Anchor.Tests;

/// <summary>
/// Asset-movement model surface: typed blocker decoding from the
/// core's discriminated account-status shape, typed disclaimer and token
/// metadata reads, and the canonical asset transport form.
/// </summary>
public sealed class AssetModelTests
{
	private static readonly string[] ExpectedAttributes = { "fullName", "dateOfBirth" };
	private static readonly string[] ExpectedPrincipals = { "keeta_principal" };

	[Fact]
	public void AccountStatusBlockersDecodeIntoTheirTypedShapes()
	{
		// The exact shape the core's account-status binding emits; its keys
		// arrive in sorted order, so the type discriminator trails the object.
		string payload = """
		{
			"actionRequired": true,
			"blockers": [
				{
					"acceptedIssuers": [[{ "name": "issuer", "value": "keeta_ca" }]],
					"neededAttributes": ["fullName", "dateOfBirth"],
					"shareWithPrincipals": ["keeta_principal"],
					"tosFlow": null,
					"type": "kycShareNeeded"
				},
				{ "toCompleteFlow": { "url": "https://flow.test" }, "type": "additionalKycNeeded" },
				{ "forAsset": "USD", "forRail": "KEETA_SEND", "type": "operationNotSupported" },
				{ "actionsNeeded": [{ "kind": "delegate" }], "type": "userActionNeeded" },
				{ "code": "SOMETHING_ELSE", "message": "boom", "name": "SomeError", "type": "other" }
			]
		}
		""";

		AssetAccountStatus status = JsonSerializer.Deserialize<AssetAccountStatus>(payload, KeetaJson.Options)!;
		Assert.True(status.ActionRequired);
		Assert.Equal(5, status.Blockers!.Count);

		var share = Assert.IsType<AssetKycShareNeededBlocker>(status.Blockers[0]);
		Assert.Equal(JsonValueKind.Null, share.TosFlow.ValueKind);
		Assert.Equal(ExpectedAttributes, share.NeededAttributes);
		Assert.Equal(ExpectedPrincipals, share.ShareWithPrincipals);
		Assert.Equal(JsonValueKind.Array, share.AcceptedIssuers.ValueKind);

		var additional = Assert.IsType<AssetAdditionalKycNeededBlocker>(status.Blockers[1]);
		Assert.Equal("https://flow.test", additional.ToCompleteFlow.GetProperty("url").GetString());

		var unsupported = Assert.IsType<AssetOperationNotSupportedBlocker>(status.Blockers[2]);
		Assert.Equal("USD", unsupported.ForAsset.GetString());
		Assert.Equal("KEETA_SEND", unsupported.ForRail);

		var action = Assert.IsType<AssetUserActionNeededBlocker>(status.Blockers[3]);
		JsonElement needed = Assert.Single(action.ActionsNeeded);
		Assert.Equal("delegate", needed.GetProperty("kind").GetString());

		// An unrecognized anchor error is kept verbatim, never dropped.
		var other = Assert.IsType<AssetOtherBlocker>(status.Blockers[4]);
		Assert.Equal("SomeError", other.Name);
		Assert.Equal("SOMETHING_ELSE", other.Code);
		Assert.Equal("boom", other.Message);
	}

	[Fact]
	public void AnUnknownBlockerTypeRefusesToDecode()
	{
		string payload = """{ "actionRequired": true, "blockers": [{ "type": "mystery" }] }""";

		Assert.Throws<JsonException>(
			() => JsonSerializer.Deserialize<AssetAccountStatus>(payload, KeetaJson.Options));
	}

	[Fact]
	public void LegalDisclaimersDecodeAndSkipMalformedEntries()
	{
		using var runtime = WasmRuntime.Load();
		using Account account = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, TestSeeds.DefaultAlgorithm);
		using AssetMovementClient client = runtime.CreateAssetMovementClient(TestSeeds.NonRoutableAnchor, account.PublicKeyString, account);

		// One well-formed markdown disclaimer and one with an unknown purpose
		AssetProvider provider = Provider(legal: """
		{
			"disclaimers": [
				{ "purpose": "general", "content": { "type": "markdown", "content": "# Terms" } },
				{ "purpose": "unrecognized", "content": { "type": "plaintext", "content": "skip me" } }
			]
		}
		""");

		IReadOnlyList<AssetDisclaimer>? disclaimers = client.GetLegalDisclaimers(provider);
		Assert.NotNull(disclaimers);
		AssetDisclaimer disclaimer = Assert.Single(disclaimers!);
		Assert.Equal(AssetDisclaimerPurpose.General, disclaimer.Purpose);
		Assert.Equal(AssetContentType.Markdown, disclaimer.Content.Type);
		Assert.Equal("# Terms", disclaimer.Content.Content);

		// A provider without legal metadata reports none, not an empty list.
		Assert.Null(client.GetLegalDisclaimers(Provider(legal: null)));
	}

	[Fact]
	public void TokenMetadataDecodesNumberAndStringDecimalPlaces()
	{
		using var runtime = WasmRuntime.Load();
		using Account account = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, TestSeeds.DefaultAlgorithm);
		using AssetMovementClient client = runtime.CreateAssetMovementClient(TestSeeds.NonRoutableAnchor, account.PublicKeyString, account);

		// The reference TokenMetadataJSON publishes decimalPlaces as a number
		// or a numeric string; both must decode, and garbage must read absent.
		AssetProvider provider = Provider(locationMetadata: """
		{
			"chain:evm:100": {
				"assets": {
					"text-places": { "decimalPlaces": "18", "logoURI": "https://logo.test/t.png", "displayName": "Token", "ticker": "$TOK" },
					"numeric-places": { "decimalPlaces": 6 },
					"garbage-places": { "decimalPlaces": "eighteen" }
				}
			}
		}
		""");

		AssetTokenMetadata? full = client.GetAssetMetadataForLocation(provider, "chain:evm:100", "text-places");
		Assert.NotNull(full);
		Assert.Equal(18u, full!.DecimalPlaces);
		Assert.Equal("https://logo.test/t.png", full.LogoUri);
		Assert.Equal("Token", full.DisplayName);
		Assert.Equal("$TOK", full.Ticker);

		AssetTokenMetadata? bare = client.GetAssetMetadataForLocation(provider, "chain:evm:100", "numeric-places");
		Assert.NotNull(bare);
		Assert.Equal(6u, bare!.DecimalPlaces);
		Assert.Null(bare.LogoUri);

		Assert.Null(client.GetAssetMetadataForLocation(provider, "chain:evm:100", "garbage-places"));
		Assert.Null(client.GetAssetMetadataForLocation(provider, "chain:evm:100", "absent-asset"));
		Assert.Null(client.GetAssetMetadataForLocation(provider, "chain:solana:1", "text-places"));
	}

	[Fact]
	public void AssetOrPairRoundTripsItsCanonicalTransportForms()
	{
		// A single asset crosses as a bare string, a conversion as { from, to },
		// exactly what the core's request parser accepts.
		AssetOrPair single = "evm:0x5";
		Assert.Equal("\"evm:0x5\"", JsonSerializer.Serialize(single, KeetaJson.Options));

		AssetOrPair pair = AssetOrPair.Pair("USD", "evm:0x5");
		Assert.Equal("""{"from":"USD","to":"evm:0x5"}""", JsonSerializer.Serialize(pair, KeetaJson.Options));

		Assert.Equal(single, JsonSerializer.Deserialize<AssetOrPair>("\"evm:0x5\"", KeetaJson.Options));
		Assert.Equal(pair, JsonSerializer.Deserialize<AssetOrPair>("""{"from":"USD","to":"evm:0x5"}""", KeetaJson.Options));
	}

	[Fact]
	public void ForwardingAddressesDecodeTheirTypedFeeAndMinimumShapes()
	{
		// The shape the core serializes for one listed address: typed fees,
		// a minimum transfer value, and raw locations that stay opaque.
		string payload = """
		{
			"id": "address-1",
			"address": "keeta_destination",
			"asset": { "from": "USD", "to": "evm:0x5" },
			"sourceLocation": "bank-account:us",
			"destinationLocation": "chain:keeta:100",
			"outgoingRail": "KEETA_SEND",
			"incomingRail": ["ACH_DEBIT"],
			"minimumTransferValue": { "asset": "USD", "value": "500" },
			"fees": {
				"lineItems": [
					{ "purpose": "FIXED", "value": "5", "asset": "USD" },
					{ "purpose": "VALUE_VARIABLE", "basisPoints": 50, "asset": { "id": "evm:0x5", "location": "chain:evm:100" } }
				],
				"total": "10",
				"totalPricedIn": "USD"
			}
		}
		""";

		AssetForwardingAddress address = JsonSerializer.Deserialize<AssetForwardingAddress>(payload, KeetaJson.Options)!;
		Assert.Equal("address-1", address.Id);
		Assert.Equal("keeta_destination", address.Address.GetString());
		Assert.Equal(AssetOrPair.Pair("USD", "evm:0x5"), address.Asset);
		Assert.Equal("KEETA_SEND", address.OutgoingRail);
		Assert.Equal("ACH_DEBIT", Assert.Single(address.IncomingRail!));
		Assert.Equal("USD", address.MinimumTransferValue!.Asset);
		Assert.Equal("500", address.MinimumTransferValue.Value);

		Assert.Equal("10", address.Fees!.Total);
		Assert.Equal(new AssetOrAssetWithLocation("USD"), address.Fees.TotalPricedIn);
		Assert.Equal(2, address.Fees.LineItems.Count);
		Assert.Equal("5", address.Fees.LineItems[0].Value);
		Assert.Null(address.Fees.LineItems[0].BasisPoints);
		Assert.Equal(50d, address.Fees.LineItems[1].BasisPoints);
		Assert.Equal(new AssetOrAssetWithLocation("evm:0x5", "chain:evm:100"), address.Fees.LineItems[1].Asset);
	}

	// A bare id crosses as a string, a located one as { id, location },
	// exactly the reference AssetOrAssetWithLocation union.
	[Theory]
	[InlineData("USD", null, "\"USD\"")]
	[InlineData("evm:0x5", "chain:evm:100", """{"id":"evm:0x5","location":"chain:evm:100"}""")]
	public void LocatedAssetsRoundTripTheirCanonicalTransportForms(string id, string? location, string transport)
	{
		var asset = new AssetOrAssetWithLocation(id, location);

		Assert.Equal(transport, JsonSerializer.Serialize(asset, KeetaJson.Options));
		Assert.Equal(asset, JsonSerializer.Deserialize<AssetOrAssetWithLocation>(transport, KeetaJson.Options));
	}

	[Fact]
	public void AnchorDetailsDecodeAndDropAMalformedDescription()
	{
		using var runtime = WasmRuntime.Load();
		using Account account = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, TestSeeds.DefaultAlgorithm);
		using AssetMovementClient client = runtime.CreateAssetMovementClient(TestSeeds.NonRoutableAnchor, account.PublicKeyString, account);

		AssetProvider provider = Provider(legal: """
		{
			"anchorDetails": {
				"name": "Anchor Under Test",
				"description": { "type": "plaintext", "content": "plain words" },
				"logo": "https://logo.test/a.svg"
			}
		}
		""");

		AssetAnchorDetails? details = client.GetProviderAnchorDetails(provider);
		Assert.NotNull(details);
		Assert.Equal("Anchor Under Test", details!.Name);
		Assert.Equal(AssetContentType.Plaintext, details.Description!.Type);
		Assert.Equal("plain words", details.Description.Content);
		Assert.Equal("https://logo.test/a.svg", details.Logo);

		// A malformed description drops while the identifying fields survive.
		AssetProvider malformed = Provider(legal: """
		{ "anchorDetails": { "name": "Partial", "description": { "type": "unknown-kind", "content": 5 } } }
		""");
		AssetAnchorDetails? partial = client.GetProviderAnchorDetails(malformed);
		Assert.NotNull(partial);
		Assert.Equal("Partial", partial!.Name);
		Assert.Null(partial.Description);
		Assert.Null(partial.Logo);

		// Legal metadata without anchor details reports none.
		Assert.Null(client.GetProviderAnchorDetails(Provider(legal: """{ "disclaimers": [] }""")));
		Assert.Null(client.GetProviderAnchorDetails(Provider(legal: null)));
	}

	/// <summary>A minimal provider carrying only the polymorphic metadata under test.</summary>
	private static AssetProvider Provider(string? legal = null, string? locationMetadata = null)
	{
		JsonElement? legalElement = null;
		if (legal is not null)
		{
			legalElement = JsonSerializer.Deserialize<JsonElement>(legal);
		}

		JsonElement? locationElement = null;
		if (locationMetadata is not null)
		{
			locationElement = JsonSerializer.Deserialize<JsonElement>(locationMetadata);
		}

		return new AssetProvider(
			"provider-under-test",
			new Dictionary<string, AssetEndpoint>(),
			LocationMetadata: locationElement,
			Legal: legalElement);
	}
}
