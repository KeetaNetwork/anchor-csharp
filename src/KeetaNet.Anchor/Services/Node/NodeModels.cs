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
