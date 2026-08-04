using System.Numerics;

using KeetaNet.Anchor.Crypto;

namespace KeetaNet.Anchor;

/// <summary>
/// The outcome of evaluating an account's published certificates against a trust set.
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
/// <remarks><see cref="Pending"/> is the not-yet-settled amount. It is zero when none is pending.</remarks>
public sealed record TokenBalance(Account Token, BigInteger Balance, BigInteger Pending);

/// <summary>On-chain account info. <see cref="Supply"/> is present only for token accounts.</summary>
public sealed record NodeAccountInfo(string? Name, string? Description, string? Metadata, BigInteger? Supply);

/// <summary>
/// The ledger state of an account.
/// </summary>
/// <remarks>
/// The state carries the head block hash and height, the delegated
/// representative, the published <see cref="NodeAccountInfo"/>, and the token
/// balances. A never-used account reads back with a null head and empty balances.
/// </remarks>
public sealed record AccountState(
	BlockHash? HeadBlock,
	BigInteger? HeadHeight,
	Account? Representative,
	NodeAccountInfo? Info,
	IReadOnlyList<TokenBalance> Balances);

/// <summary>
/// A representative and its on-ledger voting weight.
/// </summary>
/// <remarks>
/// <see cref="ApiUrl"/> is the REST endpoint that the node advertises for the
/// representative. The all-representatives read includes it. The singular
/// lookups do not.
/// </remarks>
public sealed record NodeRepresentative(Account Account, BigInteger Weight, string? ApiUrl);

/// <summary>
/// A non-binding vote quote from one representative.
/// </summary>
/// <remarks>
/// The quote locks in the fee that the issuing representative would charge
/// for the quoted blocks. Attach quotes to a transmit through
/// <see cref="TransmitOptions.Quotes"/>. The vote round returns each quote
/// only to the representative that issued it, matched by
/// <see cref="IssuerApiUrl"/>.
/// </remarks>
public sealed record VoteQuote(byte[] Bytes, string IssuerApiUrl);

/// <summary>
/// A point-in-time XOR checksum over the node's ledger.
/// </summary>
/// <remarks>
/// <see cref="Moment"/> is the approximate time of the measurement.
/// <see cref="MomentRangeMs"/> is half the measurement window, in milliseconds.
/// </remarks>
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
/// The pagination and range bounds for <see cref="KeetaClient.GetChain"/>.
/// </summary>
/// <remarks>
/// <see cref="Start"/> and <see cref="End"/> are block-hash cursors.
/// <see cref="Limit"/> caps the page size, and the node applies its own
/// default and maximum.
/// </remarks>
public sealed record ChainQuery(BlockHash? Start = null, BlockHash? End = null, int? Limit = null);

/// <summary>
/// One page of an account's chain, most recent first, with the cursor for
/// the next page.
/// </summary>
/// <remarks>
/// Pass <see cref="NextKey"/> as the next query's
/// <see cref="ChainQuery.Start"/>. The cursor is null once the chain is
/// exhausted. The caller owns the blocks and must dispose them.
/// </remarks>
public sealed record ChainPage(IReadOnlyList<Block> Blocks, BlockHash? NextKey);

/// <summary>
/// The pagination bounds for <see cref="KeetaClient.GetHistory"/>.
/// </summary>
/// <remarks>
/// <see cref="Start"/> is the previous page's last staple id.
/// <see cref="Limit"/> caps the page size.
/// </remarks>
public sealed record HistoryQuery(BlockHash? Start = null, int? Limit = null);

/// <summary>
/// One committed vote staple in an account's history.
/// </summary>
/// <remarks>
/// The entry carries the staple's transport bytes, its id (the hash over the
/// block hashes it covers), and the moment of the commit.
/// </remarks>
public sealed record NodeHistoryEntry(byte[] StapleBytes, BlockHash? Id, DateTimeOffset? Timestamp);

/// <summary>
/// One page of history with the cursor for the next page.
/// </summary>
/// <remarks>
/// Pass <see cref="NextKey"/> as the next query's
/// <see cref="HistoryQuery.Start"/>. The cursor is null once the history is
/// exhausted.
/// </remarks>
public sealed record HistoryPage(IReadOnlyList<NodeHistoryEntry> Entries, BlockHash? NextKey);

/// <summary>The principal that an ACL entry grants permissions to.</summary>
public abstract record AclPrincipal
{
	private protected AclPrincipal()
	{
	}
}

/// <summary>A concrete account principal.</summary>
public sealed record AclAccountPrincipal(Account Account) : AclPrincipal;

/// <summary>
/// A certificate principal. It matches any account that presents a
/// certificate issued by the certificate with <see cref="Hash"/>, anchored
/// to <see cref="Account"/>.
/// </summary>
public sealed record AclCertificatePrincipal(CertificateHash Hash, Account Account) : AclPrincipal;

/// <summary>
/// An access-control entry that grants <see cref="Principal"/> the
/// <see cref="Granted"/> permissions over <see cref="Target"/>, keyed under
/// <see cref="Entity"/>.
/// </summary>
/// <remarks>
/// The entry carries live accounts and a permission set that the caller must
/// dispose, like every other model carrying handles.
/// </remarks>
public sealed record Acl(AclPrincipal? Principal, Account? Entity, Account? Target, Permissions Granted);
