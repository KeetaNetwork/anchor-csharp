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

	internal byte[] AssetProviders(int handle)
	{
		int result = Invoke<int, int>("keeta_asset_providers", handle);
		return TakeBytes(result);
	}

	internal byte[] AssetProviderById(int handle, string id) =>
		WithHandleAndText("keeta_asset_provider_by_id", handle, id);

	internal byte[] AssetProviderByAccount(int handle, string account) =>
		WithHandleAndText("keeta_asset_provider_by_account", handle, account);

	internal byte[] AssetProvidersForTransfer(int handle, string searchJson) =>
		WithHandleAndText("keeta_asset_providers_for_transfer", handle, searchJson);

	internal byte[] AssetSimulateTransfer(int handle, string providerJson, string requestJson) =>
		WithProviderAndArg("keeta_asset_simulate_transfer", handle, providerJson, requestJson);

	internal byte[] AssetInitiateTransfer(int handle, string providerJson, string requestJson) =>
		WithProviderAndArg("keeta_asset_initiate_transfer", handle, providerJson, requestJson);

	internal byte[] AssetExecuteTransfer(int handle, string providerJson, string requestJson) =>
		WithProviderAndArg("keeta_asset_execute_transfer", handle, providerJson, requestJson);

	internal byte[] AssetTransferStatus(int handle, string providerJson, string id) =>
		WithProviderAndArg("keeta_asset_transfer_status", handle, providerJson, id);

	internal byte[] AssetAccountStatus(int handle, string providerJson) =>
		WithHandleAndText("keeta_asset_account_status", handle, providerJson);

	internal byte[] AssetInitiateForwardingTemplate(int handle, string providerJson, string requestJson) =>
		WithProviderAndArg("keeta_asset_initiate_forwarding_template", handle, providerJson, requestJson);

	internal byte[] AssetCreateForwardingTemplate(int handle, string providerJson, string requestJson) =>
		WithProviderAndArg("keeta_asset_create_forwarding_template", handle, providerJson, requestJson);

	internal byte[] AssetListForwardingTemplates(int handle, string providerJson, string requestJson) =>
		WithProviderAndArg("keeta_asset_list_forwarding_templates", handle, providerJson, requestJson);

	internal byte[] AssetCreateForwardingAddress(int handle, string providerJson, string requestJson) =>
		WithProviderAndArg("keeta_asset_create_forwarding_address", handle, providerJson, requestJson);

	internal byte[] AssetListForwardingAddresses(int handle, string providerJson, string requestJson) =>
		WithProviderAndArg("keeta_asset_list_forwarding_addresses", handle, providerJson, requestJson);

	internal byte[] AssetDeactivateForwardingTemplate(int handle, string providerJson, string id) =>
		WithProviderAndArg("keeta_asset_deactivate_forwarding_template", handle, providerJson, id);

	internal byte[] AssetDeactivateForwardingAddress(int handle, string providerJson, string id) =>
		WithProviderAndArg("keeta_asset_deactivate_forwarding_address", handle, providerJson, id);

	internal byte[] AssetListTransactions(int handle, string providerJson, string requestJson) =>
		WithProviderAndArg("keeta_asset_list_transactions", handle, providerJson, requestJson);

	internal byte[] AssetShareKyc(int handle, string providerJson, string requestJson) =>
		WithProviderAndArg("keeta_asset_share_kyc", handle, providerJson, requestJson);

	internal byte[] AssetShareKycAwait(int handle, string providerJson, string requestJson, int intervalMs, int timeoutMs)
	{
		using var arguments = new ArgumentScope(this);
		Argument provider = arguments.Write(providerJson);
		Argument request = arguments.Write(requestJson);

		int result = Invoke<int, int, int, int, int, int, int, int>("keeta_asset_share_kyc_await", handle,
			provider.Pointer, provider.Length,
			request.Pointer, request.Length,
			intervalMs, timeoutMs);
		return TakeBytes(result);
	}

	internal void AssetFree(int handle) => Free("keeta_asset_free", handle);
}
