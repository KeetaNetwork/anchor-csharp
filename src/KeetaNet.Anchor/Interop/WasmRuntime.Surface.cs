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
}
