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
			Assert.StartsWith("keeta_", account.PublicKeyString, StringComparison.Ordinal);
		}

		Assert.True(runtime.IsDisposed);
	}

	[Fact]
	public void RegistersTheRuntimeFactorySurfacesForDirectInjection()
	{
		var services = new ServiceCollection();
		services.AddKeetaNetAnchor();

		using ServiceProvider provider = services.BuildServiceProvider();
		WasmRuntime runtime = provider.GetRequiredService<WasmRuntime>();

		Assert.Same(runtime.Accounts, provider.GetRequiredService<AccountFactory>());
		Assert.Same(runtime.Certificates, provider.GetRequiredService<CertificateFactory>());
		Assert.Same(runtime.KycCertificates, provider.GetRequiredService<KycCertificateFactory>());
		Assert.Same(runtime.Containers, provider.GetRequiredService<EncryptedContainerFactory>());
		Assert.Same(runtime.Sharables, provider.GetRequiredService<SharableCertificateAttributesFactory>());
	}

	[Fact]
	public void RegistersAKeetaClientBackedByTheHttpClientFactory()
	{
		var services = new ServiceCollection();
		services.AddKeetaNetAnchorKeetaClient("http://127.0.0.1:1/api/node");

		using ServiceProvider provider = services.BuildServiceProvider();
		using KeetaClient first = provider.GetRequiredService<KeetaClient>();
		using KeetaClient second = provider.GetRequiredService<KeetaClient>();
		Assert.NotSame(first, second);

		// The client registration also provides the shared runtime.
		WasmRuntime runtime = provider.GetRequiredService<WasmRuntime>();
		Assert.False(runtime.IsDisposed);
	}
}
