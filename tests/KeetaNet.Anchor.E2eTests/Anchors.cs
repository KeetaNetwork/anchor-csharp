using System.Text.Json;
using System.Text.Json.Nodes;

namespace KeetaNet.Anchor.E2eTests;

/// <summary>
/// A live KYC anchor HTTP server started by the harness, with its service
/// metadata published on-chain to <see cref="Root"/> and readable through the
/// node API at <see cref="Api"/>.
/// </summary>
internal sealed record KycAnchor(string Api, string Root, string Ca, string ProviderId)
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
/// <see cref="Account"/>: <see cref="Leaf"/> recorded with <see cref="Ca"/> as
/// its intermediate bundle, and <see cref="Bare"/> recorded without
/// intermediates, so a ledger read serves both shapes.
/// </summary>
internal sealed record PublishedChain(string Account, string Leaf, string Bare, string Ca)
{
	/// <summary>Publish the two-record chain on the running anchor's node.</summary>
	public static PublishedChain Publish(NodeHarness harness)
	{
		JsonElement published = harness.Request("publishCertificateChain", new JsonObject());

		return new PublishedChain(
			published.GetProperty("account").GetString()!,
			published.GetProperty("leaf").GetString()!,
			published.GetProperty("bare").GetString()!,
			published.GetProperty("ca").GetString()!);
	}
}

/// <summary>
/// A live asset-movement anchor HTTP server started by the harness, alongside
/// the fixture values its callbacks report back.
/// </summary>
internal sealed record AssetAnchor(
	string Api,
	string Root,
	string ProviderId,
	string Signer,
	string Asset,
	string SendToAddress)
{
	/// <summary>Start a signed asset-movement anchor.</summary>
	public static AssetAnchor Start(NodeHarness harness)
	{
		var arguments = new JsonObject { ["sign"] = true };
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
