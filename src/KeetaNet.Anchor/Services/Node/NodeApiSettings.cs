using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeetaNet.Anchor.Generated.Node;

/// <summary>
/// Serializer settings for the generated node transport. Null optional members
/// must vanish from request bodies: the vote endpoint reads an explicit
/// <c>votes: null</c> differently from an absent field, exactly as the
/// reference clients (which serialize with null suppression) rely on.
/// </summary>
public partial class NodeApi
{
	static partial void UpdateJsonSerializerSettings(JsonSerializerOptions settings) =>
		settings.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
}
