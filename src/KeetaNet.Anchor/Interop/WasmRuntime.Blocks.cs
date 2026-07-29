using System.Buffers.Binary;

namespace KeetaNet.Anchor;

/// <summary>
/// The block surface of the P1 core module: builders, operations, signed
/// blocks, votes, and vote staples - the guest side of the fee-block and
/// transmit flow. Every internal entry point dispatches onto the runtime's
/// owner thread.
/// </summary>
public sealed partial class WasmRuntime
{
	internal int BlockFromHex(string hex) => ParseText("keeta_block_from_hex", hex);

	internal string BlockHashHex(int handle) => TextOf("keeta_block_hash", handle);

	internal string BlockToHex(int handle) => TextOf("keeta_block_to_hex", handle);

	internal byte[] BlockToBytes(int handle) => BytesOf("keeta_block_to_bytes", handle);

	internal int BlockAccount(int handle) =>
		Run(() => TakeHandle(Invoke<int, int>("keeta_block_account", handle)));

	internal void BlockFree(int handle) => RunFree("keeta_block_free", handle);

	internal int PermissionsFromFlags(string flagsJoined, byte[] offsets) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument flags = arguments.Write(flagsJoined);
			Argument external = arguments.WriteBytes(offsets);

			int result = Invoke<int, int, int, int, int>(
				"keeta_permissions_from_flags", flags.Pointer, flags.Length, external.Pointer, external.Length);
			return TakeHandle(result);
		});

	internal int PermissionsFromBitmaps(string baseHex, string externalHex) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument baseMap = arguments.Write(baseHex);
			Argument externalMap = arguments.Write(externalHex);

			int result = Invoke<int, int, int, int, int>(
				"keeta_permissions_from_bitmaps", baseMap.Pointer, baseMap.Length, externalMap.Pointer, externalMap.Length);
			return TakeHandle(result);
		});

	internal string PermissionsFlags(int handle) => TextOf("keeta_permissions_flags", handle);

	internal byte[] PermissionsOffsets(int handle) => BytesOf("keeta_permissions_offsets", handle);

	internal string PermissionsBitmaps(int handle) => TextOf("keeta_permissions_bitmaps", handle);

	internal void PermissionsFree(int handle) => RunFree("keeta_permissions_free", handle);

	internal int GenerateIdentifier(int account, string kind, byte[] previous, int index) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument name = arguments.Write(kind);
			Argument previousHash = arguments.WriteBytes(previous);

			int result = Invoke<int, int, int, int, int, int, int>(
				"keeta_generate_identifier",
				account, name.Pointer, name.Length, previousHash.Pointer, previousHash.Length, index);
			return TakeHandle(result);
		});

	internal int OpSetRep(int to) =>
		Run(() =>
		{
			int result = Invoke<int, int>("keeta_op_set_rep", to);
			return TakeHandle(result);
		});

	internal int OpSend(int to, string amount, int token, string external) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument value = arguments.Write(amount);
			Argument reference = arguments.Write(external);

			int result = Invoke<int, int, int, int, int, int, int>(
				"keeta_op_send", to, value.Pointer, value.Length, token, reference.Pointer, reference.Length);
			return TakeHandle(result);
		});

	internal int OpReceive(int from, string amount, int token, bool exact, int forward) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument value = arguments.Write(amount);

			int result = Invoke<int, int, int, int, int, int, int>(
				"keeta_op_receive", from, value.Pointer, value.Length, token, exact ? 1 : 0, forward);
			return TakeHandle(result);
		});

	internal int OpSetInfo(string name, string description, string metadata, int permissions) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument accountName = arguments.Write(name);
			Argument accountDescription = arguments.Write(description);
			Argument accountMetadata = arguments.Write(metadata);

			int result = Invoke<int, int, int, int, int, int, int, int>(
				"keeta_op_set_info",
				accountName.Pointer, accountName.Length,
				accountDescription.Pointer, accountDescription.Length,
				accountMetadata.Pointer, accountMetadata.Length,
				permissions);
			return TakeHandle(result);
		});

	internal int OpModifyPermissions(int principal, int permissions, string method, int target) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument adjust = arguments.Write(method);

			int result = Invoke<int, int, int, int, int, int>(
				"keeta_op_modify_permissions", principal, permissions, adjust.Pointer, adjust.Length, target);
			return TakeHandle(result);
		});

	internal int OpTokenAdminSupply(string amount, string method) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument value = arguments.Write(amount);
			Argument adjust = arguments.Write(method);

			int result = Invoke<int, int, int, int, int>(
				"keeta_op_token_admin_supply", value.Pointer, value.Length, adjust.Pointer, adjust.Length);
			return TakeHandle(result);
		});

	internal int OpCreateIdentifier(int identifier) =>
		Run(() => TakeHandle(Invoke<int, int>("keeta_op_create_identifier", identifier)));

	internal int OpCreateMultisig(int multisig, int[] signers, int quorum) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument signerList = arguments.WriteHandles(signers);

			int result = Invoke<int, int, int, int, int>(
				"keeta_op_create_multisig", multisig, signerList.Pointer, signerList.Length, quorum);
			return TakeHandle(result);
		});

	internal void OpFree(int handle) => RunFree("keeta_op_free", handle);

	internal int BuilderNew() =>
		Run(() =>
		{
			int result = Invoke<int>("keeta_builder_new");
			return TakeHandle(result);
		});

	internal int BuilderWithVersion(int handle, int version) =>
		Run(() => TakeHandle(Invoke<int, int, int>("keeta_builder_with_version", handle, version)));

	internal int BuilderWithNetwork(int handle, long network) =>
		Run(() => TakeHandle(Invoke<int, long, int>("keeta_builder_with_network", handle, network)));

	internal int BuilderWithAccount(int handle, int account) =>
		Run(() => TakeHandle(Invoke<int, int, int>("keeta_builder_with_account", handle, account)));

	internal int BuilderWithSigner(int handle, int signer) =>
		Run(() => TakeHandle(Invoke<int, int, int>("keeta_builder_with_signer", handle, signer)));

	internal int BuilderWithPrevious(int handle, byte[] previous) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument hash = arguments.WriteBytes(previous);

			int result = Invoke<int, int, int, int>("keeta_builder_with_previous", handle, hash.Pointer, hash.Length);
			return TakeHandle(result);
		});

	internal int BuilderAsOpening(int handle) =>
		Run(() => TakeHandle(Invoke<int, int>("keeta_builder_as_opening", handle)));

	internal int BuilderWithDate(int handle, long unixMillis) =>
		Run(() => TakeHandle(Invoke<int, long, int>("keeta_builder_with_date", handle, unixMillis)));

	internal int BuilderWithPurpose(int handle, string purpose) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument name = arguments.Write(purpose);

			int result = Invoke<int, int, int, int>("keeta_builder_with_purpose", handle, name.Pointer, name.Length);
			return TakeHandle(result);
		});

	internal int BuilderWithOperation(int handle, int operation) =>
		Run(() => TakeHandle(Invoke<int, int, int>("keeta_builder_with_operation", handle, operation)));

	/// <summary>
	/// Build, validate, and sign the block in one dispatch, consuming the
	/// builder. The intermediate unsigned handle never crosses the boundary,
	/// so a signing failure cannot leak it.
	/// </summary>
	internal int BuilderSign(int handle) =>
		Run(() =>
		{
			int unsigned = TakeHandle(Invoke<int, int>("keeta_builder_build", handle));
			return TakeHandle(Invoke<int, int>("keeta_unsigned_sign", unsigned));
		});

	internal void BuilderFree(int handle) => RunFree("keeta_builder_free", handle);

	internal int VoteFromBytes(byte[] bytes) => ParseBytes("keeta_vote_from_bytes", bytes);

	internal void VoteFree(int handle) => RunFree("keeta_vote_free", handle);

	internal bool VoteRequiresFee(int handle) =>
		Run(() => Invoke<int, int>("keeta_fees_required", handle) != 0);

	internal int VoteStapleNew(int[] blocks, int[] votes, long momentMillis) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument blockList = arguments.WriteHandles(blocks);
			Argument voteList = arguments.WriteHandles(votes);

			int result = Invoke<int, int, int, int, long, int>(
				"keeta_vote_staple_new",
				blockList.Pointer, blockList.Length,
				voteList.Pointer, voteList.Length,
				momentMillis);
			return TakeHandle(result);
		});

	internal byte[] VoteStapleBuild(int[] blocks, int[] votes, long momentMillis) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument blockList = arguments.WriteHandles(blocks);
			Argument voteList = arguments.WriteHandles(votes);

			int result = Invoke<int, int, int, int, long, int>(
				"keeta_vote_staple_build",
				blockList.Pointer, blockList.Length,
				voteList.Pointer, voteList.Length,
				momentMillis);
			return TakeBytes(result);
		});

	internal void VoteStapleFree(int handle) => RunFree("keeta_vote_staple_free", handle);

	/// <summary>
	/// The fee-paying operation handles the staple's votes require. Zero from
	/// the core means no fee is owed, decoded here as an empty list.
	/// </summary>
	internal int[] StapleFeeSends(int staple, int baseToken, int[] priority) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument tokens = arguments.WriteHandles(priority);

			int result = Invoke<int, int, int, int, int>(
				"keeta_staple_fee_sends", staple, baseToken, tokens.Pointer, tokens.Length);
			if (result == 0)
			{
				return Array.Empty<int>();
			}

			byte[] encoded = ReadAndFreeBytes(result);
			int[] handles = new int[encoded.Length / sizeof(int)];
			for (int index = 0; index < handles.Length; index++)
			{
				handles[index] = BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(index * sizeof(int)));
			}

			return handles;
		});

	/// <summary>
	/// The hex hash of the payer's last block in the staple, or null when the
	/// payer has no block in the round.
	/// </summary>
	internal string? StapleTipFor(int staple, int payer) =>
		Run(() =>
		{
			int result = Invoke<int, int, int>("keeta_staple_tip_for", staple, payer);
			if (result == 0)
			{
				return null;
			}

			return Text(result);
		});

	internal int NetworkBaseToken(long network) =>
		Run(() =>
		{
			int result = Invoke<long, int>("keeta_base_token", network);
			return TakeHandle(result);
		});
}
