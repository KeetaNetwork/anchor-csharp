namespace KeetaNet.Anchor;

/// <summary>
/// A failure surfaced from the wasm core: a programmatic <see cref="Code"/> plus
/// a human-readable message.
/// </summary>
public sealed class KeetaException : Exception
{
	/// <summary>The stable, machine-readable error code.</summary>
	public string Code { get; }

	/// <summary>Build a failure from its stable <paramref name="code"/> and human-readable <paramref name="message"/>.</summary>
	public KeetaException(string code, string message) : base($"{code}: {message}")
	{
		Code = code;
	}

	/// <summary>
	/// Parse a wasm <c>code: message</c> error string back into an exception.
	/// </summary>
	internal static KeetaException Parse(string encoded)
	{
		int separator = encoded.IndexOf(": ", StringComparison.Ordinal);
		if (separator < 0)
		{
			return new KeetaException("UNKNOWN", encoded);
		}

		string code = encoded[..separator];
		string message = encoded[(separator + 2)..];
		return new KeetaException(code, message);
	}
}
