using KeetaNet.Anchor.Crypto;

namespace KeetaNet.Anchor;

/// <summary>
/// The runtime's public creation surface: domain factories for handle-backed
/// crypto objects, and creation methods for the networked clients. Everything
/// created here is owned by this runtime and must be disposed before it.
/// </summary>
public sealed partial class WasmRuntime
{
	/// <summary>Creates accounts: signers from key material, read-only accounts from addresses or public keys.</summary>
	public AccountFactory Accounts { get; }

	/// <summary>Parses base X.509 certificates: provider CAs, trust roots, intermediates.</summary>
	public CertificateFactory Certificates { get; }

	/// <summary>Parses and issues KYC leaf certificates.</summary>
	public KycCertificateFactory KycCertificates { get; }

	/// <summary>Creates hybrid-encrypted, optionally signed containers.</summary>
	public EncryptedContainerFactory Containers { get; }

	/// <summary>Creates and opens selectively disclosed attribute bundles.</summary>
	public SharableCertificateAttributesFactory Sharables { get; }

	/// <summary>Creates block builders, ledger operations, and parsed blocks.</summary>
	public BlockFactory Blocks { get; }

	/// <summary>
	/// Create a KYC anchor client signed by <paramref name="account"/>, resolving
	/// providers from <paramref name="root"/>'s on-chain service metadata read via
	/// the node API at <paramref name="nodeUrl"/>.
	/// </summary>
	public KycClient CreateKycClient(string nodeUrl, string root, Account account) =>
		KycClient.WithAccount(this, nodeUrl, root, account);

	/// <summary>
	/// Create an asset-movement anchor client signed by <paramref name="account"/>,
	/// resolving providers from <paramref name="root"/>'s on-chain service metadata
	/// read via the node API at <paramref name="nodeUrl"/>.
	/// </summary>
	public AssetMovementClient CreateAssetMovementClient(string nodeUrl, string root, Account account) =>
		AssetMovementClient.WithAccount(this, nodeUrl, root, account);

	/// <summary>
	/// Create the base client for the node API at <paramref name="nodeUrl"/>.
	/// An injected <paramref name="httpClient"/> (for example from
	/// <c>IHttpClientFactory</c>) is borrowed, not disposed. Absent one the
	/// client owns its own. Binding <paramref name="network"/> enables the
	/// write path (<see cref="KeetaClient.Transmit(Crypto.Block, TransmitOptions?, CancellationToken)"/>
	/// and fee blocks). A client without one stays read-only.
	/// </summary>
	public KeetaClient CreateKeetaClient(string nodeUrl, HttpClient? httpClient = null, long? network = null) =>
		new(this, nodeUrl, httpClient, network);

	/// <summary>
	/// Create a client bound to <paramref name="signer"/> (null for a
	/// read-only client), operating as <paramref name="account"/> when given
	/// and as the signer itself otherwise. Both accounts are borrowed, not
	/// disposed. See <see cref="CreateKeetaClient"/> for the remaining
	/// parameters.
	/// </summary>
	public UserClient CreateUserClient(
		string nodeUrl,
		Account? signer,
		HttpClient? httpClient = null,
		long? network = null,
		Account? account = null) =>
		new(this, nodeUrl, httpClient, network, signer, account);
}
