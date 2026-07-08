using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeetaNet.Anchor;

/// <summary>
/// The authentication an asset-movement operation endpoint requires. Serialized
/// in its lowercase transport form (<c>none</c>/<c>optional</c>/<c>required</c>) by the
/// camelCase enum converter the asset-movement options register.
/// </summary>
public enum AssetEndpointAuth
{
	/// <summary>The endpoint is unauthenticated.</summary>
	None,
	/// <summary>The endpoint accepts, but does not require, a signature.</summary>
	Optional,
	/// <summary>The endpoint requires a signature.</summary>
	Required,
}

/// <summary>One advertised asset-movement operation endpoint.</summary>
public sealed record AssetEndpoint(string Url, AssetEndpointAuth Auth);

/// <summary>
/// An asset-movement provider discovered from on-chain service metadata. The
/// polymorphic <see cref="SupportedAssets"/>, <see cref="LocationMetadata"/>, and
/// <see cref="Legal"/> members are carried as raw JSON so the value round-trips
/// unchanged when handed back to an operation.
/// </summary>
public sealed record AssetProvider(
	string Id,
	IReadOnlyDictionary<string, AssetEndpoint> Operations,
	IReadOnlyList<JsonElement>? SupportedAssets = null,
	JsonElement? LocationMetadata = null,
	JsonElement? Legal = null,
	string? Account = null);

/// <summary>Pagination bounds shared by the list operations.</summary>
public sealed record AssetPagination(uint? Limit = null, uint? Offset = null);

/// <summary>The source of a transfer: a location and an optional provider-specific source.</summary>
public sealed record AssetTransferSource(string Location, object? Source = null);

/// <summary>
/// The destination of a transfer: a location, an optional recipient, and an
/// optional deposit message.
/// </summary>
public sealed record AssetTransferDestination(string Location, object? Recipient = null, string? DepositMessage = null);

/// <summary>A request to simulate or initiate a transfer.</summary>
public sealed record AssetTransferRequest(
	AssetOrPair Asset,
	AssetTransferSource From,
	AssetTransferDestination To,
	string Value,
	IReadOnlyList<string>? AllowedRails = null);

/// <summary>
/// A fiat pull instruction (<c>ACH_DEBIT</c>/<c>CARD_PULL</c>): the rail
/// <paramref name="Type"/> and the persistent address or template reference to
/// pull funds from.
/// </summary>
public sealed record AssetPullInstruction(string Type, object PullFrom);

/// <summary>A request to execute a pull instruction for a transfer.</summary>
public sealed record AssetExecuteRequest(string Id, AssetPullInstruction Instruction);

/// <summary>A request to open a persistent-forwarding template session.</summary>
public sealed record AssetInitiateTemplateRequest(AssetOrPair Asset, string Location);

/// <summary>
/// A request to create a persistent-forwarding template: either a direct
/// template (<paramref name="Asset"/>, <paramref name="Location"/>, and
/// <paramref name="Address"/>) or the completion of a session (<paramref name="Data"/>).
/// </summary>
public sealed record AssetCreateTemplateRequest(
	AssetOrPair? Asset = null,
	string? Location = null,
	object? Address = null,
	string? Id = null,
	object? Data = null);

/// <summary>A request to list persistent-forwarding templates.</summary>
public sealed record AssetListTemplatesRequest(
	IReadOnlyList<string>? Asset = null,
	IReadOnlyList<string>? Location = null);

/// <summary>A request to create a persistent-forwarding address.</summary>
public sealed record AssetCreateAddressRequest(
	string SourceLocation,
	AssetOrPair Asset,
	string? OutgoingRail = null,
	string? IncomingRail = null,
	string? DestinationLocation = null,
	object? DestinationAddress = null,
	string? PersistentAddressTemplateId = null);

/// <summary>One filter over persistent-forwarding addresses.</summary>
public sealed record AssetAddressFilter(
	string? SourceLocation = null,
	string? DestinationLocation = null,
	string? Asset = null,
	string? DestinationAddress = null,
	string? PersistentAddressTemplateId = null);

/// <summary>A request to list persistent-forwarding addresses.</summary>
public sealed record AssetListAddressesRequest(
	IReadOnlyList<AssetAddressFilter>? Search = null,
	AssetPagination? Pagination = null);

/// <summary>A persistent-address filter for listing transactions.</summary>
public sealed record AssetPersistentAddressFilter(
	string Location,
	string? PersistentAddress = null,
	string? PersistentAddressTemplate = null);

/// <summary>A source/destination endpoint filter for listing transactions.</summary>
public sealed record AssetEndpointFilter(string Location, string? UserAddress = null, string? Asset = null);

/// <summary>A specific-transaction filter for listing transactions.</summary>
public sealed record AssetTransactionRef(string Location, object Transaction);

/// <summary>A request to list asset-movement transactions.</summary>
public sealed record AssetListTransactionsRequest(
	IReadOnlyList<AssetPersistentAddressFilter>? PersistentAddresses = null,
	AssetEndpointFilter? From = null,
	AssetEndpointFilter? To = null,
	IReadOnlyList<AssetTransactionRef>? Transactions = null,
	AssetPagination? Pagination = null);

/// <summary>A request to share KYC attributes with the provider.</summary>
public sealed record AssetShareKycRequest(string Attributes, object? TosAgreement = null);

