using System.Text.Json;

namespace KeetaNet.Anchor;

/// <summary>
/// An asset-movement anchor client bound to a signer and a metadata root.
/// Discovery, request signing, retries, and the account-status blocker fold all
/// run inside the wasm core. The client is thread-safe: operations serialize
/// onto the runtime's dispatcher, and every networked method honors its
/// <see cref="CancellationToken"/> before dispatch and during host HTTP and sleeps.
/// </summary>
public sealed class AssetMovementClient : WasmObject
{
	private AssetMovementClient(WasmRuntime runtime, int handle)
		: base(runtime, handle)
	{
	}

	/// <summary>
	/// Build a client signed by an existing <paramref name="account"/>, resolving
	/// providers from <paramref name="root"/>'s on-chain service metadata read via
	/// the node API at <paramref name="nodeUrl"/>. Reached through
	/// <see cref="WasmRuntime.CreateAssetMovementClient"/>.
	/// </summary>
	internal static AssetMovementClient WithAccount(WasmRuntime runtime, string nodeUrl, string root, Crypto.Account account)
	{
		int handle = runtime.AssetWithAccount(nodeUrl, root, account.Handle);
		return new AssetMovementClient(runtime, handle);
	}

	/// <summary>Every advertised provider.</summary>
	public async Task<IReadOnlyList<AssetProvider>> GetProvidersAsync(CancellationToken cancellationToken = default)
	{
		byte[] payload = await Runtime.AssetProviders(Handle, cancellationToken).ConfigureAwait(false);
		return KeetaJson.ReadList<AssetProvider>(payload);
	}

	/// <summary>The provider with <paramref name="id"/>, or null when none advertises it.</summary>
	public async Task<AssetProvider?> GetProviderByIdAsync(string id, CancellationToken cancellationToken = default)
	{
		byte[] payload = await Runtime.AssetProviderById(Handle, id, cancellationToken).ConfigureAwait(false);
		return ParseOptionalProvider(payload);
	}

	/// <summary>The provider signed by <paramref name="account"/>, or null when absent.</summary>
	public async Task<AssetProvider?> GetProviderByAccountAsync(string account, CancellationToken cancellationToken = default)
	{
		byte[] payload = await Runtime.AssetProviderByAccount(Handle, account, cancellationToken).ConfigureAwait(false);
		return ParseOptionalProvider(payload);
	}

	/// <summary>
	/// Every provider whose advertised <c>supportedAssets</c> satisfies
	/// <paramref name="search"/> (asset, endpoints, and directional rails).
	/// </summary>
	public async Task<IReadOnlyList<AssetProvider>> GetProvidersForTransferAsync(
		AssetProviderSearch search,
		CancellationToken cancellationToken = default)
	{
		string searchJson = Serialize(search);
		byte[] payload = await Runtime
			.AssetProvidersForTransfer(Handle, searchJson, cancellationToken)
			.ConfigureAwait(false);

		return KeetaJson.ReadList<AssetProvider>(payload);
	}

	/// <summary>
	/// Whether <paramref name="provider"/> advertises the
	/// <paramref name="operation"/> endpoint (e.g. <c>initiateTransfer</c>,
	/// <c>createPersistentForwarding</c>).
	/// </summary>
	public bool IsOperationSupported(AssetProvider provider, string operation) => provider.Operations.ContainsKey(operation);

	/// <summary>The provider's advertised legal disclaimers, or null when none.</summary>
	public JsonElement? GetLegalDisclaimers(AssetProvider provider) => provider.Legal;

	/// <summary>
	/// The legal disclaimers advertised by the provider with
	/// <paramref name="id"/>, or null when the provider or its disclaimers are
	/// absent.
	/// </summary>
	public async Task<JsonElement?> GetProviderLegalDisclaimersByIdAsync(
		string id,
		CancellationToken cancellationToken = default)
	{
		AssetProvider? provider = await GetProviderByIdAsync(id, cancellationToken).ConfigureAwait(false);
		return provider?.Legal;
	}

