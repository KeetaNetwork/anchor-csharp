namespace KeetaNet.Anchor;

/// <summary>
/// A well-known KeetaNet network from the network registry.
/// </summary>
/// <remarks>
/// The <c>FromNetwork</c>-style client factories read the network id and the
/// default representative set from this enum.
/// </remarks>
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

/// <summary>
/// One entry of the representative registry.
/// </summary>
/// <remarks>
/// The entry carries the representative's account address and its advertised
/// API and P2P endpoints. The address is null when the network derives it at
/// runtime, as <c>dev</c> does.
/// </remarks>
public sealed record RepresentativeEndpoint(string? Key, string ApiUrl, string? P2pUrl);

/// <summary>The registry values for each <see cref="KeetaNetwork"/>.</summary>
public static class KeetaNetworkExtensions
{
	/// <summary>The registered representative addresses of the production network.</summary>
	private static readonly string[] MainRepresentativeKeys =
	{
		"keeta_aabwip6zeo2fnzfxp5hssrrqtascs2277w2zk7vqd6d3k3m4dkt2flcbca2mqki",
		"keeta_aabvmwxttv4q56gbfveighwfwp3yvitlrdfsacic3ckqc7lqelsspvmhc7oldmq",
		"keeta_aabwqf5fnta4t2v2atieis545b3rqoq6z7x5w3geugiilqlz5jdsb5og2rmxvdq",
		"keeta_aablpogflko72eusdhuuqgsto2rwcvy2m5mo5snmvrmbacz3qczwjtwpmzf5ufq",
	};

	private static readonly string[] StagingRepresentativeKeys =
	{
		"keeta_aabaagdrwrwnkzox4u3qh6uukre6lckax6kb5fwyxd4vtpua6vrjc6nuhb75fji",
		"keeta_aabgizanf4agmioyrswbg4wsl7nmjlrakwd4piuks7cqagfccnxc2fscm25hw7i",
		"keeta_aab2gw2zmtazqgtromyfmhjn5h67ep23676zq62obgtqaw65x5l5krn252w57ma",
		"keeta_aabue4mdj22i5o6774tlszcxy2sxyvpninbm54nfhxn6dkmsvtryd7oha4bzh2i",
	};

	private static readonly string[] TestRepresentativeKeys =
	{
		"keeta_aabi4bd3f7jrt67mxcq44ozj65bh4bp2mygmrkedxggu2rxwn2ztuw3b6exivbq",
		"keeta_aabf7dz5asq2n2lrldct33x2ww65cophxp7egfiixbb7tbyat5r3kcbcez7ftpi",
		"keeta_aab3cxegizwhtim3zlyuwjhiqd5ikkhxg42smhwc3wx6yn7ep2t6lwo6emvw4wa",
		"keeta_aabznoicrzvte6ql5rxbgugmfrjqubbnjuo5l6ivopowy4rpkqgs5fco3oaezcq",
	};

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
	/// Returns the API endpoint of representative
	/// <paramref name="representative"/>, numbered from one.
	/// </summary>
	/// <remarks>
	/// Production network URLs carry a <c>network</c> infix. The <c>dev</c>
	/// URLs do not.
	/// </remarks>
	public static string RepresentativeApiUrl(this KeetaNetwork network, int representative = 1) =>
		network.RepresentativeUrl("https", "api", representative);

	/// <summary>
	/// Returns the P2P (WebSocket) endpoint of representative
	/// <paramref name="representative"/>, numbered from one.
	/// </summary>
	public static string RepresentativeP2pUrl(this KeetaNetwork network, int representative = 1) =>
		network.RepresentativeUrl("wss", "p2p", representative);

	/// <summary>
	/// Returns the default representative set for this network.
	/// </summary>
	/// <remarks>
	/// Each network registers four representatives, each with its account
	/// address and advertised endpoints. The <c>dev</c> network derives its
	/// representative accounts from a deterministic seed at runtime, so its
	/// entries carry no key.
	/// </remarks>
	public static IReadOnlyList<RepresentativeEndpoint> Representatives(this KeetaNetwork network)
	{
		string[]? keys = network switch
		{
			KeetaNetwork.Main => MainRepresentativeKeys,
			KeetaNetwork.Staging => StagingRepresentativeKeys,
			KeetaNetwork.Test => TestRepresentativeKeys,
			_ => null,
		};

		var endpoints = new RepresentativeEndpoint[4];
		for (int index = 0; index < endpoints.Length; index++)
		{
			int representative = index + 1;
			endpoints[index] = new RepresentativeEndpoint(
				keys?[index],
				network.RepresentativeApiUrl(representative),
				network.RepresentativeP2pUrl(representative));
		}

		return endpoints;
	}

	/// <summary>Builds one representative endpoint URL. The API and P2P forms share this path.</summary>
	private static string RepresentativeUrl(this KeetaNetwork network, string scheme, string path, int representative)
	{
		string alias = network.Alias();
		if (network == KeetaNetwork.Dev)
		{
			return $"{scheme}://rep{representative}.{alias}.api.keeta.com/{path}";
		}

		return $"{scheme}://rep{representative}.{alias}.network.api.keeta.com/{path}";
	}
}
