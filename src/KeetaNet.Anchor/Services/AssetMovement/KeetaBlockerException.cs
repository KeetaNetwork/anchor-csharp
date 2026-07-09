using System.Text.Json;

namespace KeetaNet.Anchor;

/// <summary>
/// An anchor refusal carrying a typed asset-movement blocker: the operation
/// cannot proceed until the user resolves <see cref="Blocker"/>.
/// </summary>
public sealed class KeetaBlockerException : KeetaException
{
	/// <summary>Every recognized blocker transport code starts with this.</summary>
	internal const string CodePrefix = "KEETA_ANCHOR_ASSET_MOVEMENT_";

	/// <summary>The typed blocker the user must resolve.</summary>
	public AssetMovementBlocker Blocker { get; }

	private KeetaBlockerException(string code, string message, AssetMovementBlocker blocker)
		: base(code, message)
	{
		Blocker = blocker;
	}

	/// <summary>
	/// Rehydrate a blocker failure from the core's last-error parts, or null
	/// when <paramref name="code"/> is not a blocker transport code or
	/// <paramref name="payload"/> does not decode as a blocker.
	/// </summary>
	internal static KeetaBlockerException? TryDecode(string code, string payload)
	{
		if (!code.StartsWith(CodePrefix, StringComparison.Ordinal))
		{
			return null;
		}

		AssetMovementBlocker? blocker;
		try
		{
			blocker = JsonSerializer.Deserialize<AssetMovementBlocker>(payload, KeetaJson.Options);
		}
		catch (JsonException)
		{
			return null;
		}

		if (blocker is null)
		{
			return null;
		}

		return new KeetaBlockerException(code, Describe(blocker), blocker);
	}

	/// <summary>A human-readable summary of what the user must resolve.</summary>
	private static string Describe(AssetMovementBlocker blocker) =>
		blocker switch
		{
			AssetKycShareNeededBlocker => "the provider requires KYC attributes to be shared first",
			AssetAdditionalKycNeededBlocker => "the provider requires additional KYC steps",
			AssetOperationNotSupportedBlocker => "the provider does not support this operation",
			AssetUserActionNeededBlocker => "the provider requires on-ledger user actions",
			_ => "the provider reported a blocker",
		};
}
