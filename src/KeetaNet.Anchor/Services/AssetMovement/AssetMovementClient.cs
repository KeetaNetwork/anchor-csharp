using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace KeetaNet.Anchor;

/// <summary>
/// An asset-movement anchor client bound to a signer and a metadata root.
/// Discovery, request signing, retries, and the account-status blocker fold all
/// run inside the wasm core.
/// </summary>
public sealed class AssetMovementClient : IDisposable
{
	private readonly WasmRuntime _runtime;
	private readonly int _handle;
	private bool _disposed;

	private AssetMovementClient(WasmRuntime runtime, int handle)
	{
		_runtime = runtime;
		_handle = handle;
	}

	/// <summary>
	/// Build a client signed by an existing <paramref name="account"/> from the
	/// <c>crypto</c> surface, resolving providers from <paramref name="root"/>'s
	/// on-chain service metadata read via the node API at <paramref name="nodeUrl"/>.
	/// </summary>
	public static AssetMovementClient WithAccount(WasmRuntime runtime, string nodeUrl, string root, Crypto.Account account)
	{
		int handle = runtime.AssetWithAccount(nodeUrl, root, account.Handle);
		return new AssetMovementClient(runtime, handle);
	}

	/// <summary>Every advertised provider.</summary>
	public IReadOnlyList<AssetProvider> Providers()
	{
		byte[] payload = _runtime.AssetProviders(_handle);
		return KeetaJson.ReadList<AssetProvider>(payload);
	}

	/// <summary>The provider with <paramref name="id"/>, or null when none advertises it.</summary>
	public AssetProvider? ProviderById(string id) => FindProvider(_runtime.AssetProviderById, id);

	/// <summary>The provider signed by <paramref name="account"/>, or null when absent.</summary>
	public AssetProvider? ProviderByAccount(string account) => FindProvider(_runtime.AssetProviderByAccount, account);

	/// <summary>
	/// Every provider whose advertised <c>supportedAssets</c> satisfies
	/// <paramref name="search"/> (asset, endpoints, and directional rails).
	/// </summary>
	public IReadOnlyList<AssetProvider> GetProvidersForTransfer(AssetProviderSearch search)
	{
		string searchJson = Serialize(search);

		byte[] payload = _runtime.AssetProvidersForTransfer(_handle, searchJson);
		return KeetaJson.ReadList<AssetProvider>(payload);
	}

