using System.Text.Json;

namespace KeetaNet.Anchor;

/// <summary>
/// An asset-movement anchor client bound to a signer and a metadata root.
/// Discovery, request signing, retries, and the account-status blocker fold all
/// run inside the wasm core. Discovery returns <see cref="AssetProvider"/>
/// handles carrying every per-provider operation. The client is thread-safe:
/// operations serialize onto the runtime's dispatcher, and every networked
/// method honors its <see cref="CancellationToken"/> before dispatch and during
/// host HTTP and sleeps.
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
	public async Task<IReadOnlyList<AssetProvider>> GetProviders(CancellationToken cancellationToken = default)
	{
		byte[] payload = await Runtime.AssetProviders(Handle, cancellationToken).ConfigureAwait(false);
		return BindAll(KeetaJson.ReadList<AssetProviderInfo>(payload));
	}

	/// <summary>The provider with <paramref name="id"/>, or null when none advertises it.</summary>
	public async Task<AssetProvider?> GetProviderById(string id, CancellationToken cancellationToken = default)
	{
		byte[] payload = await Runtime.AssetProviderById(Handle, id, cancellationToken).ConfigureAwait(false);
		return BindOptional(payload);
	}

	/// <summary>The provider signed by <paramref name="account"/>, or null when absent.</summary>
	public async Task<AssetProvider?> GetProviderByAccount(
		Crypto.Account account,
		CancellationToken cancellationToken = default) =>
		await GetProviderByAccount(account.PublicKeyString, cancellationToken).ConfigureAwait(false);

	/// <summary>The provider signed by <paramref name="account"/> (a <c>keeta_</c> public key string), or null when absent.</summary>
	public async Task<AssetProvider?> GetProviderByAccount(string account, CancellationToken cancellationToken = default)
	{
		byte[] payload = await Runtime.AssetProviderByAccount(Handle, account, cancellationToken).ConfigureAwait(false);
		return BindOptional(payload);
	}

	/// <summary>
	/// Every provider whose advertised <c>supportedAssets</c> satisfies
	/// <paramref name="search"/> (asset, endpoints, and directional rails).
	/// </summary>
	public async Task<IReadOnlyList<AssetProvider>> GetProvidersForTransfer(
		AssetProviderSearch search,
		CancellationToken cancellationToken = default)
	{
		string searchJson = Serialize(search);
		byte[] payload = await Runtime
			.AssetProvidersForTransfer(Handle, searchJson, cancellationToken)
			.ConfigureAwait(false);

		return BindAll(KeetaJson.ReadList<AssetProviderInfo>(payload));
	}

	/// <summary>Bind a stored metadata snapshot back to this client as an operable handle.</summary>
	public AssetProvider Provider(AssetProviderInfo info) => new(this, info);

	/// <summary>
	/// The legal disclaimers advertised by the provider with
	/// <paramref name="id"/>, or null when the provider or its disclaimers are
	/// absent.
	/// </summary>
	public async Task<IReadOnlyList<AssetDisclaimer>?> GetProviderLegalDisclaimersById(
		string id,
		CancellationToken cancellationToken = default)
	{
		AssetProvider? provider = await GetProviderById(id, cancellationToken).ConfigureAwait(false);
		return provider?.GetLegalDisclaimers();
	}

	/// <summary>Simulate a transfer for <paramref name="provider"/>.</summary>
	internal async Task<AssetSimulatedTransfer> SimulateTransfer(
		AssetProvider provider,
		AssetTransferRequest request,
		CancellationToken cancellationToken)
	{
		var transport = await ReadOperationAsync<AssetSimulatedTransferTransport>(Runtime.AssetSimulateTransfer, provider.Info, request, cancellationToken).ConfigureAwait(false);
		return new AssetSimulatedTransfer(provider, request, transport.InstructionChoices);
	}

	/// <summary>Initiate a transfer for <paramref name="provider"/>.</summary>
	internal async Task<AssetTransfer> InitiateTransfer(
		AssetProvider provider,
		AssetTransferRequest request,
		CancellationToken cancellationToken)
	{
		var transport = await ReadOperationAsync<AssetTransferTransport>(Runtime.AssetInitiateTransfer, provider.Info, request, cancellationToken).ConfigureAwait(false);
		return new AssetTransfer(provider, transport.Id, transport.InstructionChoices);
	}

	/// <summary>Execute a pull instruction for a transfer.</summary>
	internal Task<AssetTransferStatus> ExecuteTransfer(
		AssetProviderInfo provider,
		AssetExecuteRequest request,
		CancellationToken cancellationToken) =>
		ReadOperationAsync<AssetTransferStatus>(Runtime.AssetExecuteTransfer, provider, request, cancellationToken);

	/// <summary>Read the status of transfer <paramref name="id"/>.</summary>
	internal Task<AssetTransferStatus> GetTransferStatus(
		AssetProviderInfo provider,
		string id,
		CancellationToken cancellationToken) =>
		ReadOperationForIdAsync<AssetTransferStatus>(Runtime.AssetTransferStatus, provider, id, cancellationToken);

	/// <summary>Read whether the signer's account is ready to use <paramref name="provider"/>.</summary>
	internal async Task<AssetAccountStatus> GetAccountStatus(
		AssetProviderInfo provider,
		CancellationToken cancellationToken)
	{
		string providerJson = Serialize(provider);
		byte[] payload = await Runtime.AssetAccountStatus(Handle, providerJson, cancellationToken).ConfigureAwait(false);

		return Read<AssetAccountStatus>(payload);
	}

	/// <summary>Open a persistent-forwarding template session.</summary>
	internal Task<AssetTemplateSession> InitiatePersistentForwardingTemplate(
		AssetProviderInfo provider,
		AssetInitiateTemplateRequest request,
		CancellationToken cancellationToken) =>
		ReadOperationAsync<AssetTemplateSession>(Runtime.AssetInitiatePersistentForwardingTemplate, provider, request, cancellationToken);

	/// <summary>Create a persistent-forwarding template.</summary>
	internal Task<AssetForwardingTemplate> CreatePersistentForwardingTemplate(
		AssetProviderInfo provider,
		AssetCreateTemplateRequest request,
		CancellationToken cancellationToken) =>
		ReadOperationAsync<AssetForwardingTemplate>(Runtime.AssetCreatePersistentForwardingTemplate, provider, request, cancellationToken);

	/// <summary>List persistent-forwarding templates.</summary>
	internal Task<AssetTemplatePage> ListForwardingAddressTemplates(
		AssetProviderInfo provider,
		AssetListTemplatesRequest request,
		CancellationToken cancellationToken) =>
		ReadOperationAsync<AssetTemplatePage>(Runtime.AssetListForwardingAddressTemplates, provider, request, cancellationToken);

	/// <summary>Create a persistent-forwarding address, returning its (obfuscated) details.</summary>
	internal Task<AssetForwardingAddress> CreatePersistentForwardingAddress(
		AssetProviderInfo provider,
		AssetCreateAddressRequest request,
		CancellationToken cancellationToken) =>
		ReadOperationAsync<AssetForwardingAddress>(Runtime.AssetCreatePersistentForwardingAddress, provider, request, cancellationToken);

	/// <summary>List persistent-forwarding addresses.</summary>
	internal Task<AssetAddressPage> ListForwardingAddresses(
		AssetProviderInfo provider,
		AssetListAddressesRequest request,
		CancellationToken cancellationToken) =>
		ReadOperationAsync<AssetAddressPage>(Runtime.AssetListForwardingAddresses, provider, request, cancellationToken);

	/// <summary>Deactivate a persistent-forwarding template by id.</summary>
	internal Task DeactivatePersistentForwardingTemplate(
		AssetProviderInfo provider,
		string id,
		CancellationToken cancellationToken) =>
		RunOperationForIdAsync(Runtime.AssetDeactivatePersistentForwardingTemplate, provider, id, cancellationToken);

	/// <summary>Deactivate a persistent-forwarding address by id.</summary>
	internal Task DeactivatePersistentForwardingAddress(
		AssetProviderInfo provider,
		string id,
		CancellationToken cancellationToken) =>
		RunOperationForIdAsync(Runtime.AssetDeactivatePersistentForwardingAddress, provider, id, cancellationToken);

	/// <summary>List asset-movement transactions.</summary>
	internal Task<AssetTransactionPage> ListTransactions(
		AssetProviderInfo provider,
		AssetListTransactionsRequest request,
		CancellationToken cancellationToken) =>
		ReadOperationAsync<AssetTransactionPage>(Runtime.AssetListTransactions, provider, request, cancellationToken);

	/// <summary>Share KYC attributes with <paramref name="provider"/>, returning its outcome unchanged.</summary>
	internal Task<AssetShareKycOutcome> ShareKycAttributes(
		AssetProviderInfo provider,
		AssetShareKycRequest request,
		CancellationToken cancellationToken) =>
		ReadOperationAsync<AssetShareKycOutcome>(Runtime.AssetShareKycAttributes, provider, request, cancellationToken);

	/// <summary>
	/// Share KYC attributes and, when the outcome is pending with a promise URL,
	/// poll that URL inside the core until it resolves.
	/// </summary>
	internal async Task<AssetShareKycOutcome> ShareKycAttributesAndWait(
		AssetProviderInfo provider,
		AssetShareKycRequest request,
		TimeSpan? pollInterval,
		TimeSpan? timeout,
		CancellationToken cancellationToken)
	{
		string providerJson = Serialize(provider);
		string requestJson = Serialize(request);
		int intervalMs = ToWholeMilliseconds(pollInterval);
		int timeoutMs = ToWholeMilliseconds(timeout);
		byte[] payload = await Runtime
			.AssetShareKycAttributesAndWait(Handle, providerJson, requestJson, intervalMs, timeoutMs, cancellationToken)
			.ConfigureAwait(false);

		return Read<AssetShareKycOutcome>(payload);
	}

	/// <summary>Drive a provider operation whose <paramref name="request"/> crosses as JSON.</summary>
	private async Task<TResponse> ReadOperationAsync<TResponse>(
		Func<int, string, string, CancellationToken, Task<byte[]>> operation,
		AssetProviderInfo provider,
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
		AssetProviderInfo provider,
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
		AssetProviderInfo provider,
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

	/// <summary>Bind every discovered snapshot to this client.</summary>
	private AssetProvider[] BindAll(IReadOnlyList<AssetProviderInfo> infos) =>
		infos.Select(Provider).ToArray();

	/// <summary>Bind an optional discovery payload, mapping JSON null to no provider.</summary>
	private AssetProvider? BindOptional(byte[] payload)
	{
		using var document = JsonDocument.Parse(payload);
		JsonElement root = document.RootElement;
		if (root.ValueKind == JsonValueKind.Null)
		{
			return null;
		}

		AssetProviderInfo? info = root.Deserialize<AssetProviderInfo>(KeetaJson.Options);
		if (info is null)
		{
			return null;
		}

		return Provider(info);
	}

	private protected override void Release(WasmRuntime runtime, int handle) => runtime.AssetFree(handle);
}
