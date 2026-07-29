namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// A signed, sealed block ready to transmit. Produced by
/// <see cref="BlockBuilder.Build"/> or parsed from transport hex through
/// <see cref="BlockFactory.ParseHex"/>. The block lives inside the wasm core.
/// </summary>
public sealed class Block : WasmObject
{
	internal Block(WasmRuntime runtime, int handle)
		: base(runtime, handle)
	{
	}

	/// <summary>The block hash.</summary>
	public BlockHash Hash => BlockHash.Parse(Runtime.BlockHashHex(Handle));

	/// <summary>The block's raw transport bytes, as a vote request carries them.</summary>
	public byte[] ToBytes() => Runtime.BlockToBytes(Handle);

	/// <summary>The block's transport hex encoding.</summary>
	public string ToHex() => Runtime.BlockToHex(Handle);

	/// <summary>The block's originating account. The caller owns the returned handle.</summary>
	public Account GetAccount() => new(Runtime, Runtime.BlockAccount(Handle));

	private protected override void Release(WasmRuntime runtime, int handle) => runtime.BlockFree(handle);
}

/// <summary>
/// One ledger operation a block carries. Created through
/// <see cref="BlockFactory"/> or handed out by the fee flow; appending it to a
/// builder clones it, so one operation may feed several blocks.
/// </summary>
public sealed class BlockOperation : WasmObject
{
	internal BlockOperation(WasmRuntime runtime, int handle)
		: base(runtime, handle)
	{
	}

	private protected override void Release(WasmRuntime runtime, int handle) => runtime.OpFree(handle);
}

/// <summary>
/// A representative vote decoded from its transport bytes. Produced by the
/// transmit flow and the vote reads on <see cref="KeetaClient"/>.
/// </summary>
public sealed class Vote : WasmObject
{
	internal Vote(WasmRuntime runtime, int handle)
		: base(runtime, handle)
	{
	}

	/// <summary>Whether this vote obliges a fee block (a required, non-optional fee schedule).</summary>
	public bool RequiresFee => Runtime.VoteRequiresFee(Handle);

	private protected override void Release(WasmRuntime runtime, int handle) => runtime.VoteFree(handle);
}

/// <summary>
/// A validated round of blocks and the votes endorsing them. The transmit flow
/// hands one to the fee-block factory so it can read the fee the round owes
/// and the payer's chaining tip.
/// </summary>
public sealed class VoteStaple : WasmObject
{
	internal VoteStaple(WasmRuntime runtime, int handle)
		: base(runtime, handle)
	{
	}

	private protected override void Release(WasmRuntime runtime, int handle) => runtime.VoteStapleFree(handle);
}

/// <summary>The declared purpose of a block.</summary>
public enum BlockPurpose
{
	/// <summary>An ordinary user block.</summary>
	Generic,
	/// <summary>A fee block paying for a vote round.</summary>
	Fee,
}

/// <summary>
/// A fluent builder for one signed block. Each step consumes the core-side
/// builder and rebinds it, so a failed step invalidates the builder. Reached
/// through <see cref="BlockFactory.NewBuilder"/>.
/// </summary>
public sealed class BlockBuilder : IDisposable
{
	private readonly WasmRuntime _runtime;

	/// <summary>The live core builder handle; zero once consumed or disposed.</summary>
	private int _handle;

	internal BlockBuilder(WasmRuntime runtime)
	{
		_runtime = runtime;
		_handle = runtime.BuilderNew();
	}

	/// <summary>Set the block version (the reference builds version 2).</summary>
	public BlockBuilder WithVersion(int version) => Step(handle => _runtime.BuilderWithVersion(handle, version));

	/// <summary>Set the network id the block belongs to.</summary>
	public BlockBuilder WithNetwork(long network) => Step(handle => _runtime.BuilderWithNetwork(handle, network));

	/// <summary>Set the originating account.</summary>
	public BlockBuilder WithAccount(Account account) => Step(handle => _runtime.BuilderWithAccount(handle, account.Handle));

	/// <summary>Set the signing account (distinct from the originator under delegated signing).</summary>
	public BlockBuilder WithSigner(Account signer) => Step(handle => _runtime.BuilderWithSigner(handle, signer.Handle));

	/// <summary>Chain the block atop <paramref name="previous"/>.</summary>
	public BlockBuilder WithPrevious(BlockHash previous) => Step(handle => _runtime.BuilderWithPrevious(handle, previous.ToBytes()));

	/// <summary>Mark the block as an account opening (no previous block).</summary>
	public BlockBuilder AsOpening() => Step(_runtime.BuilderAsOpening);

	/// <summary>Set the block timestamp.</summary>
	public BlockBuilder WithDate(DateTimeOffset date) => Step(handle => _runtime.BuilderWithDate(handle, date.ToUnixTimeMilliseconds()));

	/// <summary>Set the block purpose; unset defaults to <see cref="BlockPurpose.Generic"/>.</summary>
	public BlockBuilder WithPurpose(BlockPurpose purpose) => Step(handle => _runtime.BuilderWithPurpose(handle, PurposeName(purpose)));

	/// <summary>Append <paramref name="operation"/> (cloned; the caller keeps ownership).</summary>
	public BlockBuilder AddOperation(BlockOperation operation) => Step(handle => _runtime.BuilderWithOperation(handle, operation.Handle));

	/// <summary>
	/// Build, validate, and sign the block, consuming the builder.
	/// </summary>
	public Block Build()
	{
		int handle = TakeCurrent();
		int block = _runtime.BuilderSign(handle);

		return new Block(_runtime, block);
	}

	/// <summary>Release the core builder when it was never consumed by <see cref="Build"/>.</summary>
	public void Dispose()
	{
		if (_handle == 0)
		{
			return;
		}

		int handle = _handle;
		_handle = 0;
		_runtime.BuilderFree(handle);
	}

	/// <summary>
	/// Run one consuming builder step. The current handle is cleared before
	/// the call: the core frees it even on failure, so a throwing step must
	/// not leave a stale handle behind for <see cref="Dispose"/> to double-free.
	/// </summary>
	private BlockBuilder Step(Func<int, int> operation)
	{
		int handle = TakeCurrent();
		_handle = operation(handle);

		return this;
	}

	/// <summary>The live handle, cleared so no failure path can reuse it.</summary>
	private int TakeCurrent()
	{
		if (_handle == 0)
		{
			throw new KeetaException("BUILDER_CONSUMED", "the block builder was already consumed or disposed");
		}

		int handle = _handle;
		_handle = 0;

		return handle;
	}

	private static string PurposeName(BlockPurpose purpose)
	{
		if (purpose == BlockPurpose.Fee)
		{
			return "fee";
		}

		return "generic";
	}
}
