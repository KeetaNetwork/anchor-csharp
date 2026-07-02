using KeetaNet.Anchor.Crypto;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KeetaNet.Anchor.Tests;

/// <summary>
/// The container integration: one shared runtime, idempotent registration,
/// container-owned disposal.
/// </summary>
public sealed class DependencyInjectionTests
{
	[Fact]
	public void RegistersOneSharedRuntimeTheContainerDisposes()
	{
		var services = new ServiceCollection();
		services.AddKeetaNetAnchor();
		services.AddKeetaNetAnchor();

		WasmRuntime runtime;
		using (ServiceProvider provider = services.BuildServiceProvider())
		{
			runtime = provider.GetRequiredService<WasmRuntime>();
			WasmRuntime resolvedAgain = provider.GetRequiredService<WasmRuntime>();
			Assert.Same(runtime, resolvedAgain);

			using Account account = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, "ed25519");
			Assert.StartsWith("keeta_", account.Address, StringComparison.Ordinal);
		}

		Assert.True(runtime.IsDisposed);
	}
}
