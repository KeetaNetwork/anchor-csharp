namespace KeetaNet.Anchor;

/// <summary>
/// Produces the fee block a vote round requires: the transmit flow calls the
/// factory with the temporary round's staple so it can read the fee owed and
/// the payer's chaining tip. Return null to decline, which fails the transmit
/// with <c>FEE_REQUIRED</c> before anything is published.
/// </summary>
public delegate Task<Crypto.Block?> GenerateFeeBlock(
	NodeClient client,
	Crypto.VoteStaple staple,
	IReadOnlyList<Crypto.Account> feeTokenPriority,
	CancellationToken cancellationToken);

/// <summary>
/// How a transmit pays the fee its vote round may require. The default pays
/// none: a vote requiring one fails with <c>FEE_REQUIRED</c>.
/// </summary>
public sealed class TransmitOptions
{
	/// <summary>
	/// Token accounts to prefer, in order, when the votes offer a fee choice.
	/// The base token is always an implicit last resort.
	/// </summary>
	public IList<Crypto.Account> FeeTokenPriority { get; } = new List<Crypto.Account>();

	/// <summary>The fee-block factory, or null to pay no fee.</summary>
	public GenerateFeeBlock? FeeBlockFactory { get; set; }

	/// <summary>
	/// Pay any required fee from <paramref name="signer"/>'s own balance,
	/// signed by itself - the common case.
	/// </summary>
	public static TransmitOptions WithFeeSigner(Crypto.Account signer) => WithFeeBlockFrom(signer, signer);

	/// <summary>
	/// Pay any required fee from <paramref name="account"/>'s balance with
	/// <paramref name="signer"/> signing (delegated signing, e.g. a storage
	/// account whose owner signs).
	/// </summary>
	public static TransmitOptions WithFeeBlockFrom(Crypto.Account account, Crypto.Account signer) =>
		new()
		{
			FeeBlockFactory = (client, staple, priority, cancellationToken) =>
				client.BuildFeeBlock(staple, account, signer, priority, cancellationToken),
		};
}
