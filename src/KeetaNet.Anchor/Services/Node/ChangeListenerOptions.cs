namespace KeetaNet.Anchor;

/// <summary>
/// Tuning for <see cref="UserClient.OnChange"/>.
/// </summary>
/// <remarks>
/// The WebSocket path reacts to a change immediately. The fallback poll
/// finds the updates that the socket missed.
/// </remarks>
public sealed class ChangeListenerOptions
{
	/// <summary>
	/// How often the fallback poll re-reads the account. The default is one minute.
	/// </summary>
	public TimeSpan FallbackFrequency { get; set; } = TimeSpan.FromMinutes(1);
}
