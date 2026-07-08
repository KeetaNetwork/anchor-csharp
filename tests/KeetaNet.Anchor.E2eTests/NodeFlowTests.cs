using System.Numerics;

using KeetaNet.Anchor.Crypto;
using Xunit;

namespace KeetaNet.Anchor.E2eTests;

/// <summary>
/// The node client's read surface against a live reference node whose ledger
/// the harness mutates on request, the node-rs e2e pattern.
/// </summary>
public sealed class NodeFlowTests
{
	/// <summary>Base token funded to the holder.</summary>
	private const long Funding = 1_000_000;

	[Fact]
	public async Task LedgerReadsRoundTripAgainstTheLiveNode()
	{
		CancellationToken cancellationToken = TestContext.Current.CancellationToken;
		using var harness = NodeHarness.Spawn("node");
		LedgerNode node = LedgerNode.Start(harness);

		using var runtime = WasmRuntime.Load();
		using NodeClient client = runtime.CreateNodeClient(node.Api);
		using Account baseToken = runtime.Accounts.FromAccount(node.BaseToken);

		string version = await client.GetNodeVersion(cancellationToken);
		Assert.NotEmpty(version);

		// An account the ledger has never seen reads back empty: no head, no
		// representative, no balances, and an info envelope with blank fields.
		using Account observer = runtime.Accounts.FromSeed(E2eSeeds.Caller, 0, E2eSeeds.Secp256k1);
		AccountState empty = await client.GetAccountState(observer, cancellationToken);
		Assert.Null(empty.HeadBlock);
		Assert.Null(empty.Representative);
		Assert.NotNull(empty.Info);
		Assert.True(string.IsNullOrEmpty(empty.Info!.Name));
		Assert.True(string.IsNullOrEmpty(empty.Info.Description));
		Assert.True(string.IsNullOrEmpty(empty.Info.Metadata));
		Assert.Null(empty.Info.Supply);
		Assert.Empty(empty.Balances);
		Assert.Empty(await client.GetAccountBalances(observer, cancellationToken));
		Assert.Equal(BigInteger.Zero, await client.GetAccountBalance(observer, baseToken, cancellationToken));

		// The C#-derived holder must be the address the harness funds - the
		// interop anchor proving both sides derive the same account.
		using Account holder = runtime.Accounts.FromSeed(E2eSeeds.Subject, 0, E2eSeeds.Secp256k1);
		string funded = node.Fund(E2eSeeds.Subject, Funding);
		Assert.Equal(holder.Address, funded);

		// Before the holder publishes anything, the balance is the exact
		// funded amount (the sender paid the transfer fee).
		BigInteger initial = await client.GetAccountBalance(holder, baseToken, cancellationToken);
		Assert.Equal(new BigInteger(Funding), initial);

		// Publish info and delegate weight through the reference client, then
		// read both back through the node client's typed state.
		node.SetInfo(E2eSeeds.Subject, "TREASURY", "Primary holder account", "tier-genesis");
		string representative = node.SetRep(E2eSeeds.Subject, E2eSeeds.Recipient);
		using Account expectedRep = runtime.Accounts.FromSeed(E2eSeeds.Recipient, 0, E2eSeeds.Secp256k1);
		Assert.Equal(expectedRep.Address, representative);

		AccountState state = await client.GetAccountState(holder, cancellationToken);
		Assert.NotNull(state.Info);
		Assert.Equal("TREASURY", state.Info!.Name);
		Assert.Equal("Primary holder account", state.Info.Description);
		Assert.Equal("tier-genesis", state.Info.Metadata);
		Assert.Null(state.Info.Supply);
		Assert.NotNull(state.Representative);
		Assert.Equal(expectedRep.Address, state.Representative!.Address);

		// The state's head must be the exact head hash the reference client
		// reports, and the height must reflect the two published blocks.
		Assert.NotNull(state.HeadBlock);
		string? head = node.Head(holder.Address);
		Assert.NotNull(head);
		Assert.Equal(BlockHash.Parse(head!), state.HeadBlock!.Value);
		Assert.True(state.HeadHeight >= BigInteger.One);

		// Fees nibble at the funded amount; the state and the direct balance
		// read must agree on the settled value under the base token.
		TokenBalance settled = Assert.Single(state.Balances);
		Assert.Equal(baseToken.Address, settled.Token.Address);
		Assert.True(settled.Balance > BigInteger.Zero);
		Assert.True(settled.Balance <= new BigInteger(Funding));

		BigInteger direct = await client.GetAccountBalance(holder, baseToken, cancellationToken);
		Assert.Equal(settled.Balance, direct);

		// The token account's own state carries the chain-initialized supply.
		AccountState tokenState = await client.GetAccountState(baseToken, cancellationToken);
		Assert.NotNull(tokenState.Info);
		Assert.NotNull(tokenState.Info!.Supply);
		Assert.True(tokenState.Info.Supply > BigInteger.Zero);

		harness.Shutdown();
	}
}
