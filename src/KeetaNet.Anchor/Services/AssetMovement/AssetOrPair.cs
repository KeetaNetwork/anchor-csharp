using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeetaNet.Anchor;

/// <summary>
/// A single movable asset or a <c>{ from, to }</c> conversion pair, each named
/// by its canonical string: an ISO currency code, a <c>$</c>-prefixed custom
/// currency, a Keeta token public key, or an external-chain asset.
/// </summary>
[JsonConverter(typeof(AssetOrPairConverter))]
public sealed record AssetOrPair
{
	private AssetOrPair(string? asset, string? from, string? to)
	{
		Asset = asset;
		From = from;
		To = to;
	}

	/// <summary>The canonical asset when this names one asset, null for a pair.</summary>
	public string? Asset { get; }

	/// <summary>The source asset when this is a conversion pair, null for a single asset.</summary>
	public string? From { get; }

	/// <summary>The destination asset when this is a conversion pair, null for a single asset.</summary>
	public string? To { get; }

	/// <summary>One asset, moved from and to the same denomination.</summary>
	public static implicit operator AssetOrPair(string asset) => new(asset, null, null);

	/// <summary>A conversion pair: <paramref name="from"/> is exchanged into <paramref name="to"/>.</summary>
	public static AssetOrPair Pair(string from, string to) => new(null, from, to);
}

/// <summary>
/// Writes the canonical transport form (a bare string or <c>{ from, to }</c>)
/// and reads either back.
/// </summary>
internal sealed class AssetOrPairConverter : JsonConverter<AssetOrPair>
{
	public override AssetOrPair Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType == JsonTokenType.String)
		{
			return reader.GetString() ?? "";
		}

		using var document = JsonDocument.ParseValue(ref reader);
		JsonElement element = document.RootElement;
		string? from = element.GetProperty("from").GetString();
		string? to = element.GetProperty("to").GetString();
		if (from is null || to is null)
		{
			throw new JsonException("an asset pair requires string 'from' and 'to' members");
		}

		return AssetOrPair.Pair(from, to);
	}

	public override void Write(Utf8JsonWriter writer, AssetOrPair value, JsonSerializerOptions options)
	{
		if (value.Asset is { } asset)
		{
			writer.WriteStringValue(asset);
			return;
		}

		writer.WriteStartObject();
		writer.WriteString("from", value.From);
		writer.WriteString("to", value.To);
		writer.WriteEndObject();
	}
}
