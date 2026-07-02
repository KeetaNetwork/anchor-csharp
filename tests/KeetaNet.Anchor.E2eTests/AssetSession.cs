using KeetaNet.Anchor.Crypto;
using Xunit;

namespace KeetaNet.Anchor.E2eTests;

/// <summary>
/// One asset-movement test's live setup: a running anchor plus a client signed
/// by the shared caller, torn down together in reverse creation order.
/// </summary>
internal sealed class AssetSession : IDisposable
{
	private readonly NodeHarness _harness;
	private readonly WasmRuntime _runtime;
	private readonly Account _signer;

	public AssetAnchor Anchor { get; }
	public AssetMovementClient Client { get; }
	public CancellationToken CancellationToken { get; }

	private AssetSession(
		NodeHarness harness,
		AssetAnchor anchor,
		WasmRuntime runtime,
		Account signer,
		AssetMovementClient client)
	{
		_harness = harness;
		Anchor = anchor;
		_runtime = runtime;
		_signer = signer;
		Client = client;
		CancellationToken = TestContext.Current.CancellationToken;
	}

	/// <summary>Boot the anchor and connect a signed client to it.</summary>
	public static AssetSession Open()
	{
		NodeHarness harness = NodeHarness.Spawn("asset");
		try
		{
			AssetAnchor anchor = AssetAnchor.Start(harness);
			WasmRuntime runtime = WasmRuntime.Load();
			Account signer = runtime.Accounts.FromSeed(E2eSeeds.Caller, 0, E2eSeeds.Secp256k1);
			AssetMovementClient client = runtime.CreateAssetMovementClient(anchor.Api, anchor.Root, signer);

			return new AssetSession(harness, anchor, runtime, signer, client);
		}
		catch
		{
			harness.Dispose();
			throw;
		}
	}

	public void Deconstruct(
		out AssetMovementClient client,
		out AssetAnchor anchor,
		out CancellationToken cancellationToken)
	{
		client = Client;
		anchor = Anchor;
		cancellationToken = CancellationToken;
	}

	/// <summary>The single provider the running anchor publishes.</summary>
	public async Task<AssetProvider> DiscoveredProviderAsync()
	{
		AssetProvider? provider = await Client.GetProviderByIdAsync(Anchor.ProviderId, CancellationToken);
		Assert.NotNull(provider);

		return provider!;
	}

	/// <summary>Stop the harness cleanly once the test's assertions are done.</summary>
	public void Shutdown() => _harness.Shutdown();

	public void Dispose()
	{
		Client.Dispose();
		_signer.Dispose();
		_runtime.Dispose();
		_harness.Dispose();
	}
}
