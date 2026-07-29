namespace KeetaNet.Anchor;

/// <summary>
/// One asset-movement provider bound to its discovering client (the reference
/// provider handle): a metadata snapshot in <see cref="Info"/> plus every
/// per-provider operation, signed and retried by the client it came from.
/// Obtained from the <see cref="AssetMovementClient"/> discovery methods or
/// re-bound from a stored snapshot with
/// <see cref="AssetMovementClient.Provider"/>.
/// </summary>
public sealed class AssetProvider
{
	private readonly AssetMovementClient _client;

	internal AssetProvider(AssetMovementClient client, AssetProviderInfo info)
	{
		_client = client;
		Info = info;
	}

	/// <summary>The provider's advertised metadata snapshot.</summary>
	public AssetProviderInfo Info { get; }

	/// <summary>The provider's id.</summary>
	public string Id => Info.Id;

	/// <inheritdoc cref="AssetProviderInfo.IsOperationSupported"/>
	public bool IsOperationSupported(string operation) => Info.IsOperationSupported(operation);

	/// <inheritdoc cref="AssetProviderInfo.GetLegalDisclaimers"/>
	public IReadOnlyList<AssetDisclaimer>? GetLegalDisclaimers() => Info.GetLegalDisclaimers();

	/// <inheritdoc cref="AssetProviderInfo.GetAnchorDetails"/>
	public AssetAnchorDetails? GetAnchorDetails() => Info.GetAnchorDetails();

	/// <inheritdoc cref="AssetProviderInfo.GetAssetMetadataForLocation"/>
	public AssetTokenMetadata? GetAssetMetadataForLocation(string location, string asset) =>
		Info.GetAssetMetadataForLocation(location, asset);

	/// <summary>Simulate a transfer, returning a fluent handle over its instruction choices.</summary>
	public Task<AssetSimulatedTransfer> SimulateTransfer(
		AssetTransferRequest request,
		CancellationToken cancellationToken = default) =>
		_client.SimulateTransfer(this, request, cancellationToken);

	/// <summary>Initiate a transfer, returning a fluent handle. The request's recipient is required.</summary>
	public Task<AssetTransfer> InitiateTransfer(
		AssetTransferRequest request,
		CancellationToken cancellationToken = default) =>
		_client.InitiateTransfer(this, request, cancellationToken);

	/// <summary>Execute a pull instruction for a transfer.</summary>
	public Task<AssetTransferStatus> ExecuteTransfer(
		AssetExecuteRequest request,
		CancellationToken cancellationToken = default) =>
		_client.ExecuteTransfer(Info, request, cancellationToken);

	/// <summary>Read the status of transfer <paramref name="id"/>.</summary>
	public Task<AssetTransferStatus> GetTransferStatus(string id, CancellationToken cancellationToken = default) =>
		_client.GetTransferStatus(Info, id, cancellationToken);

	/// <summary>Read whether the signer's account is ready to use this provider.</summary>
	public Task<AssetAccountStatus> GetAccountStatus(CancellationToken cancellationToken = default) =>
		_client.GetAccountStatus(Info, cancellationToken);

	/// <summary>Open a persistent-forwarding template session.</summary>
	public Task<AssetTemplateSession> InitiatePersistentForwardingTemplate(
		AssetInitiateTemplateRequest request,
		CancellationToken cancellationToken = default) =>
		_client.InitiatePersistentForwardingTemplate(Info, request, cancellationToken);

	/// <summary>Create a persistent-forwarding template.</summary>
	public Task<AssetForwardingTemplate> CreatePersistentForwardingTemplate(
		AssetCreateTemplateRequest request,
		CancellationToken cancellationToken = default) =>
		_client.CreatePersistentForwardingTemplate(Info, request, cancellationToken);

	/// <summary>List persistent-forwarding templates.</summary>
	public Task<AssetTemplatePage> ListForwardingAddressTemplates(
		AssetListTemplatesRequest request,
		CancellationToken cancellationToken = default) =>
		_client.ListForwardingAddressTemplates(Info, request, cancellationToken);

	/// <summary>Create a persistent-forwarding address, returning its (obfuscated) details.</summary>
	public Task<AssetForwardingAddress> CreatePersistentForwardingAddress(
		AssetCreateAddressRequest request,
		CancellationToken cancellationToken = default) =>
		_client.CreatePersistentForwardingAddress(Info, request, cancellationToken);

	/// <summary>List persistent-forwarding addresses.</summary>
	public Task<AssetAddressPage> ListForwardingAddresses(
		AssetListAddressesRequest request,
		CancellationToken cancellationToken = default) =>
		_client.ListForwardingAddresses(Info, request, cancellationToken);

	/// <summary>Deactivate a persistent-forwarding template by id.</summary>
	public Task DeactivatePersistentForwardingTemplate(string id, CancellationToken cancellationToken = default) =>
		_client.DeactivatePersistentForwardingTemplate(Info, id, cancellationToken);

	/// <summary>Deactivate a persistent-forwarding address by id.</summary>
	public Task DeactivatePersistentForwardingAddress(string id, CancellationToken cancellationToken = default) =>
		_client.DeactivatePersistentForwardingAddress(Info, id, cancellationToken);

	/// <summary>List asset-movement transactions.</summary>
	public Task<AssetTransactionPage> ListTransactions(
		AssetListTransactionsRequest request,
		CancellationToken cancellationToken = default) =>
		_client.ListTransactions(Info, request, cancellationToken);

	/// <summary>
	/// Share KYC attributes with the provider, returning the provider's outcome unchanged.
	/// A pending outcome carries the promise URL the caller must poll. Use
	/// <see cref="ShareKycAttributesAndWait"/> to poll it automatically.
	/// </summary>
	public Task<AssetShareKycOutcome> ShareKycAttributes(
		AssetShareKycRequest request,
		CancellationToken cancellationToken = default) =>
		_client.ShareKycAttributes(Info, request, cancellationToken);

	/// <summary>
	/// Share KYC attributes and, when the outcome is pending with a promise URL,
	/// poll that URL inside the core until it resolves.
	/// </summary>
	public Task<AssetShareKycOutcome> ShareKycAttributesAndWait(
		AssetShareKycRequest request,
		TimeSpan? pollInterval = null,
		TimeSpan? timeout = null,
		CancellationToken cancellationToken = default) =>
		_client.ShareKycAttributesAndWait(Info, request, pollInterval, timeout, cancellationToken);
}
