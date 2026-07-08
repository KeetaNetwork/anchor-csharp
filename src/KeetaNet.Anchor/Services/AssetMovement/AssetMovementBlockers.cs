using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeetaNet.Anchor;

/// <summary>
/// A blocker an anchor reports that a user must resolve before an
/// asset-movement operation can proceed. The core rehydrates each anchor
/// error envelope into one of the typed shapes below. Anything it does not
/// recognize arrives unchanged as <see cref="AssetOtherBlocker"/> so no
/// information is lost. Decoded by the converter <see cref="KeetaJson"/>
/// registers, since only the core produces the shape.
/// </summary>
public abstract record AssetMovementBlocker;

/// <summary>The user must share KYC attributes before proceeding.</summary>
/// <remarks>
/// The provider-polymorphic members (<see cref="TosFlow"/>,
/// <see cref="AcceptedIssuers"/>) cross as raw JSON, a null value marked
/// <see cref="JsonValueKind.Null"/>, so they round-trip unchanged.
/// </remarks>
public sealed record AssetKycShareNeededBlocker(
	JsonElement TosFlow,
	IReadOnlyList<string>? NeededAttributes,
	IReadOnlyList<string> ShareWithPrincipals,
	JsonElement AcceptedIssuers) : AssetMovementBlocker;

/// <summary>The user must complete additional KYC steps.</summary>
public sealed record AssetAdditionalKycNeededBlocker(JsonElement ToCompleteFlow) : AssetMovementBlocker;

/// <summary>The requested operation is not supported for the given asset or rail.</summary>
public sealed record AssetOperationNotSupportedBlocker(JsonElement ForAsset, string? ForRail) : AssetMovementBlocker;

/// <summary>The user must take one or more on-ledger actions.</summary>
public sealed record AssetUserActionNeededBlocker(IReadOnlyList<JsonElement> ActionsNeeded) : AssetMovementBlocker;

/// <summary>Any other anchor error, kept unchanged.</summary>
public sealed record AssetOtherBlocker(string Name, string? Code, string Message) : AssetMovementBlocker;

/// <summary>
/// Decodes a blocker from the core's <c>type</c>-discriminated JSON. A manual
/// switch, not <c>[JsonPolymorphic]</c>: the core writes its keys in sorted
/// order, so the discriminator is not guaranteed to lead the object.
/// </summary>
internal sealed class AssetMovementBlockerConverter : JsonConverter<AssetMovementBlocker>
{
	/// <summary>
	/// Match only the abstract base, so the derived-type deserialization the
	/// decoder performs never re-enters this converter.
	/// </summary>
	public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(AssetMovementBlocker);

	public override AssetMovementBlocker Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		using var document = JsonDocument.ParseValue(ref reader);
		JsonElement element = document.RootElement;
		string? type = element.GetProperty("type").GetString();

		return type switch
		{
			"kycShareNeeded" => Decode<AssetKycShareNeededBlocker>(element, options),
			"additionalKycNeeded" => Decode<AssetAdditionalKycNeededBlocker>(element, options),
			"operationNotSupported" => Decode<AssetOperationNotSupportedBlocker>(element, options),
			"userActionNeeded" => Decode<AssetUserActionNeededBlocker>(element, options),
			"other" => Decode<AssetOtherBlocker>(element, options),
			_ => throw new JsonException($"unknown asset-movement blocker type '{type}'"),
		};
	}

	public override void Write(Utf8JsonWriter writer, AssetMovementBlocker value, JsonSerializerOptions options) =>
		throw new NotSupportedException("blockers are read from the core, never written");

	private static AssetMovementBlocker Decode<T>(JsonElement element, JsonSerializerOptions options)
		where T : AssetMovementBlocker =>
		element.Deserialize<T>(options)
		?? throw new JsonException($"could not decode a {typeof(T).Name}");
}
