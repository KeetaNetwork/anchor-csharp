using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeetaNet.Anchor;

/// <summary>
/// The camelCase transport serialization every SDK surface shares with the
/// wasm core: null members are omitted and enums cross in their lowercase form.
/// </summary>
internal static class KeetaJson
{
	public static readonly JsonSerializerOptions Options = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
	};

	/// <summary>Deserialize a JSON array payload, mapping an absent body to an empty list.</summary>
	public static IReadOnlyList<T> ReadList<T>(byte[] payload) =>
		JsonSerializer.Deserialize<List<T>>(payload, Options) ?? new List<T>();
}
