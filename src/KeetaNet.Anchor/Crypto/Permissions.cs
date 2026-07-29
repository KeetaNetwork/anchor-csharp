namespace KeetaNet.Anchor.Crypto;

/// <summary>How a permission or supply adjustment is applied.</summary>
public enum AdjustMethod
{
	/// <summary>Grant on top of the existing set, or mint supply.</summary>
	Add,
	/// <summary>Revoke from the existing set, or burn supply.</summary>
	Subtract,
	/// <summary>Replace the existing set outright.</summary>
	Set,
}

/// <summary>The kind of identifier account an account can derive.</summary>
public enum IdentifierKind
{
	/// <summary>A network identifier.</summary>
	Network,
	/// <summary>A token identifier.</summary>
	Token,
	/// <summary>A storage identifier.</summary>
	Storage,
}

/// <summary>
/// A named base permission bit, matching the reference ledger's offsets.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
	"Naming",
	"CA1711:Identifiers should not have incorrect suffix",
	Justification = "BaseFlag is the reference implementations' name for this type")]
public enum BaseFlag
{
	/// <summary>Account has access.</summary>
	Access = 0,
	/// <summary>Account is an owner.</summary>
	Owner = 1,
	/// <summary>Account is an administrator.</summary>
	Admin = 2,
	/// <summary>Account can update info.</summary>
	UpdateInfo = 3,
	/// <summary>Account can send on behalf of the entity.</summary>
	SendOnBehalf = 4,
	/// <summary>Account can create tokens.</summary>
	TokenAdminCreate = 5,
	/// <summary>Account can modify token supply.</summary>
	TokenAdminSupply = 6,
	/// <summary>Account can modify token balances.</summary>
	TokenAdminModifyBalance = 7,
	/// <summary>Account can create storage accounts.</summary>
	StorageCreate = 8,
	/// <summary>Storage account can hold the principal token.</summary>
	StorageCanHold = 9,
	/// <summary>Account can deposit into the storage account.</summary>
	StorageDeposit = 10,
	/// <summary>Account can delegate permission additions.</summary>
	PermissionDelegateAdd = 11,
	/// <summary>Account can delegate permission removals.</summary>
	PermissionDelegateRemove = 12,
	/// <summary>Account can manage certificates.</summary>
	ManageCertificate = 13,
	/// <summary>Account is a multisig signer.</summary>
	MultisigSigner = 14,
}

/// <summary>
/// A ledger permission set: named base flags plus external bit offsets.
/// Created through <see cref="BlockFactory.PermissionsFromFlags"/> or decoded
/// from its <c>[base, external]</c> bitmaps via
/// <see cref="BlockFactory.PermissionsFromBitmaps"/>.
/// </summary>
public sealed class Permissions : WasmObject
{
	internal Permissions(WasmRuntime runtime, int handle)
		: base(runtime, handle)
	{
	}

	/// <summary>The granted base flags, in ledger offset order.</summary>
	public IReadOnlyList<BaseFlag> Flags =>
		Lines(Runtime.PermissionsFlags(Handle)).Select(CoreNames.FlagOf).ToArray();

	/// <summary>The external permission bit offsets.</summary>
	public byte[] ExternalOffsets => Runtime.PermissionsOffsets(Handle);

	/// <summary>The <c>[base, external]</c> bitmaps as 0x-prefixed hex, the ACL transport form.</summary>
	public IReadOnlyList<string> Bitmaps => Lines(Runtime.PermissionsBitmaps(Handle));

	private protected override void Release(WasmRuntime runtime, int handle) => runtime.PermissionsFree(handle);

	private static string[] Lines(string joined) =>
		joined.Length == 0 ? Array.Empty<string>() : joined.Split('\n');
}

/// <summary>Transport names for the adjust, identifier, and flag enums.</summary>
internal static class CoreNames
{
	/// <summary>The base flag wire names, indexed by ledger offset.</summary>
	private static readonly string[] FlagNames =
	{
		"access",
		"owner",
		"admin",
		"update_info",
		"send_on_behalf",
		"token_admin_create",
		"token_admin_supply",
		"token_admin_modify_balance",
		"storage_create",
		"storage_can_hold",
		"storage_deposit",
		"permission_delegate_add",
		"permission_delegate_remove",
		"manage_certificate",
		"multisig_signer",
	};

	public static string Of(AdjustMethod method) =>
		method switch
		{
			AdjustMethod.Add => "add",
			AdjustMethod.Subtract => "subtract",
			_ => "set",
		};

	public static string Of(IdentifierKind kind) =>
		kind switch
		{
			IdentifierKind.Network => "network",
			IdentifierKind.Token => "token",
			_ => "storage",
		};

	public static string Of(BaseFlag flag) => FlagNames[(int)flag];

	public static BaseFlag FlagOf(string name)
	{
		int offset = Array.IndexOf(FlagNames, name);
		if (offset < 0)
		{
			throw new KeetaException("UNKNOWN", $"unknown base permission flag: {name}");
		}

		return (BaseFlag)offset;
	}
}