	/// <summary>
	/// The provider's display metadata for <paramref name="asset"/> (an external
	/// chain asset id) at <paramref name="location"/> (a canonical location
	/// string), or null when the provider advertises none.
	/// </summary>
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
	public async Task<AssetSimulatedTransfer> SimulateTransferAsync(
		AssetProvider provider,
		AssetTransferRequest request,
		CancellationToken cancellationToken = default)
	{
		var transport = await ReadOperationAsync<AssetSimulatedTransferTransport>(Runtime.AssetSimulateTransfer, provider, request, cancellationToken).ConfigureAwait(false);
		return new AssetSimulatedTransfer(this, provider, request, transport.InstructionChoices);
	}

	/// <summary>Initiate a transfer, returning a fluent handle. The request's recipient is required.</summary>
	public async Task<AssetTransfer> InitiateTransferAsync(
		AssetProvider provider,
		AssetTransferRequest request,
		CancellationToken cancellationToken = default)
	{
		var transport = await ReadOperationAsync<AssetTransferTransport>(Runtime.AssetInitiateTransfer, provider, request, cancellationToken).ConfigureAwait(false);
		return new AssetTransfer(this, provider, transport.Id, transport.InstructionChoices);
	}

	/// <summary>Execute a pull instruction for a transfer.</summary>
	public Task<AssetTransferStatus> ExecuteTransferAsync(
		AssetProvider provider,
		AssetExecuteRequest request,
		CancellationToken cancellationToken = default) =>
		ReadOperationAsync<AssetTransferStatus>(Runtime.AssetExecuteTransfer, provider, request, cancellationToken);

	/// <summary>Read the status of transfer <paramref name="id"/>.</summary>
	public Task<AssetTransferStatus> GetTransferStatusAsync(
		AssetProvider provider,
		string id,
		CancellationToken cancellationToken = default) =>
		ReadOperationForIdAsync<AssetTransferStatus>(Runtime.AssetTransferStatus, provider, id, cancellationToken);

	/// <summary>Read whether the signer's account is ready to use this provider.</summary>
	public async Task<AssetAccountStatus> GetAccountStatusAsync(
		AssetProvider provider,
		CancellationToken cancellationToken = default)
	{
		string providerJson = Serialize(provider);
		byte[] payload = await Runtime.AssetAccountStatus(Handle, providerJson, cancellationToken).ConfigureAwait(false);

		return Read<AssetAccountStatus>(payload);
	}

	/// <summary>Open a persistent-forwarding template session.</summary>
	public Task<AssetTemplateSession> InitiatePersistentForwardingTemplateAsync(
		AssetProvider provider,
		AssetInitiateTemplateRequest request,
		CancellationToken cancellationToken = default) =>
		ReadOperationAsync<AssetTemplateSession>(Runtime.AssetInitiateForwardingTemplate, provider, request, cancellationToken);

	/// <summary>Create a persistent-forwarding template.</summary>
	public Task<AssetForwardingTemplate> CreatePersistentForwardingTemplateAsync(
		AssetProvider provider,
		AssetCreateTemplateRequest request,
		CancellationToken cancellationToken = default) =>
		ReadOperationAsync<AssetForwardingTemplate>(Runtime.AssetCreateForwardingTemplate, provider, request, cancellationToken);

	/// <summary>List persistent-forwarding templates.</summary>
	public Task<AssetTemplatePage> ListForwardingAddressTemplatesAsync(
		AssetProvider provider,
		AssetListTemplatesRequest request,
		CancellationToken cancellationToken = default) =>
		ReadOperationAsync<AssetTemplatePage>(Runtime.AssetListForwardingTemplates, provider, request, cancellationToken);

	/// <summary>Create a persistent-forwarding address, returning its (obfuscated) details.</summary>
	public Task<JsonElement> CreatePersistentForwardingAddressAsync(
		AssetProvider provider,
		AssetCreateAddressRequest request,
		CancellationToken cancellationToken = default) =>
		ReadOperationAsync<JsonElement>(Runtime.AssetCreateForwardingAddress, provider, request, cancellationToken);

	/// <summary>List persistent-forwarding addresses.</summary>
	public Task<AssetAddressPage> ListForwardingAddressesAsync(
		AssetProvider provider,
		AssetListAddressesRequest request,
		CancellationToken cancellationToken = default) =>
		ReadOperationAsync<AssetAddressPage>(Runtime.AssetListForwardingAddresses, provider, request, cancellationToken);

