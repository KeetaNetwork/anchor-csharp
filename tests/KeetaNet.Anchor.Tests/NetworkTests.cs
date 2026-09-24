using Xunit;

namespace KeetaNet.Anchor.Tests;

/// <summary>
/// The well-known network registry. The ids, the aliases, and the
/// representative endpoints must match the reference registry exactly, and
/// the <c>fromNetwork</c>-style factories must bind them.
/// </summary>
public sealed class NetworkTests
{
	[Theory]
	[InlineData(KeetaNetwork.Main, 0x5382, "main", "https://rep1.main.network.api.keeta.com/api")]
	[InlineData(KeetaNetwork.Staging, 0x0053_8201, "staging", "https://rep1.staging.network.api.keeta.com/api")]
	[InlineData(KeetaNetwork.Test, 0x5445_5354, "test", "https://rep1.test.network.api.keeta.com/api")]
	[InlineData(KeetaNetwork.Dev, 0x0044_4556, "dev", "https://rep1.dev.api.keeta.com/api")]
	public void TheRegistryMatchesTheReferenceValues(KeetaNetwork network, long id, string alias, string apiUrl)
	{
		Assert.Equal(id, network.Id());
		Assert.Equal(alias, network.Alias());
		Assert.Equal(apiUrl, network.RepresentativeApiUrl());
	}

	[Theory]
	[InlineData(KeetaNetwork.Main, "keeta_aabwip6zeo2fnzfxp5hssrrqtascs2277w2zk7vqd6d3k3m4dkt2flcbca2mqki")]
	[InlineData(KeetaNetwork.Staging, "keeta_aabaagdrwrwnkzox4u3qh6uukre6lckax6kb5fwyxd4vtpua6vrjc6nuhb75fji")]
	[InlineData(KeetaNetwork.Test, "keeta_aabi4bd3f7jrt67mxcq44ozj65bh4bp2mygmrkedxggu2rxwn2ztuw3b6exivbq")]
	public void TheRegistryCarriesFourKeyedRepresentativesPerNetwork(KeetaNetwork network, string firstKey)
	{
		IReadOnlyList<RepresentativeEndpoint> representatives = network.Representatives();

		Assert.Equal(4, representatives.Count);
		Assert.Equal(firstKey, representatives[0].Key);
		Assert.All(representatives, entry => Assert.NotNull(entry.Key));
		Assert.Equal(network.RepresentativeApiUrl(2), representatives[1].ApiUrl);
		Assert.Equal(network.RepresentativeP2pUrl(3), representatives[2].P2pUrl);
		Assert.StartsWith("wss://", representatives[0].P2pUrl!, StringComparison.Ordinal);
	}

	[Fact]
	public void TheDevRegistryDerivesItsRepresentativesAtRuntime()
	{
		IReadOnlyList<RepresentativeEndpoint> representatives = KeetaNetwork.Dev.Representatives();

		Assert.Equal(4, representatives.Count);
		Assert.All(representatives, entry => Assert.Null(entry.Key));
		Assert.Equal("https://rep1.dev.api.keeta.com/api", representatives[0].ApiUrl);
	}

	[Fact]
	public void TheNetworkFactoriesBindTheNetworkAndDeriveItsBaseToken()
	{
		using var runtime = WasmRuntime.Load();

		using KeetaClient client = runtime.CreateKeetaClient(KeetaNetwork.Test);
		Assert.Equal(KeetaNetwork.Test.Id(), client.Network);
		Assert.NotNull(client.BaseToken);

		// The bound network's base token is the deterministic derivation.
		using Crypto.Account derived = runtime.Blocks.NetworkBaseToken(KeetaNetwork.Test.Id());
		Assert.Equal(derived.PublicKeyString, client.BaseToken!.PublicKeyString);

		using UserClient user = runtime.CreateUserClient(KeetaNetwork.Dev, signer: null);
		Assert.True(user.IsReadOnly);
		Assert.Equal(KeetaNetwork.Dev.Id(), user.Client.Network);
	}
}
