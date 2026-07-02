using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace KeetaNet.Anchor.E2eTests;

/// <summary>
/// One issue attribute projected for both directions of the KYC round-trip:
/// its <see cref="Name"/>, the <see cref="Semantic"/> bytes the core codec
/// encodes, whether it is <see cref="Sensitive"/>, and the <see cref="Expected"/>
/// value a reader recovers.
/// </summary>
internal sealed record AttributeCase(string Name, byte[] Semantic, bool Sensitive, JsonNode Expected);

/// <summary>
/// The attributes exercised by the cross-implementation round-trip, spanning a
/// plain string scalar, a date, and structured types whose CHOICE fields carry
/// the positional wrapper. Matches the anchor-rs interop fixture.
/// </summary>
internal static class IssueAttributes
{
	private const string Fixture = """
	[
		{ "name": "fullName", "sensitive": true, "value": "Test User" },
		{ "name": "email", "sensitive": true, "value": "user@example.com" },
		{ "name": "dateOfBirth", "sensitive": true, "value": { "__date": "1980-01-01T00:00:00.000Z" } },
		{ "name": "address", "sensitive": true, "value": {
			"addressLines": ["100 Belgrave Street"],
			"addressType": "HOME",
			"streetName": "100 Belgrave Street",
			"townName": "Oldsmar",
			"countrySubDivision": "FL",
			"postalCode": "34677"
		} },
		{ "name": "entityType", "sensitive": true, "value": {
			"person": [{ "id": "123-45-6789", "schemeName": "SSN" }]
		} },
		{ "name": "documentPassport", "sensitive": true, "value": {
			"documentNumber": "X1234567",
			"fullName": "Jane Doe",
			"issuingCountry": "US",
			"nationality": "US",
			"address": { "country": "US", "postalCode": "34677", "townName": "Oldsmar" },
			"front": {
				"external": { "url": "https://example.test/doc", "contentType": "image/png" },
				"digest": {
					"digestAlgorithm": "sha3-256",
					"digest": { "type": "Buffer", "data": [1, 2, 3] }
				},
				"encryptionAlgorithm": "1.3.6.1.4.1.62675.2"
			}
		} }
	]
	""";

	/// <summary>The fixture in the shape the harness `issueCertificate` command takes.</summary>
	public static JsonArray ForHarness() => (JsonArray)JsonNode.Parse(Fixture)!;

	/// <summary>The fixture entries for <paramref name="names"/>, harness-shaped.</summary>
	public static JsonArray ForHarness(IReadOnlyCollection<string> names)
	{
		var subset = new JsonArray();
		foreach (JsonNode? entry in ForHarness())
		{
			string name = (string)entry!["name"]!;
			if (names.Contains(name))
			{
				subset.Add(entry.DeepClone());
			}
		}

		return subset;
	}

	/// <summary>The fixture projected into per-attribute encode/compare cases.</summary>
	public static IReadOnlyList<AttributeCase> Cases()
	{
		var cases = new List<AttributeCase>();
		foreach (JsonNode? node in ForHarness())
		{
			JsonObject entry = node!.AsObject();
			string name = (string)entry["name"]!;
			bool sensitive = (bool)entry["sensitive"]!;
			(byte[] semantic, JsonNode expected) = SemanticAndExpected(entry["value"]!);

			cases.Add(new AttributeCase(name, semantic, sensitive, expected));
		}

		return cases;
	}

	/// <summary>The cases for <paramref name="names"/> only.</summary>
	public static IReadOnlyList<AttributeCase> Cases(IReadOnlyCollection<string> names) =>
		Cases().Where(attribute => names.Contains(attribute.Name)).ToList();

	/// <summary>Attribute names in the JSON-array shape harness commands take.</summary>
	public static JsonArray NameArray(IEnumerable<string> names)
	{
		var array = new JsonArray();
		foreach (string name in names)
		{
			array.Add(name);
		}

		return array;
	}

	/// <summary>
	/// Assert the decoded semantic <paramref name="bytes"/> match the case's
	/// expected value: a scalar as its UTF-8 text, a structured attribute as
	/// structurally equal JSON.
	/// </summary>
	public static void AssertMatches(AttributeCase expected, byte[] bytes)
	{
		string text = Encoding.UTF8.GetString(bytes);
		if (expected.Expected is JsonValue)
		{
			Assert.Equal((string)expected.Expected!, text);
			return;
		}

		AssertJsonEqual(expected.Name, expected.Expected, JsonNode.Parse(text));
	}

	/// <summary>Assert structural JSON equality with a readable divergence message.</summary>
	public static void AssertJsonEqual(string name, JsonNode? expected, JsonNode? actual)
	{
		Assert.True(
			JsonNode.DeepEquals(expected, actual),
			$"attribute `{name}` diverges: expected {expected?.ToJsonString()}, got {actual?.ToJsonString()}");
	}

	/// <summary>
	/// Project a fixture value into the bytes the core codec encodes and the
	/// value a reader emits: a string passes through, a <c>__date</c> wrapper
	/// becomes its ISO string, and a structured object crosses as JSON text.
	/// </summary>
	private static (byte[] Semantic, JsonNode Expected) SemanticAndExpected(JsonNode value)
	{
		if (value is JsonValue text)
		{
			return (Encoding.UTF8.GetBytes((string)text!), value.DeepClone());
		}

		if (value is JsonObject wrapper && wrapper.TryGetPropertyValue("__date", out JsonNode? iso))
		{
			return (Encoding.UTF8.GetBytes((string)iso!), iso!.DeepClone());
		}

		return (Encoding.UTF8.GetBytes(value.ToJsonString()), value.DeepClone());
	}
}
