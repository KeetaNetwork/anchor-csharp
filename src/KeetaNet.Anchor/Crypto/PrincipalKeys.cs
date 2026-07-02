using System.Text.Json;

namespace KeetaNet.Anchor.Crypto;

/// <summary>Decode the core's principal list payload into type-prefixed public keys.</summary>
internal static class PrincipalKeys
{
	/// <summary>The payload crosses as arrays of byte values, one per principal.</summary>
	public static IReadOnlyList<byte[]> Decode(byte[] payload)
	{
		int[][] raw = JsonSerializer.Deserialize<int[][]>(payload) ?? Array.Empty<int[]>();
		return raw.Select(values => Array.ConvertAll(values, value => (byte)value)).ToList();
	}
}
