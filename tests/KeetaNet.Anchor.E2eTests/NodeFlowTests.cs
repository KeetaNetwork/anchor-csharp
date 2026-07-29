using System.Numerics;
using System.Text.Json;

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
		using KeetaClient client = runtime.CreateKeetaClient(node.Api);
		using Account baseToken = runtime.Accounts.FromPublicKeyString(node.BaseToken);

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
		Assert.Equal(holder.PublicKeyString, funded);

		// Before the holder publishes anything, the balance is the exact
		// funded amount (the sender paid the transfer fee).
		BigInteger initial = await client.GetAccountBalance(holder, baseToken, cancellationToken);
		Assert.Equal(new BigInteger(Funding), initial);

		// Publish info and delegate weight through the reference client, then
		// read both back through the node client's typed state.
		node.SetInfo(E2eSeeds.Subject, "TREASURY", "Primary holder account", "tier-genesis");
		string representative = node.SetRep(E2eSeeds.Subject, E2eSeeds.Recipient);
		using Account expectedRep = runtime.Accounts.FromSeed(E2eSeeds.Recipient, 0, E2eSeeds.Secp256k1);
		Assert.Equal(expectedRep.PublicKeyString, representative);

		AccountState state = await client.GetAccountState(holder, cancellationToken);
		Assert.NotNull(state.Info);
		Assert.Equal("TREASURY", state.Info!.Name);
		Assert.Equal("Primary holder account", state.Info.Description);
		Assert.Equal("tier-genesis", state.Info.Metadata);
		Assert.Null(state.Info.Supply);
		Assert.NotNull(state.Representative);
		Assert.Equal(expectedRep.PublicKeyString, state.Representative!.PublicKeyString);

		// The state's head must be the exact head hash the reference client
		// reports, and the height must reflect the two published blocks.
		Assert.NotNull(state.HeadBlock);

		string? head = node.Head(holder.PublicKeyString);
		Assert.NotNull(head);
		Assert.Equal(BlockHash.Parse(head!), state.HeadBlock!.Value);
		Assert.True(state.HeadHeight >= BigInteger.One);

		// Fees deduct from the funded amount, so the state and the direct
		// balance read must agree on the settled value under the base token.
		TokenBalance settled = Assert.Single(state.Balances);
		Assert.Equal(baseToken.PublicKeyString, settled.Token.PublicKeyString);
		Assert.True(settled.Balance > BigInteger.Zero);
		Assert.True(settled.Balance <= new BigInteger(Funding));

		BigInteger direct = await client.GetAccountBalance(holder, baseToken, cancellationToken);
		Assert.Equal(settled.Balance, direct);

		// The token account's own state carries the chain-initialized supply,
		// and the supply convenience serves the same value. A non-token
		// account reports no supply at all.
		AccountState tokenState = await client.GetAccountState(baseToken, cancellationToken);
		Assert.NotNull(tokenState.Info);
		Assert.NotNull(tokenState.Info!.Supply);
		Assert.True(tokenState.Info.Supply > BigInteger.Zero);

		BigInteger? supply = await client.GetTokenSupply(baseToken, cancellationToken);
		Assert.Equal(tokenState.Info.Supply, supply);
		Assert.Null(await client.GetTokenSupply(holder, cancellationToken));

		// The batch read returns one state per account in request order,
		// agreeing with the individual reads.
		IReadOnlyList<AccountState> states = await client.GetAccountStates(new[] { holder, observer }, cancellationToken);
		Assert.Equal(2, states.Count);
		Assert.Equal(state.HeadBlock, states[0].HeadBlock);
		Assert.Equal(settled.Balance, Assert.Single(states[0].Balances).Balance);
		Assert.Null(states[1].HeadBlock);
		Assert.Empty(states[1].Balances);

		await AssertRepresentativeReads(client, node, cancellationToken);
		await AssertNodeDiagnostics(client, cancellationToken);

		// A response from outside the node API surfaces as the stable typed
		// failure, with the transport error preserved as its cause.
		using KeetaClient misRouted = runtime.CreateKeetaClient(node.Api + "/bogus");
		KeetaException failure = await Assert.ThrowsAsync<KeetaException>(
			() => misRouted.GetNodeVersion(cancellationToken));
		Assert.Equal("NODE_STATUS", failure.Code);
		Assert.NotNull(failure.InnerException);

		harness.Shutdown();
	}

	/// <summary>The flat base-token fee the harness chain charges per vote round.</summary>
	private static readonly BigInteger RoundFee = BigInteger.One;

	[Fact]
	public async Task FeeBearingSendTransmitsAgainstTheLiveNode()
	{
		CancellationToken cancellationToken = TestContext.Current.CancellationToken;
		using var harness = NodeHarness.Spawn("node");
		LedgerNode node = LedgerNode.Start(harness);

		using var runtime = WasmRuntime.Load();
		using Account holder = runtime.Accounts.FromSeed(E2eSeeds.Subject, 0, E2eSeeds.Secp256k1);
		using Account recipient = runtime.Accounts.FromSeed(E2eSeeds.Recipient, 0, E2eSeeds.Secp256k1);
		using UserClient user = runtime.CreateUserClient(node.Api, holder, network: node.Network);
		KeetaClient client = user.Client;

		// Both sides must derive the same base token from the network id.
		Assert.NotNull(client.BaseToken);
		Assert.Equal(node.BaseToken, client.BaseToken!.PublicKeyString);
		Account baseToken = client.BaseToken;

		node.Fund(E2eSeeds.Subject, Funding);

		// The send opens the holder's chain and pays the demanded fee from
		// the bound signer by default.
		const long Amount = 12_345;
		Assert.True(await user.Send(recipient, Amount, baseToken, cancellationToken: cancellationToken));

		// The recipient gains exactly the amount; the holder also paid the
		// round's flat fee.
		BigInteger credited = await client.GetAccountBalance(recipient, baseToken, cancellationToken);
		Assert.Equal(new BigInteger(Amount), credited);

		BigInteger remaining = await user.GetBalance(baseToken, cancellationToken);
		Assert.Equal(new BigInteger(Funding) - Amount - RoundFee, remaining);

		// The fee block chained atop the send, so the holder's head advanced
		// past the send block and must match the reference client's.
		AccountState state = await user.GetState(cancellationToken);
		Assert.NotNull(state.HeadBlock);

		string? referenceHead = node.Head(holder.PublicKeyString);
		Assert.NotNull(referenceHead);
		Assert.Equal(BlockHash.Parse(referenceHead!), state.HeadBlock!.Value);

		// The SET_REP chains atop the advanced head and costs one more fee.
		Assert.True(await user.SetRep(recipient, cancellationToken: cancellationToken));

		state = await user.GetState(cancellationToken);
		Assert.Equal(recipient.PublicKeyString, state.Representative!.PublicKeyString);
		remaining -= RoundFee;
		Assert.Equal(remaining, await user.GetBalance(baseToken, cancellationToken));

		// A fee-less transmit against the fee-enforcing node refuses with the
		// typed FEE_REQUIRED before anything is published. The refusal leaves
		// the representative's temporary vote behind, blocking the holder's
		// height for the rest of the test - each refusal rides its own account.
		using (Block feeless = BuildSend(runtime, user, recipient, Amount, state.HeadBlock))
		{
			KeetaException refused = await Assert.ThrowsAsync<KeetaException>(
				() => client.Transmit(feeless, cancellationToken: cancellationToken));
			Assert.Equal("FEE_REQUIRED", refused.Code);
		}

		// A signer-less user client rejects writes outright.
		using UserClient readOnly = runtime.CreateUserClient(node.Api, signer: null, network: node.Network);
		Assert.True(readOnly.IsReadOnly);

		KeetaException unsigned = await Assert.ThrowsAsync<KeetaException>(
			() => readOnly.Send(recipient, 1, baseToken, cancellationToken: cancellationToken));
		Assert.Equal("SIGNER_REQUIRED", unsigned.Code);

		// A client without a bound network cannot originate the fee block the
		// round demands, so its transmit refuses before publishing anything.
		using KeetaClient unbound = runtime.CreateKeetaClient(node.Api);
		Assert.Null(unbound.BaseToken);

		using UserClient recipientUser = runtime.CreateUserClient(node.Api, recipient, network: node.Network);
		using (Block opening = BuildSend(runtime, recipientUser, holder, 1, previous: null))
		{
			KeetaException refused = await Assert.ThrowsAsync<KeetaException>(
				() => unbound.Transmit(opening, TransmitOptions.WithFeeSigner(recipient), cancellationToken));
			Assert.Equal("NETWORK_REQUIRED", refused.Code);
		}

		// No refusal advanced either chain.
		Assert.Equal(remaining, await user.GetBalance(baseToken, cancellationToken));
		Assert.Equal(new BigInteger(Amount), await client.GetAccountBalance(recipient, baseToken, cancellationToken));

		harness.Shutdown();
	}

	/// <summary>
	/// A signed base-token send from <paramref name="user"/>'s operating
	/// account to <paramref name="to"/>, opening the chain when
	/// <paramref name="previous"/> is null.
	/// </summary>
	private static Block BuildSend(
		WasmRuntime runtime,
		UserClient user,
		Account to,
		long amount,
		BlockHash? previous)
	{
		using BlockOperation send = runtime.Blocks.Send(to, amount, user.Client.BaseToken!);
		using BlockBuilder builder = user.InitBuilder();

		if (previous is { } hash)
		{
			builder.WithPrevious(hash);
		}
		else
		{
			builder.AsOpening();
		}

		return builder.AddOperation(send).Build();
	}

	/// <summary>
	/// The three representative reads agree on the chain's one representative:
	/// the node's own, the singular lookup, and the advertised set.
	/// </summary>
	private static async Task AssertRepresentativeReads(KeetaClient client, LedgerNode node, CancellationToken cancellationToken)
	{
		NodeRepresentative own = await client.GetNodeRepresentative(cancellationToken);
		Assert.Equal(node.Representative, own.Account.PublicKeyString);
		Assert.True(own.Weight > BigInteger.Zero);

		NodeRepresentative named = await client.GetRepresentative(own.Account, cancellationToken);
		Assert.Equal(node.Representative, named.Account.PublicKeyString);
		Assert.Equal(own.Weight, named.Weight);

		// Only the plural read advertises the REST endpoint.
		IReadOnlyList<NodeRepresentative> all = await client.GetAllRepresentatives(cancellationToken);
		NodeRepresentative advertised = Assert.Single(all, entry => entry.Account.PublicKeyString == node.Representative);
		Assert.NotNull(advertised.ApiUrl);
		Assert.NotEmpty(advertised.ApiUrl!);
	}

	/// <summary>The diagnostic reads: checksum with a moment, stats and peers as JSON objects.</summary>
	private static async Task AssertNodeDiagnostics(KeetaClient client, CancellationToken cancellationToken)
	{
		LedgerChecksum checksum = await client.GetLedgerChecksum(cancellationToken);
		Assert.NotEqual(BigInteger.Zero, checksum.Checksum);
		Assert.NotNull(checksum.Moment);

		JsonElement stats = await client.GetNodeStats(cancellationToken);
		Assert.Equal(JsonValueKind.Object, stats.ValueKind);

		JsonElement peers = await client.GetNodePeers(cancellationToken);
		Assert.Equal(JsonValueKind.Object, peers.ValueKind);
	}
}
