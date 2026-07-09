namespace KeetaNet.Anchor;

/// <summary>
/// A failure surfaced from the SDK (the wasm core or the node transport): a
/// programmatic <see cref="Code"/> plus a human-readable message. Failures
/// that carry a typed payload derive from it, such as
/// <see cref="KeetaBlockerException"/>.
/// </summary>
public class KeetaException : Exception
{
	/// <summary>The stable, machine-readable error code.</summary>
	public string Code { get; }

	/// <summary>Build a failure from its stable <paramref name="code"/> and human-readable <paramref name="message"/>.</summary>
	public KeetaException(string code, string message) : base($"{code}: {message}")
	{
		Code = code;
	}

	/// <summary>
	/// Build a failure from its stable <paramref name="code"/> and human-readable
	/// <paramref name="message"/>, preserving the <paramref name="cause"/> it wraps.
	/// </summary>
	public KeetaException(string code, string message, Exception cause) : base($"{code}: {message}", cause)
	{
		Code = code;
	}
}
