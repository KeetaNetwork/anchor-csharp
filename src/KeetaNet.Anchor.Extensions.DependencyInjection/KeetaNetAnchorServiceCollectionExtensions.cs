using KeetaNet.Anchor;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the KeetaNet anchor SDK with an <see cref="IServiceCollection"/>.
/// </summary>
public static class KeetaNetAnchorServiceCollectionExtensions
{
	/// <summary>
	/// Register the shared <see cref="WasmRuntime"/> as a singleton, loading the
	/// embedded wasm core on first resolve. The container disposes it on
	/// shutdown. The runtime is thread-safe.
	/// </summary>
	/// <returns>The same collection, for chaining.</returns>
	public static IServiceCollection AddKeetaNetAnchor(this IServiceCollection services)
	{
		services.TryAddSingleton(static _ => WasmRuntime.Load());
		return services;
	}

	/// <summary>
	/// Register a <see cref="NodeClient"/> for the node API at
	/// <paramref name="nodeUrl"/> as a typed HTTP client, so its
	/// <see cref="System.Net.Http.HttpClient"/> comes from
	/// <c>IHttpClientFactory</c> (pooled handlers, policy-friendly).
	/// </summary>
	/// <returns>The same collection, for chaining.</returns>
	public static IServiceCollection AddKeetaNetAnchorNodeClient(this IServiceCollection services, string nodeUrl)
	{
		services.AddKeetaNetAnchor();
		services.AddHttpClient(nameof(NodeClient))
			.AddTypedClient((http, provider) =>
				provider.GetRequiredService<WasmRuntime>().CreateNodeClient(nodeUrl, http));
		return services;
	}
}
