using System.Numerics;

namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// Creates block builders, operations, and parsed blocks owned by one runtime.
/// Reached through <see cref="WasmRuntime.Blocks"/>. Everything it creates must
/// be disposed before that runtime.
/// </summary>
public sealed class BlockFactory
{
	private readonly WasmRuntime _runtime;

	internal BlockFactory(WasmRuntime runtime) => _runtime = runtime;

	/// <summary>A fresh builder for one signed block.</summary>
	public BlockBuilder NewBuilder() => new(_runtime);

	/// <summary>
	/// A <c>SEND</c> operation transferring <paramref name="amount"/> base units
	/// of <paramref name="token"/> to <paramref name="to"/>, with an optional
	/// <paramref name="external"/> reference.
	/// </summary>
	public BlockOperation Send(Account to, BigInteger amount, Account token, string? external = null)
	{
		string value = amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
		int handle = _runtime.OpSend(to.Handle, value, token.Handle, external ?? "");

		return new BlockOperation(_runtime, handle);
	}

	/// <summary>A <c>SET_REP</c> operation delegating to <paramref name="representative"/>.</summary>
	public BlockOperation SetRep(Account representative)
	{
		int handle = _runtime.OpSetRep(representative.Handle);
		return new BlockOperation(_runtime, handle);
	}

	/// <summary>
	/// A <c>RECEIVE</c> operation crediting <paramref name="amount"/> base units
	/// of <paramref name="token"/> from <paramref name="from"/>. With
	/// <paramref name="exact"/> set the send must match the amount exactly;
	/// <paramref name="forward"/> redirects the funds onward.
	/// </summary>
	public BlockOperation Receive(Account from, BigInteger amount, Account token, bool exact = false, Account? forward = null)
	{
		string value = amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
		int handle = _runtime.OpReceive(from.Handle, value, token.Handle, exact, forward?.Handle ?? 0);

		return new BlockOperation(_runtime, handle);
	}

	/// <summary>
	/// A <c>SET_INFO</c> operation publishing the account's name, description,
	/// and metadata. <paramref name="defaultPermission"/> is required for
	/// identifier accounts.
	/// </summary>
	public BlockOperation SetInfo(string name, string description, string metadata, Permissions? defaultPermission = null)
	{
		int handle = _runtime.OpSetInfo(name, description, metadata, defaultPermission?.Handle ?? 0);
		return new BlockOperation(_runtime, handle);
	}

	/// <summary>
	/// A <c>MODIFY_PERMISSIONS</c> operation applying <paramref name="permissions"/>
	/// to <paramref name="principal"/> with <paramref name="method"/>, optionally
	/// scoped to <paramref name="target"/> (the block account when omitted).
	/// </summary>
	public BlockOperation ModifyPermissions(Account principal, Permissions permissions, AdjustMethod method, Account? target = null)
	{
		int handle = _runtime.OpModifyPermissions(
			principal.Handle, permissions.Handle, CoreNames.Of(method), target?.Handle ?? 0);

		return new BlockOperation(_runtime, handle);
	}

	/// <summary>
	/// A <c>TOKEN_ADMIN_SUPPLY</c> operation adjusting the block token's supply
	/// by <paramref name="amount"/> using <paramref name="method"/>
	/// (<see cref="AdjustMethod.Set"/> is not a valid supply adjustment).
	/// </summary>
	public BlockOperation TokenAdminSupply(BigInteger amount, AdjustMethod method)
	{
		string value = amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
		int handle = _runtime.OpTokenAdminSupply(value, CoreNames.Of(method));

		return new BlockOperation(_runtime, handle);
	}

	/// <summary>A <c>CREATE_IDENTIFIER</c> operation claiming <paramref name="identifier"/>.</summary>
	public BlockOperation CreateIdentifier(Account identifier)
	{
		int handle = _runtime.OpCreateIdentifier(identifier.Handle);
		return new BlockOperation(_runtime, handle);
	}

	/// <summary>
	/// A <c>CREATE_IDENTIFIER</c> operation claiming <paramref name="multisig"/>
	/// as a multisig account governed by <paramref name="signers"/> with the
	/// given signing <paramref name="quorum"/>.
	/// </summary>
	public BlockOperation CreateMultisig(Account multisig, IReadOnlyList<Account> signers, int quorum)
	{
		int handle = _runtime.OpCreateMultisig(multisig.Handle, Handles.Of(signers), quorum);
		return new BlockOperation(_runtime, handle);
	}

	/// <summary>A permission set from base <paramref name="flags"/> and optional external bit <paramref name="externalOffsets"/>.</summary>
	public Permissions PermissionsFromFlags(IReadOnlyList<BaseFlag> flags, byte[]? externalOffsets = null)
	{
		string names = string.Join('\n', flags.Select(CoreNames.Of));
		int handle = _runtime.PermissionsFromFlags(names, externalOffsets ?? Array.Empty<byte>());

		return new Permissions(_runtime, handle);
	}

	/// <summary>A permission set decoded from its <c>[base, external]</c> hex bitmaps, the ACL transport form.</summary>
	public Permissions PermissionsFromBitmaps(string baseBitmap, string externalBitmap)
	{
		int handle = _runtime.PermissionsFromBitmaps(baseBitmap, externalBitmap);
		return new Permissions(_runtime, handle);
	}

	/// <summary>Decode a signed block from its transport hex.</summary>
	public Block ParseHex(string hex)
	{
		int handle = _runtime.BlockFromHex(hex);
		return new Block(_runtime, handle);
	}

	/// <summary>
	/// The base token account for <paramref name="network"/> (the implicit fee
	/// currency), derived exactly as the reference does.
	/// </summary>
	public Account NetworkBaseToken(long network)
	{
		int handle = _runtime.NetworkBaseToken(network);
		return new Account(_runtime, handle);
	}
}
