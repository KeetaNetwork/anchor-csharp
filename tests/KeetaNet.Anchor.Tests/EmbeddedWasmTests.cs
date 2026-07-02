using System.Reflection;
using Wasmtime;
using Xunit;

using WasmModule = Wasmtime.Module;

namespace KeetaNet.Anchor.Tests;

public sealed class EmbeddedWasmTests
{
	private const string ResourceName = "KeetaNet.Anchor.keetanetwork_anchor_client_wasi.wasm";

	[Fact]
	public void EmbeddedCoreModuleExposesTheAbi()
	{
		Assembly assembly = typeof(Sdk).Assembly;
		using Stream? resource = assembly.GetManifestResourceStream(ResourceName);
		Assert.NotNull(resource);

		using var payload = new MemoryStream();
		resource.CopyTo(payload);

		using var engine = new Engine();
		byte[] core = payload.ToArray();
		using WasmModule module = WasmModule.FromBytes(engine, "core", core);

		var exports = module.Exports.Select(export => export.Name).ToArray();
		Assert.Contains("memory", exports);
		Assert.Contains("keeta_alloc", exports);
		Assert.Contains("keeta_dealloc", exports);
		Assert.Contains("keeta_bytes_free", exports);
		Assert.Contains("keeta_last_error_code", exports);
		Assert.Contains("keeta_kyc_with_account", exports);
		Assert.Contains("keeta_asset_with_account", exports);
	}
}
