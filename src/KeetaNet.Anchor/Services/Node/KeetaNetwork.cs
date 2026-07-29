namespace KeetaNet.Anchor;

/// <summary>
/// A well-known KeetaNet network, the port of the reference network registry.
/// Feeds the <c>FromNetwork</c>-style client factories with the network id and
/// its first representative's API endpoint.
/// </summary>
public enum KeetaNetwork
{
	/// <summary>The production network.</summary>
	Main,
	/// <summary>The staging network.</summary>
	Staging,
	/// <summary>The public test network.</summary>
	Test,
	/// <summary>The development network (deterministic, seed-derived accounts).</summary>
	Dev,
}

/// <summary>The reference registry values for each <see cref="KeetaNetwork"/>.</summary>
public static class KeetaNetworkExtensions
{
	/// <summary>The network identifier stamped onto blocks for this network.</summary>
	public static long Id(this KeetaNetwork network) =>
		network switch
		{
			KeetaNetwork.Main => 0x5382,
			KeetaNetwork.Staging => 0x0053_8201,
			KeetaNetwork.Test => 0x5445_5354,
			_ => 0x0044_4556,
		};

	/// <summary>The lowercase alias used in URLs and string parsing.</summary>
	public static string Alias(this KeetaNetwork network) =>
		network switch
		{
			KeetaNetwork.Main => "main",
			KeetaNetwork.Staging => "staging",
			KeetaNetwork.Test => "test",
			_ => "dev",
		};

	/// <summary>
	/// The API endpoint of representative <paramref name="representative"/>
	/// (numbered from one). Production networks carry a <c>network</c> infix;
	/// <c>dev</c> does not.
	/// </summary>
	public static string RepresentativeApiUrl(this KeetaNetwork network, int representative = 1)
	{
		string alias = network.Alias();
		if (network == KeetaNetwork.Dev)
		{
			return $"https://rep{representative}.{alias}.api.keeta.com/api";
		}

		return $"https://rep{representative}.{alias}.network.api.keeta.com/api";
	}
}