	/// <summary>
	/// Whether <paramref name="provider"/> advertises the
	/// <paramref name="operation"/> endpoint (e.g. <c>initiateTransfer</c>,
	/// <c>createPersistentForwarding</c>).
	/// </summary>
	[SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "An instance member for API symmetry with the TypeScript client.")]
	public bool IsOperationSupported(AssetProvider provider, string operation) =>
		provider.Operations.ContainsKey(operation);

	/// <summary>The provider's advertised legal disclaimers, or null when none.</summary>
	[SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "An instance member for API symmetry with the TypeScript client.")]
	public JsonElement? GetLegalDisclaimers(AssetProvider provider) => provider.Legal;

	/// <summary>
	/// The legal disclaimers advertised by the provider with
	/// <paramref name="id"/>, or null when the provider or its disclaimers are
	/// absent.
	/// </summary>
	public JsonElement? GetProviderLegalDisclaimersById(string id) => ProviderById(id)?.Legal;

	/// <summary>
	/// The provider's display metadata for <paramref name="asset"/> (an external
	/// chain asset id) at <paramref name="location"/> (a canonical location
	/// string), or null when the provider advertises none.
	/// </summary>
	[SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "An instance member for API symmetry with the TypeScript client.")]
	public JsonElement? GetAssetMetadataForLocation(AssetProvider provider, string location, string asset)
	{
		if (provider.LocationMetadata is not { } metadata || metadata.ValueKind != JsonValueKind.Object)
		{
			return null;
		}

		if (!metadata.TryGetProperty(location, out JsonElement forLocation)
			|| forLocation.ValueKind != JsonValueKind.Object
			|| !forLocation.TryGetProperty("assets", out JsonElement assets)
			|| assets.ValueKind != JsonValueKind.Object)
		{
			return null;
		}

		if (!assets.TryGetProperty(asset, out JsonElement found))
		{
			return null;
		}

		return found;
	}

	/// <summary>Simulate a transfer, returning a fluent handle over its instruction choices.</summary>
	public AssetSimulatedTransfer SimulateTransfer(AssetProvider provider, AssetTransferRequest request)
	{
		var transport = ReadOperation<AssetSimulatedTransferTransport>(_runtime.AssetSimulateTransfer, provider, request);
		return new AssetSimulatedTransfer(this, provider, request, transport.InstructionChoices);
	}

	/// <summary>Initiate a transfer, returning a fluent handle. The request's recipient is required.</summary>
	public AssetTransfer InitiateTransfer(AssetProvider provider, AssetTransferRequest request)
	{
		var transport = ReadOperation<AssetTransferTransport>(_runtime.AssetInitiateTransfer, provider, request);
		return new AssetTransfer(this, provider, transport.Id, transport.InstructionChoices);
	}

	/// <summary>Execute a pull instruction for a transfer.</summary>
	public AssetTransferStatus ExecuteTransfer(AssetProvider provider, AssetExecuteRequest request) =>
		ReadOperation<AssetTransferStatus>(_runtime.AssetExecuteTransfer, provider, request);

	/// <summary>Read the status of transfer <paramref name="id"/>.</summary>
	public AssetTransferStatus TransferStatus(AssetProvider provider, string id) =>
		ReadOperationForId<AssetTransferStatus>(_runtime.AssetTransferStatus, provider, id);

	/// <summary>Read whether the signer's account is ready to use this provider.</summary>
	public AssetAccountStatus AccountStatus(AssetProvider provider)
	{
		string providerJson = Serialize(provider);

		byte[] payload = _runtime.AssetAccountStatus(_handle, providerJson);
		return Read<AssetAccountStatus>(payload);
	}

	/// <summary>Open a persistent-forwarding template session.</summary>
	public AssetTemplateSession InitiateForwardingTemplate(AssetProvider provider, AssetInitiateTemplateRequest request) =>
		ReadOperation<AssetTemplateSession>(_runtime.AssetInitiateForwardingTemplate, provider, request);

	/// <summary>Create a persistent-forwarding template.</summary>
	public AssetForwardingTemplate CreateForwardingTemplate(AssetProvider provider, AssetCreateTemplateRequest request) =>
		ReadOperation<AssetForwardingTemplate>(_runtime.AssetCreateForwardingTemplate, provider, request);

	/// <summary>List persistent-forwarding templates.</summary>
	public AssetTemplatePage ListForwardingTemplates(AssetProvider provider, AssetListTemplatesRequest request) =>
		ReadOperation<AssetTemplatePage>(_runtime.AssetListForwardingTemplates, provider, request);

	/// <summary>Create a persistent-forwarding address, returning its (obfuscated) details.</summary>
	public JsonElement CreateForwardingAddress(AssetProvider provider, AssetCreateAddressRequest request) =>
		ReadOperation<JsonElement>(_runtime.AssetCreateForwardingAddress, provider, request);

	/// <summary>List persistent-forwarding addresses.</summary>
	public AssetAddressPage ListForwardingAddresses(AssetProvider provider, AssetListAddressesRequest request) =>
		ReadOperation<AssetAddressPage>(_runtime.AssetListForwardingAddresses, provider, request);

	/// <summary>Deactivate a persistent-forwarding template by id.</summary>
	public void DeactivateForwardingTemplate(AssetProvider provider, string id) =>
		RunOperationForId(_runtime.AssetDeactivateForwardingTemplate, provider, id);

	/// <summary>Deactivate a persistent-forwarding address by id.</summary>
	public void DeactivateForwardingAddress(AssetProvider provider, string id) =>
		RunOperationForId(_runtime.AssetDeactivateForwardingAddress, provider, id);

	/// <summary>List asset-movement transactions.</summary>
	public AssetTransactionPage ListTransactions(AssetProvider provider, AssetListTransactionsRequest request) =>
		ReadOperation<AssetTransactionPage>(_runtime.AssetListTransactions, provider, request);

	/// <summary>
	/// Share KYC attributes with the provider, returning the outcome verbatim.
	/// A pending outcome carries the promise URL the caller must poll; use
	/// <see cref="ShareKycAndWait"/> to poll it automatically.
	/// </summary>
	public AssetShareKycOutcome ShareKyc(AssetProvider provider, AssetShareKycRequest request) =>
		ReadOperation<AssetShareKycOutcome>(_runtime.AssetShareKyc, provider, request);

	/// <summary>
	/// Share KYC attributes and, when the outcome is pending with a promise URL,
	/// poll that URL inside the core until it resolves.
	/// </summary>
	public AssetShareKycOutcome ShareKycAndWait(
		AssetProvider provider,
		AssetShareKycRequest request,
		TimeSpan? pollInterval = null,
		TimeSpan? timeout = null)
	{
		string providerJson = Serialize(provider);
		string requestJson = Serialize(request);
		int intervalMs = ToWholeMilliseconds(pollInterval);
		int timeoutMs = ToWholeMilliseconds(timeout);

		byte[] payload = _runtime.AssetShareKycAwait(_handle, providerJson, requestJson, intervalMs, timeoutMs);
		return Read<AssetShareKycOutcome>(payload);
	}

	/// <summary>Drive a provider operation whose <paramref name="request"/> crosses as JSON.</summary>
	private TResponse ReadOperation<TResponse>(
		Func<int, string, string, byte[]> operation,
		AssetProvider provider,
		object request)
	{
		string providerJson = Serialize(provider);
		string requestJson = Serialize(request);

		byte[] payload = operation(_handle, providerJson, requestJson);
		return Read<TResponse>(payload);
	}

	/// <summary>Drive a provider operation keyed by a raw <paramref name="id"/>.</summary>
	private TResponse ReadOperationForId<TResponse>(
		Func<int, string, string, byte[]> operation,
		AssetProvider provider,
		string id)
	{
		string providerJson = Serialize(provider);

		byte[] payload = operation(_handle, providerJson, id);
		return Read<TResponse>(payload);
	}

	/// <summary>Drive a provider operation keyed by a raw <paramref name="id"/>, discarding the response.</summary>
	private void RunOperationForId(Func<int, string, string, byte[]> operation, AssetProvider provider, string id)
	{
		string providerJson = Serialize(provider);
		operation(_handle, providerJson, id);
	}

	/// <summary>Look up a provider by <paramref name="key"/>, mapping a JSON <c>null</c> body to null.</summary>
	private AssetProvider? FindProvider(Func<int, string, byte[]> operation, string key)
	{
		byte[] payload = operation(_handle, key);
		return ParseOptionalProvider(payload);
	}

	/// <summary>A bound as whole milliseconds, with 0 selecting the core default.</summary>
	private static int ToWholeMilliseconds(TimeSpan? bound)
	{
		if (bound is not { } value || value <= TimeSpan.Zero)
		{
			return 0;
		}

		return (int)Math.Min(value.TotalMilliseconds, int.MaxValue);
	}

	private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, KeetaJson.Options);

	private static T Read<T>(byte[] payload) =>
		JsonSerializer.Deserialize<T>(payload, KeetaJson.Options)
		?? throw new KeetaException("DECODE", $"could not decode a {typeof(T).Name} from the asset-movement response");

	private static AssetProvider? ParseOptionalProvider(byte[] payload)
	{
		using var document = JsonDocument.Parse(payload);
		JsonElement root = document.RootElement;
		if (root.ValueKind == JsonValueKind.Null)
		{
			return null;
		}

		return root.Deserialize<AssetProvider>(KeetaJson.Options);
	}

	/// <summary>Release the core-module client handle.</summary>
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_runtime.AssetFree(_handle);
	}
}
