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
}
