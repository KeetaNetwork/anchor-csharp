using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// A decoded KYC attribute value: the undecoded semantic bytes plus typed
/// accessors. The attribute schema lives in the reference implementation, so
/// each accessor parses on demand and throws <see cref="KeetaException"/>
/// (code <c>ATTRIBUTE_DECODE</c>) when the bytes are not that shape.
/// </summary>
public sealed class KycAttributeValue
{
	private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	private readonly string _name;

	internal KycAttributeValue(string name, byte[] buffer)
	{
		_name = name;
		Buffer = buffer;
	}

	/// <summary>The undecoded semantic bytes.</summary>
	public byte[] Buffer { get; }

	/// <summary>The value as UTF-8 text (scalar attributes).</summary>
	public string AsText()
	{
		try
		{
			return StrictUtf8.GetString(Buffer);
		}
		catch (DecoderFallbackException error)
		{
			throw new KeetaException("ATTRIBUTE_DECODE", $"attribute `{_name}` is not valid UTF-8 text: {error.Message}");
		}
	}

	/// <summary>The value as an RFC-3339 timestamp (date attributes).</summary>
	public DateTimeOffset AsTimestamp()
	{
		string text = AsText();
		if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset timestamp))
		{
			throw new KeetaException("ATTRIBUTE_DECODE", $"attribute `{_name}` is not an RFC-3339 timestamp");
		}

		return timestamp;
	}

	/// <summary>The value as JSON (structured attributes).</summary>
	public JsonElement AsJson()
	{
		try
		{
			using var document = JsonDocument.Parse(Buffer);
			return document.RootElement.Clone();
		}
		catch (JsonException error)
		{
			throw new KeetaException("ATTRIBUTE_DECODE", $"attribute `{_name}` is not valid JSON: {error.Message}");
		}
	}
}
