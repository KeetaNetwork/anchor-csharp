using KeetaNet.Anchor.Crypto;

namespace KeetaNet.Anchor;

/// <summary>
/// The runtime's public creation surface.
/// </summary>
/// <remarks>
/// The surface exposes domain factories for handle-backed crypto objects and
/// creation methods for the networked clients. This runtime owns everything
/// created here, and callers must dispose those objects before the runtime.
/// </remarks>
public sealed partial class WasmRuntime
{
	/// <summary>Creates signers from key material and read-only accounts from addresses or public keys.</summary>
	public AccountFactory Accounts { get; }

	/// <summary>Parses base X.509 certificates, such as provider CAs, trust roots, and intermediates.</summary>
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
	/// Creates a KYC anchor client signed by <paramref name="account"/>.
	/// </summary>
	/// <remarks>
	/// The client resolves providers from the on-chain service metadata of
	/// <paramref name="root"/> through the node API at <paramref name="nodeUrl"/>.
	/// </remarks>
	public KycClient CreateKycClient(string nodeUrl, string root, Account account) =>
		KycClient.WithAccount(this, nodeUrl, root, account);

	/// <summary>
	/// Creates an asset-movement anchor client signed by <paramref name="account"/>.
	/// </summary>
	/// <remarks>
	/// The client resolves providers from the on-chain service metadata of
	/// <paramref name="root"/> through the node API at <paramref name="nodeUrl"/>.
	/// </remarks>
	public AssetMovementClient CreateAssetMovementClient(string nodeUrl, string root, Account account) =>
		AssetMovementClient.WithAccount(this, nodeUrl, root, account);

	/// <summary>
	/// Creates the base client for the node API at <paramref name="nodeUrl"/>.
	/// </summary>
	/// <remarks>
	/// An injected <paramref name="httpClient"/> (for example from
	/// <c>IHttpClientFactory</c>) is borrowed and never disposed. Without one
	/// the client owns its own. Binding <paramref name="network"/> enables
	/// the write path
	/// (<see cref="KeetaClient.Transmit(Crypto.Block, TransmitOptions?, CancellationToken)"/>
	/// and fee blocks). A client without one stays read-only.
	/// </remarks>
	public KeetaClient CreateKeetaClient(string nodeUrl, HttpClient? httpClient = null, long? network = null) =>
		new(this, nodeUrl, httpClient, network);

	/// <summary>
	/// Creates the base client for a well-known <paramref name="network"/>.
	/// </summary>
	/// <remarks>
	/// The client binds the network's default representative set and its
	/// network id, so the write path is enabled. Votes fan out to every
	/// representative. Reads go to the representative with the highest weight.
	/// </remarks>
	public KeetaClient CreateKeetaClient(KeetaNetwork network, HttpClient? httpClient = null) =>
		new(this, network.Representatives(), httpClient, network.Id());

	/// <summary>
	/// Creates a client bound to <paramref name="signer"/>, or a read-only
	/// client when the signer is null.
	/// </summary>
	/// <remarks>
	/// The client operates as <paramref name="account"/> when given, or as
	/// the signer itself otherwise. Both accounts are borrowed and never
	/// disposed. See <see cref="CreateKeetaClient(string, HttpClient?, long?)"/>
	/// for the remaining parameters.
	/// </remarks>
	public UserClient CreateUserClient(
		string nodeUrl,
		Account? signer,
		HttpClient? httpClient = null,
		long? network = null,
		Account? account = null) =>
		new(this, nodeUrl, httpClient, network, signer, account);

	/// <summary>
	/// Creates a signer-bound client for a well-known <paramref name="network"/>.
	/// </summary>
	/// <remarks>See the URL overload for the remaining parameters.</remarks>
	public UserClient CreateUserClient(
		KeetaNetwork network,
		Account? signer,
		HttpClient? httpClient = null,
		Account? account = null) =>
		new(this, CreateKeetaClient(network, httpClient), signer, account);
}
