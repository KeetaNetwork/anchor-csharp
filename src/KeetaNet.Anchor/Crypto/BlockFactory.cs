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