/// <summary>
/// A transfer search: an optional asset, the endpoints value must move
/// between, and the directional rails each endpoint must advertise.
/// </summary>
public sealed record AssetProviderSearch(
	AssetOrPair? Asset = null,
	string? From = null,
	string? To = null,
	IReadOnlyList<string>? InboundRails = null,
	IReadOnlyList<string>? OutboundRails = null);

/// <summary>The transport shape of an initiated transfer decoded from the core.</summary>
internal sealed record AssetTransferTransport(string Id, IReadOnlyList<JsonElement> InstructionChoices);

/// <summary>The transport shape of a simulated transfer decoded from the core.</summary>
internal sealed record AssetSimulatedTransferTransport(IReadOnlyList<JsonElement> InstructionChoices);

/// <summary>A transfer's status: the underlying transaction record.</summary>
public sealed record AssetTransferStatus(JsonElement Transaction);

/// <summary>A persistent-forwarding template session opened by an initiate call.</summary>
public sealed record AssetTemplateSession(string Id, string ExpiresAt, JsonElement Data);

/// <summary>A created persistent-forwarding template.</summary>
public sealed record AssetForwardingTemplate(string Id, JsonElement Location, JsonElement Asset, JsonElement Address);

/// <summary>A page of persistent-forwarding templates.</summary>
public sealed record AssetTemplatePage(IReadOnlyList<JsonElement> Templates, string Total);

/// <summary>A page of persistent-forwarding addresses.</summary>
public sealed record AssetAddressPage(IReadOnlyList<JsonElement> Addresses, string Total);

/// <summary>A page of asset-movement transactions.</summary>
public sealed record AssetTransactionPage(IReadOnlyList<JsonElement> Transactions, string Total);

/// <summary>The outcome of a share-KYC request.</summary>
public sealed record AssetShareKycOutcome(
	bool IsPending,
	[property: JsonPropertyName("promiseURL")] string? PromiseUrl);

/// <summary>
/// The signer's readiness with a provider: <see cref="ActionRequired"/> and, when
/// set, the typed <see cref="Blockers"/> the caller must resolve first.
/// </summary>
public sealed record AssetAccountStatus(bool ActionRequired, IReadOnlyList<AssetMovementBlocker>? Blockers = null);

/// <summary>Why a provider publishes a disclaimer; the reference schema defines only <c>general</c>.</summary>
public enum AssetDisclaimerPurpose
{
	/// <summary>A general disclaimer.</summary>
	General,
}

/// <summary>How a disclaimer body is encoded.</summary>
public enum AssetContentType
{
	/// <summary>Markdown the client may render.</summary>
	Markdown,
	/// <summary>Plain text the client shows verbatim.</summary>
	Plaintext,
}

/// <summary>
/// Content a client may render directly (the reference
/// <c>ClientRenderableContent</c>): markdown or plain text with no display
/// guarantees, so it must carry context only, never critical information.
/// </summary>
public sealed record AssetRenderableContent(AssetContentType Type, string Content);

/// <summary>One legal disclaimer a provider publishes under its <c>legal</c> metadata.</summary>
public sealed record AssetDisclaimer(AssetDisclaimerPurpose Purpose, AssetRenderableContent Content);

/// <summary>
/// The token metadata a provider advertises for one asset at one location
/// (the reference <c>AnchorTokenLocationMetadata</c>).
/// </summary>
public sealed record AssetTokenMetadata(
	[property: JsonConverter(typeof(FlexibleUIntConverter))] uint DecimalPlaces,
	[property: JsonPropertyName("logoURI")] string? LogoUri = null,
	string? DisplayName = null,
	string? Ticker = null);

/// <summary>
/// Reads a count published as a JSON number or a numeric string; the
/// reference <c>TokenMetadataJSON</c> allows both for <c>decimalPlaces</c>.
/// </summary>
internal sealed class FlexibleUIntConverter : JsonConverter<uint>
{
	public override uint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType != JsonTokenType.String)
		{
			return reader.GetUInt32();
		}

		string text = reader.GetString() ?? "";
		if (!uint.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed))
		{
			throw new JsonException($"'{text}' is not a numeric count");
		}

		return parsed;
	}

	public override void Write(Utf8JsonWriter writer, uint value, JsonSerializerOptions options) =>
		writer.WriteNumberValue(value);
}

/// <summary>
/// Typed access to the polymorphic asset-movement address surface
/// (bank-account/mobile-wallet, each resolved or obfuscated). Addresses cross
/// the wasm boundary as raw JSON so they round-trip unchanged.
/// </summary>
public static class AssetAddress
{
	/// <summary>Decode <paramref name="address"/> into the generated typed model.</summary>
	public static Generated.AddressTypes Parse(JsonElement address)
	{
		string json = address.GetRawText();
		return Generated.AddressTypes.FromJson(json) ?? throw new KeetaException("DECODE", "could not decode an asset-movement address");
	}

	/// <summary>
	/// Try to decode <paramref name="address"/> into the generated typed model,
	/// returning false when the JSON does not match a known address shape.
	/// </summary>
	public static bool TryParse(JsonElement address, out Generated.AddressTypes? parsed)
	{
		string json = address.GetRawText();
		try
		{
			parsed = Generated.AddressTypes.FromJson(json);
			return parsed is not null;
		}
		catch (JsonException)
		{
			parsed = null;
			return false;
		}
	}
}
