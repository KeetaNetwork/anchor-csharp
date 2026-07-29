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
	/// embedded wasm core on first resolve, along with its factory surfaces so
	/// consumers can inject a factory directly instead of the runtime. The
	/// container disposes the runtime on shutdown. The runtime is thread-safe.
	/// </summary>
	/// <returns>The same collection, for chaining.</returns>
	public static IServiceCollection AddKeetaNetAnchor(this IServiceCollection services)
	{
		services.TryAddSingleton(static _ => WasmRuntime.Load());
		services.TryAddSingleton(static provider => provider.GetRequiredService<WasmRuntime>().Accounts);
		services.TryAddSingleton(static provider => provider.GetRequiredService<WasmRuntime>().Certificates);
		services.TryAddSingleton(static provider => provider.GetRequiredService<WasmRuntime>().KycCertificates);
		services.TryAddSingleton(static provider => provider.GetRequiredService<WasmRuntime>().Containers);
		services.TryAddSingleton(static provider => provider.GetRequiredService<WasmRuntime>().Sharables);

		return services;
	}

	/// <summary>
	/// Register a <see cref="KeetaClient"/> for the node API at
	/// <paramref name="nodeUrl"/> as a typed HTTP client, so its
	/// <see cref="System.Net.Http.HttpClient"/> comes from
	/// <c>IHttpClientFactory</c> (pooled handlers, policy-friendly).
	/// </summary>
	/// <returns>The same collection, for chaining.</returns>
	public static IServiceCollection AddKeetaNetAnchorKeetaClient(this IServiceCollection services, string nodeUrl)
	{
		services.AddKeetaNetAnchor();
		services.AddHttpClient(nameof(KeetaClient))
			.AddTypedClient((http, provider) =>
				provider.GetRequiredService<WasmRuntime>().CreateKeetaClient(nodeUrl, http));
		return services;
	}
}
