namespace KeetaNet.Anchor;

/// <summary>
/// The networked asset-movement surface of the P1 core module: discover
/// providers, then move assets, manage persistent forwarding, and share KYC.
/// Provider and request payloads cross as JSON strings.
/// </summary>
public sealed partial class WasmRuntime
{
	internal int AssetWithAccount(string nodeUrl, string root, int accountHandle) =>
		ClientWithAccount("keeta_asset_with_account", nodeUrl, root, accountHandle);

	internal Task<byte[]> AssetProviders(int handle, CancellationToken cancellationToken) =>
		RunAsync(
			() =>
			{
				int result = Invoke<int, int>("keeta_asset_providers", handle);
				return TakeBytes(result);
			},
			cancellationToken);

	internal Task<byte[]> AssetProviderById(int handle, string id, CancellationToken cancellationToken) =>
		RunAsync(() => WithHandleAndText("keeta_asset_provider_by_id", handle, id), cancellationToken);

	internal Task<byte[]> AssetProviderByAccount(int handle, string account, CancellationToken cancellationToken) =>
		RunAsync(() => WithHandleAndText("keeta_asset_provider_by_account", handle, account), cancellationToken);

	internal Task<byte[]> AssetProvidersForTransfer(int handle, string searchJson, CancellationToken cancellationToken) =>
		RunAsync(() => WithHandleAndText("keeta_asset_providers_for_transfer", handle, searchJson), cancellationToken);

	internal Task<byte[]> AssetSimulateTransfer(int handle, string providerJson, string requestJson, CancellationToken cancellationToken) =>
		RunAsync(() => WithProviderAndArg("keeta_asset_simulate_transfer", handle, providerJson, requestJson), cancellationToken);

	internal Task<byte[]> AssetInitiateTransfer(int handle, string providerJson, string requestJson, CancellationToken cancellationToken) =>
		RunAsync(() => WithProviderAndArg("keeta_asset_initiate_transfer", handle, providerJson, requestJson), cancellationToken);

	internal Task<byte[]> AssetExecuteTransfer(int handle, string providerJson, string requestJson, CancellationToken cancellationToken) =>
		RunAsync(() => WithProviderAndArg("keeta_asset_execute_transfer", handle, providerJson, requestJson), cancellationToken);

	internal Task<byte[]> AssetTransferStatus(int handle, string providerJson, string id, CancellationToken cancellationToken) =>
		RunAsync(() => WithProviderAndArg("keeta_asset_transfer_status", handle, providerJson, id), cancellationToken);

	internal Task<byte[]> AssetAccountStatus(int handle, string providerJson, CancellationToken cancellationToken) =>
		RunAsync(() => WithHandleAndText("keeta_asset_account_status", handle, providerJson), cancellationToken);

	internal Task<byte[]> AssetInitiatePersistentForwardingTemplate(int handle, string providerJson, string requestJson, CancellationToken cancellationToken) =>
		RunAsync(() => WithProviderAndArg("keeta_asset_initiate_persistent_forwarding_template", handle, providerJson, requestJson), cancellationToken);

	internal Task<byte[]> AssetCreatePersistentForwardingTemplate(int handle, string providerJson, string requestJson, CancellationToken cancellationToken) =>
		RunAsync(() => WithProviderAndArg("keeta_asset_create_persistent_forwarding_template", handle, providerJson, requestJson), cancellationToken);

	internal Task<byte[]> AssetListForwardingAddressTemplates(int handle, string providerJson, string requestJson, CancellationToken cancellationToken) =>
		RunAsync(() => WithProviderAndArg("keeta_asset_list_forwarding_address_templates", handle, providerJson, requestJson), cancellationToken);

	internal Task<byte[]> AssetCreatePersistentForwardingAddress(int handle, string providerJson, string requestJson, CancellationToken cancellationToken) =>
		RunAsync(() => WithProviderAndArg("keeta_asset_create_persistent_forwarding_address", handle, providerJson, requestJson), cancellationToken);

	internal Task<byte[]> AssetListForwardingAddresses(int handle, string providerJson, string requestJson, CancellationToken cancellationToken) =>
		RunAsync(() => WithProviderAndArg("keeta_asset_list_forwarding_addresses", handle, providerJson, requestJson), cancellationToken);

	internal Task<byte[]> AssetDeactivatePersistentForwardingTemplate(int handle, string providerJson, string id, CancellationToken cancellationToken) =>
		RunAsync(() => WithProviderAndArg("keeta_asset_deactivate_persistent_forwarding_template", handle, providerJson, id), cancellationToken);

	internal Task<byte[]> AssetDeactivatePersistentForwardingAddress(int handle, string providerJson, string id, CancellationToken cancellationToken) =>
		RunAsync(() => WithProviderAndArg("keeta_asset_deactivate_persistent_forwarding_address", handle, providerJson, id), cancellationToken);

	internal Task<byte[]> AssetListTransactions(int handle, string providerJson, string requestJson, CancellationToken cancellationToken) =>
		RunAsync(() => WithProviderAndArg("keeta_asset_list_transactions", handle, providerJson, requestJson), cancellationToken);

	internal Task<byte[]> AssetShareKycAttributes(int handle, string providerJson, string requestJson, CancellationToken cancellationToken) =>
		RunAsync(() => WithProviderAndArg("keeta_asset_share_kyc_attributes", handle, providerJson, requestJson), cancellationToken);

	internal Task<byte[]> AssetShareKycAttributesAndWait(
		int handle,
		string providerJson,
		string requestJson,
		int intervalMs,
		int timeoutMs,
		CancellationToken cancellationToken) =>
		RunAsync(
			() =>
			{
				using var arguments = new ArgumentScope(this);
				Argument provider = arguments.Write(providerJson);
				Argument request = arguments.Write(requestJson);

				int result = Invoke<int, int, int, int, int, int, int, int>(
					"keeta_asset_share_kyc_attributes_and_wait",
					handle,
					provider.Pointer, provider.Length,
					request.Pointer, request.Length,
					intervalMs, timeoutMs);
				return TakeBytes(result);
			},
			cancellationToken);

	internal void AssetFree(int handle) => RunFree("keeta_asset_free", handle);
}
