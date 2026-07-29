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

	internal byte[] BlockToBytes(int handle) => BytesOf("keeta_block_to_bytes", handle);

	internal void BlockFree(int handle) => RunFree("keeta_block_free", handle);

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
