using System.Text.Json;
using System.Text.Json.Nodes;

namespace KeetaNet.Anchor.E2eTests;

/// <summary>
/// A live KYC anchor HTTP server started by the harness, with its service
/// metadata published on-chain to <see cref="Root"/> and readable through the
/// reference node's API at <see cref="NodeApi"/>.
/// </summary>
internal sealed record KycAnchor(string NodeApi, string Root, string Ca, string ProviderId)
{
	/// <summary>Start a signed KYC anchor advertising the US country code.</summary>
	public static KycAnchor Start(NodeHarness harness)
	{
		var arguments = new JsonObject
		{
			["sign"] = true,
			["countryCodes"] = new JsonArray("US"),
		};
		JsonElement started = harness.Request("startKycAnchor", arguments);

		return new KycAnchor(
			started.GetProperty("api").GetString()!,
			started.GetProperty("root").GetString()!,
			started.GetProperty("ca").GetString()!,
			started.GetProperty("providerId").GetString()!);
	}
}

/// <summary>
/// A certificate chain the harness published on-chain for a fresh holder
/// <see cref="Account"/>: <see cref="Leaf"/> (addressable by <see cref="LeafHash"/>)
/// recorded with <see cref="Ca"/> as its intermediate bundle, and
/// <see cref="Bare"/> recorded without intermediates, so a ledger read serves
/// both shapes.
/// </summary>
internal sealed record PublishedChain(string Account, string Leaf, string LeafHash, string Bare, string Ca)
{
	/// <summary>Publish the two-record chain on the running anchor's node.</summary>
	public static PublishedChain Publish(NodeHarness harness)
	{
		JsonElement published = harness.Request("publishCertificateChain", new JsonObject());

		return new PublishedChain(
			published.GetProperty("account").GetString()!,
			published.GetProperty("leaf").GetString()!,
			published.GetProperty("leafHash").GetString()!,
			published.GetProperty("bare").GetString()!,
			published.GetProperty("ca").GetString()!);
	}
}

/// <summary>
/// A live reference node started by the node harness, with helpers that mutate
/// its ledger (funding, account info, delegation) so the node client can read
/// every surface back over the real API.
/// </summary>
internal sealed class LedgerNode
{
	private readonly NodeHarness _harness;

	public string Api { get; }
	public string BaseToken { get; }
	public string Representative { get; }
	public long Network { get; }

	private LedgerNode(NodeHarness harness, string api, string baseToken, string representative, long network)
	{
		_harness = harness;
		Api = api;
		BaseToken = baseToken;
		Representative = representative;
		Network = network;
	}

	/// <summary>Boot the reference node with an initialized chain.</summary>
	public static LedgerNode Start(NodeHarness harness)
	{
		JsonElement started = harness.Request("startNode");

		return new LedgerNode(
			harness,
			started.GetProperty("api").GetString()!,
			started.GetProperty("baseToken").GetString()!,
			started.GetProperty("representative").GetString()!,
			long.Parse(started.GetProperty("network").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
	}

	/// <summary>Fund the seed-derived account, returning its address.</summary>
	public string Fund(string seed, long amount)
	{
		var arguments = new JsonObject
		{
			["seed"] = seed,
			["algorithm"] = "secp256k1",
			["amount"] = amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
		};

		return _harness.Request("fund", arguments).GetProperty("account").GetString()!;
	}

	/// <summary>Publish on-chain info for the seed-derived account.</summary>
	public void SetInfo(string seed, string name, string description, string metadata)
	{
		var arguments = new JsonObject
		{
			["seed"] = seed,
			["algorithm"] = "secp256k1",
			["name"] = name,
			["description"] = description,
			["metadata"] = metadata,
		};

		_harness.Request("setInfo", arguments);
	}

	/// <summary>
	/// Delegate the seed-derived account's weight to the account derived from
	/// <paramref name="representativeSeed"/>, returning the representative address.
	/// </summary>
	public string SetRep(string seed, string representativeSeed)
	{
		var arguments = new JsonObject
		{
			["seed"] = seed,
			["algorithm"] = "secp256k1",
			["representativeSeed"] = representativeSeed,
			["representativeAlgorithm"] = "secp256k1",
		};

		return _harness.Request("setRep", arguments).GetProperty("representative").GetString()!;
	}

	/// <summary>The account's head block hash as the reference client reports it, or null.</summary>
	public string? Head(string account)
	{
		var arguments = new JsonObject { ["account"] = account };
		return _harness.Request("head", arguments).GetProperty("head").GetString();
	}

	/// <summary>
	/// Have the reference issue a CA and a leaf for the seed-derived holder.
	/// The reference builder emits the CA extensions the node's certificate
	/// graph check demands, which the core's KYC builder omits.
	/// </summary>
	public IssuedChain IssueChain(string seed)
	{
		var arguments = new JsonObject
		{
			["seed"] = seed,
			["algorithm"] = "secp256k1",
		};
		JsonElement issued = _harness.Request("issueChain", arguments);

		return new IssuedChain(
			issued.GetProperty("ca").GetString()!,
			issued.GetProperty("leaf").GetString()!,
			issued.GetProperty("leafHash").GetString()!);
	}
}

/// <summary>A reference-issued CA and holder leaf, as PEM strings.</summary>
internal sealed record IssuedChain(string Ca, string Leaf, string LeafHash);

/// <summary>
/// A live asset-movement anchor HTTP server started by the harness, alongside
/// the fixture values its callbacks report back.
/// </summary>
internal sealed record AssetAnchor(
	string NodeApi,
	string Root,
	string ProviderId,
	string Signer,
	string Asset,
	string SendToAddress)
{
	/// <summary>
	/// Start a signed asset-movement anchor. When <paramref name="blockedAccount"/>
	/// is given, the anchor reports that account as blocked from
	/// <c>getAccountStatus</c> with one blocker of every recoverable kind.
	/// </summary>
	public static AssetAnchor Start(NodeHarness harness, string? blockedAccount = null)
	{
		var arguments = new JsonObject { ["sign"] = true };
		if (blockedAccount is not null)
		{
			arguments["blockedAccount"] = blockedAccount;
		}

		JsonElement started = harness.Request("startAssetAnchor", arguments);

		return new AssetAnchor(
			started.GetProperty("api").GetString()!,
			started.GetProperty("root").GetString()!,
			started.GetProperty("providerId").GetString()!,
			started.GetProperty("signer").GetString()!,
			started.GetProperty("asset").GetString()!,
			started.GetProperty("sendToAddress").GetString()!);
	}
}
