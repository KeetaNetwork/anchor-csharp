using System.Numerics;

using KeetaNet.Anchor.Crypto;

namespace KeetaNet.Anchor;

/// <summary>
/// Outcome of evaluating an account's published certificates against a trust set.
/// </summary>
public enum CertificateChainStatus
{
	/// <summary>At least one published record chains to a trusted issuer.</summary>
	Trusted,

	/// <summary>The account published no certificates.</summary>
	NoCerts,

	/// <summary>Certificates exist but none chain to a trusted issuer.</summary>
	Untrusted,
}

/// <summary>An account's balance in one token, in that token's base units.</summary>
/// <remarks><see cref="Pending"/> is the not-yet-settled amount, zero when none.</remarks>
public sealed record TokenBalance(Account Token, BigInteger Balance, BigInteger Pending);

/// <summary>On-chain account info. <see cref="Supply"/> is present only for token accounts.</summary>
public sealed record NodeAccountInfo(string? Name, string? Description, string? Metadata, BigInteger? Supply);

/// <summary>
/// The ledger state of an account: its head block hash and height, delegated
/// representative, published <see cref="NodeAccountInfo"/>, and token balances.
/// A never-used account reads back with null head and empty balances.
/// </summary>
public sealed record AccountState(
	BlockHash? HeadBlock,
	BigInteger? HeadHeight,
	Account? Representative,
	NodeAccountInfo? Info,
	IReadOnlyList<TokenBalance> Balances);

/// <summary>
/// A representative and its on-ledger voting weight. <see cref="ApiUrl"/> is
/// the REST endpoint the node advertises for it: the all-representatives read
/// includes it, the singular lookups do not.
/// </summary>
public sealed record NodeRepresentative(Account Account, BigInteger Weight, string? ApiUrl);

/// <summary>
/// A point-in-time XOR checksum over the node's ledger, with the approximate
/// <see cref="Moment"/> it was taken and half the measurement window
/// (<see cref="MomentRangeMs"/>, milliseconds).
/// </summary>
public sealed record LedgerChecksum(BigInteger Checksum, DateTimeOffset? Moment, double MomentRangeMs);

/// <summary>Which ledger a block lookup searches.</summary>
public enum LedgerSide
{
	/// <summary>The settled main ledger.</summary>
	Main,
	/// <summary>The unsettled side ledger.</summary>
	Side,
}

/// <summary>
/// Pagination/range bounds for <see cref="KeetaClient.GetAccountChain"/>.
/// <see cref="Start"/>/<see cref="End"/> are block-hash cursors;
/// <see cref="Limit"/> caps the page size (the node applies its own default
/// and maximum).
/// </summary>
public sealed record ChainQuery(BlockHash? Start = null, BlockHash? End = null, int? Limit = null);

/// <summary>
/// A single page of an account's chain (most recent first) together with the
/// cursor for the next page: pass <see cref="NextKey"/> as the next query's
/// <see cref="ChainQuery.Start"/>; null once the chain is exhausted. The
/// caller owns the blocks and must dispose them.
/// </summary>
public sealed record ChainPage(IReadOnlyList<Block> Blocks, BlockHash? NextKey);

/// <summary>
/// Pagination bounds for <see cref="KeetaClient.GetAccountHistory"/>.
/// <see cref="Start"/> is the previous page's last staple id;
/// <see cref="Limit"/> caps the page size.
/// </summary>
public sealed record HistoryQuery(BlockHash? Start = null, int? Limit = null);

/// <summary>
/// One committed vote staple in an account's history: its transport bytes,
/// its id (the hash over the block hashes it covers), and the moment it was
/// committed.
/// </summary>
public sealed record NodeHistoryEntry(byte[] StapleBytes, BlockHash? Id, DateTimeOffset? Timestamp);

/// <summary>
/// A single page of history together with the cursor for the next page: pass
/// <see cref="NextKey"/> as the next query's <see cref="HistoryQuery.Start"/>;
/// null once the history is exhausted.
/// </summary>
public sealed record HistoryPage(IReadOnlyList<NodeHistoryEntry> Entries, BlockHash? NextKey);

/// <summary>The principal an ACL entry grants permissions to.</summary>
public abstract record AclPrincipal
{
	private protected AclPrincipal()
	{
	}
}

/// <summary>A concrete account principal.</summary>
public sealed record AclAccountPrincipal(Account Account) : AclPrincipal;

/// <summary>
/// A certificate principal: any account presenting a certificate issued by
/// the certificate with <see cref="Hash"/>, anchored to <see cref="Account"/>.
/// </summary>
public sealed record AclCertificatePrincipal(CertificateHash Hash, Account Account) : AclPrincipal;

/// <summary>
/// An access-control entry granting <see cref="Principal"/> the
/// <see cref="Granted"/> permissions over <see cref="Target"/>, keyed under
/// <see cref="Entity"/>. Carries live accounts and a permission set the
/// caller must dispose, like every other model carrying handles.
/// </summary>
public sealed record Acl(AclPrincipal? Principal, Account? Entity, Account? Target, Permissions Granted);
