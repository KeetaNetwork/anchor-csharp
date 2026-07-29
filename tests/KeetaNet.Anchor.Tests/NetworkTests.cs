using Xunit;

namespace KeetaNet.Anchor.Tests;

/// <summary>
/// The well-known network registry: ids, aliases, and representative
/// endpoints must match the reference registry verbatim, and the
/// <c>fromNetwork</c>-style factories must bind them.
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