	/// <summary>Deactivate a persistent-forwarding template by id.</summary>
	public Task DeactivatePersistentForwardingTemplateAsync(
		AssetProvider provider,
		string id,
		CancellationToken cancellationToken = default) =>
		RunOperationForIdAsync(Runtime.AssetDeactivateForwardingTemplate, provider, id, cancellationToken);

	/// <summary>Deactivate a persistent-forwarding address by id.</summary>
	public Task DeactivatePersistentForwardingAddressAsync(
		AssetProvider provider,
		string id,
		CancellationToken cancellationToken = default) =>
		RunOperationForIdAsync(Runtime.AssetDeactivateForwardingAddress, provider, id, cancellationToken);

	/// <summary>List asset-movement transactions.</summary>
	public Task<AssetTransactionPage> ListTransactionsAsync(
		AssetProvider provider,
		AssetListTransactionsRequest request,
		CancellationToken cancellationToken = default) =>
		ReadOperationAsync<AssetTransactionPage>(Runtime.AssetListTransactions, provider, request, cancellationToken);

	/// <summary>
	/// Share KYC attributes with the provider, returning the outcome verbatim.
	/// A pending outcome carries the promise URL the caller must poll; use
	/// <see cref="ShareKycAttributesAndWaitAsync"/> to poll it automatically.
	/// </summary>
	public Task<AssetShareKycOutcome> ShareKycAttributesAsync(
		AssetProvider provider,
		AssetShareKycRequest request,
		CancellationToken cancellationToken = default) =>
		ReadOperationAsync<AssetShareKycOutcome>(Runtime.AssetShareKyc, provider, request, cancellationToken);

	/// <summary>
	/// Share KYC attributes and, when the outcome is pending with a promise URL,
	/// poll that URL inside the core until it resolves.
	/// </summary>
	public async Task<AssetShareKycOutcome> ShareKycAttributesAndWaitAsync(
		AssetProvider provider,
		AssetShareKycRequest request,
		TimeSpan? pollInterval = null,
		TimeSpan? timeout = null,
		CancellationToken cancellationToken = default)
	{
		string providerJson = Serialize(provider);
		string requestJson = Serialize(request);
		int intervalMs = ToWholeMilliseconds(pollInterval);
		int timeoutMs = ToWholeMilliseconds(timeout);
		byte[] payload = await Runtime
			.AssetShareKycAwait(Handle, providerJson, requestJson, intervalMs, timeoutMs, cancellationToken)
			.ConfigureAwait(false);

		return Read<AssetShareKycOutcome>(payload);
	}

	/// <summary>Drive a provider operation whose <paramref name="request"/> crosses as JSON.</summary>
	private async Task<TResponse> ReadOperationAsync<TResponse>(
		Func<int, string, string, CancellationToken, Task<byte[]>> operation,
		AssetProvider provider,
		object request,
		CancellationToken cancellationToken)
	{
		string providerJson = Serialize(provider);
		string requestJson = Serialize(request);

		byte[] payload = await operation(Handle, providerJson, requestJson, cancellationToken).ConfigureAwait(false);
		return Read<TResponse>(payload);
	}

	/// <summary>Drive a provider operation keyed by a raw <paramref name="id"/>.</summary>
	private async Task<TResponse> ReadOperationForIdAsync<TResponse>(
		Func<int, string, string, CancellationToken, Task<byte[]>> operation,
		AssetProvider provider,
		string id,
		CancellationToken cancellationToken)
	{
		string providerJson = Serialize(provider);

		byte[] payload = await operation(Handle, providerJson, id, cancellationToken).ConfigureAwait(false);
		return Read<TResponse>(payload);
	}

	/// <summary>Drive a provider operation keyed by a raw <paramref name="id"/>, discarding the response.</summary>
	private async Task RunOperationForIdAsync(
		Func<int, string, string, CancellationToken, Task<byte[]>> operation,
		AssetProvider provider,
		string id,
		CancellationToken cancellationToken)
	{
		string providerJson = Serialize(provider);
		await operation(Handle, providerJson, id, cancellationToken).ConfigureAwait(false);
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

	private protected override void Release(WasmRuntime runtime, int handle) => runtime.AssetFree(handle);
}
