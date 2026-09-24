using System.Numerics;
using System.Text.Json;

using KeetaNet.Anchor.Crypto;
using Xunit;

// `Certificate` also names the published-record DTO in `KeetaNet.Anchor`.
using CryptoCertificate = KeetaNet.Anchor.Crypto.Certificate;

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

		string version = await client.GetVersion(cancellationToken);
		Assert.NotEmpty(version);

		// An account the ledger has never seen reads back empty: no head, no
		// representative, no balances, and an info envelope with blank fields.
		using Account observer = runtime.Accounts.FromSeed(E2eSeeds.Caller, 0, E2eSeeds.Secp256k1);
		AccountState empty = await client.GetAccountInfo(observer, cancellationToken);
		Assert.Null(empty.HeadBlock);
		Assert.Null(empty.Representative);
		Assert.NotNull(empty.Info);
		Assert.True(string.IsNullOrEmpty(empty.Info!.Name));
		Assert.True(string.IsNullOrEmpty(empty.Info.Description));
		Assert.True(string.IsNullOrEmpty(empty.Info.Metadata));
		Assert.Null(empty.Info.Supply);
		Assert.Empty(empty.Balances);
		Assert.Empty(await client.GetAllBalances(observer, cancellationToken));
		Assert.Equal(BigInteger.Zero, await client.GetBalance(observer, baseToken, cancellationToken));

		// The C#-derived holder must be the address the harness funds - the
		// interop anchor proving both sides derive the same account.
		using Account holder = runtime.Accounts.FromSeed(E2eSeeds.Subject, 0, E2eSeeds.Secp256k1);
		string funded = node.Fund(E2eSeeds.Subject, Funding);
		Assert.Equal(holder.PublicKeyString, funded);

		// Before the holder publishes anything, the balance is the exact
		// funded amount (the sender paid the transfer fee).
		BigInteger initial = await client.GetBalance(holder, baseToken, cancellationToken);
		Assert.Equal(new BigInteger(Funding), initial);

		// Publish info and delegate weight through the reference client, then
		// read both back through the node client's typed state.
		node.SetInfo(E2eSeeds.Subject, "TREASURY", "Primary holder account", "tier-genesis");
		string representative = node.SetRep(E2eSeeds.Subject, E2eSeeds.Recipient);
		using Account expectedRep = runtime.Accounts.FromSeed(E2eSeeds.Recipient, 0, E2eSeeds.Secp256k1);
		Assert.Equal(expectedRep.PublicKeyString, representative);

		AccountState state = await client.GetAccountInfo(holder, cancellationToken);
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

		BigInteger direct = await client.GetBalance(holder, baseToken, cancellationToken);
		Assert.Equal(settled.Balance, direct);

		// The token account's own state carries the chain-initialized supply,
		// and the supply convenience serves the same value. A non-token
		// account reports no supply at all.
		AccountState tokenState = await client.GetAccountInfo(baseToken, cancellationToken);
		Assert.NotNull(tokenState.Info);
		Assert.NotNull(tokenState.Info!.Supply);
		Assert.True(tokenState.Info.Supply > BigInteger.Zero);

		BigInteger? supply = await client.GetTokenSupply(baseToken, cancellationToken);
		Assert.Equal(tokenState.Info.Supply, supply);
		Assert.Null(await client.GetTokenSupply(holder, cancellationToken));

		// The batch read returns one state per account in request order,
		// agreeing with the individual reads.
		IReadOnlyList<AccountState> states = await client.GetAccountsInfo(new[] { holder, observer }, cancellationToken);
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
			() => misRouted.GetVersion(cancellationToken));
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

		// The recipient gains exactly the amount. The holder also paid the
		// round's flat fee.
		BigInteger credited = await client.GetBalance(recipient, baseToken, cancellationToken);
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
		Assert.Equal(new BigInteger(Amount), await client.GetBalance(recipient, baseToken, cancellationToken));

		harness.Shutdown();
	}

	[Fact]
	public async Task ChangeListenersReactToLedgerMutations()
	{
		CancellationToken cancellationToken = TestContext.Current.CancellationToken;
		using var harness = NodeHarness.Spawn("node");
		LedgerNode node = LedgerNode.Start(harness);

		using var runtime = WasmRuntime.Load();
		using Account holder = runtime.Accounts.FromSeed(E2eSeeds.Subject, 0, E2eSeeds.Secp256k1);
		using Account recipient = runtime.Accounts.FromSeed(E2eSeeds.Recipient, 0, E2eSeeds.Secp256k1);
		using UserClient user = runtime.CreateUserClient(node.Api, holder, network: node.Network);
		Account baseToken = user.Client.BaseToken!;

		node.Fund(E2eSeeds.Subject, Funding);

		// The test node advertises no P2P endpoint, so the fallback poll is
		// the delivery path. A tight frequency keeps the test fast.
		var heads = new System.Collections.Concurrent.ConcurrentQueue<string>();
		using var delivered = new SemaphoreSlim(0);
		var options = new ChangeListenerOptions { FallbackFrequency = TimeSpan.FromMilliseconds(200) };

		using (user.OnChange(
			state =>
			{
				heads.Enqueue(state.HeadBlock?.ToString() ?? "");
				delivered.Release();
			},
			options))
		{
			// The first poll emits the funded state.
			Assert.True(await delivered.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken));

			// A send advances the head, and only that change emits again.
			Assert.True(await user.Send(recipient, 7, baseToken, cancellationToken: cancellationToken));
			Assert.True(await delivered.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken));
		}

		Assert.True(heads.Count >= 2);
		string[] observed = heads.ToArray();
		Assert.NotEqual(observed[0], observed[^1]);

		harness.Shutdown();
	}

	[Fact]
	public async Task ChangeSocketReactsToTheLiveNodeBroadcast()
	{
		CancellationToken cancellationToken = TestContext.Current.CancellationToken;
		using var harness = NodeHarness.Spawn("node");
		LedgerNode node = LedgerNode.Start(harness);

		using var runtime = WasmRuntime.Load();
		using Account holder = runtime.Accounts.FromSeed(E2eSeeds.Subject, 0, E2eSeeds.Secp256k1);
		using Account recipient = runtime.Accounts.FromSeed(E2eSeeds.Recipient, 0, E2eSeeds.Secp256k1);

		// The reference node's real P2P socket serves this client, so the
		// greeting and the staple broadcasts cross implementations.
		var endpoints = new[] { new RepresentativeEndpoint(null, node.Api, node.P2p) };
		using UserClient user = runtime.CreateUserClient(endpoints, holder, network: node.Network);
		Account baseToken = user.Client.BaseToken!;

		node.Fund(E2eSeeds.Subject, Funding);

		// A long fallback keeps the poll out of the test. Every emission
		// below must arrive through the socket.
		var heads = new System.Collections.Concurrent.ConcurrentQueue<string>();
		using var delivered = new SemaphoreSlim(0);
		var options = new ChangeListenerOptions { FallbackFrequency = TimeSpan.FromMinutes(10) };

		using (user.OnChange(
			state =>
			{
				heads.Enqueue(state.HeadBlock?.ToString() ?? "");
				delivered.Release();
			},
			options))
		{
			// The socket needs a moment to connect and greet, or the node
			// broadcasts the first staple before this participant registers.
			await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

			// The node broadcasts each published staple to the greeted
			// socket, and the listener re-reads the account and emits.
			Assert.True(await user.Send(recipient, 7, baseToken, cancellationToken: cancellationToken));
			Assert.True(await delivered.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken));
			Assert.True(await user.Send(recipient, 9, baseToken, cancellationToken: cancellationToken));
			Assert.True(await delivered.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken));
		}

		// Each broadcast delivered a fresh head, and the final head matches
		// the reference client's own view of the chain.
		Assert.True(heads.Count >= 2);
		string[] observed = heads.ToArray();
		Assert.NotEqual(observed[0], observed[^1]);
		Assert.Equal(BlockHash.Parse(node.Head(holder.PublicKeyString)!), BlockHash.Parse(observed[^1]));

		harness.Shutdown();
	}

	[Fact]
	public async Task ChangeSocketDropsOversizedFramesAndReconnects()
	{
		CancellationToken cancellationToken = TestContext.Current.CancellationToken;
		using var harness = NodeHarness.Spawn("node");
		LedgerNode node = LedgerNode.Start(harness);

		await using ScriptedP2pNode p2p = await ScriptedP2pNode.Start();

		using var runtime = WasmRuntime.Load();
		using Account holder = runtime.Accounts.FromSeed(E2eSeeds.Subject, 0, E2eSeeds.Secp256k1);
		using Account recipient = runtime.Accounts.FromSeed(E2eSeeds.Recipient, 0, E2eSeeds.Secp256k1);

		// The scripted endpoint stands in for the P2P socket, so the test
		// can send the hostile frames the reference node never produces.
		var endpoints = new[] { new RepresentativeEndpoint(null, node.Api, p2p.WsUrl) };
		using UserClient user = runtime.CreateUserClient(endpoints, holder, network: node.Network);
		Account baseToken = user.Client.BaseToken!;

		node.Fund(E2eSeeds.Subject, Funding);

		// A long fallback keeps the poll out of the test. Every emission
		// below must arrive through the socket.
		var heads = new System.Collections.Concurrent.ConcurrentQueue<string>();
		using var delivered = new SemaphoreSlim(0);
		var options = new ChangeListenerOptions { FallbackFrequency = TimeSpan.FromMinutes(10) };

		using (user.OnChange(
			state =>
			{
				heads.Enqueue(state.HeadBlock?.ToString() ?? "");
				delivered.Release();
			},
			options))
		{
			// The client greets as a participant filtered to its account.
			ScriptedP2pConnection first = await p2p.NextConnection(cancellationToken);
			JsonElement greeting = first.Greeting.GetProperty("greeting");
			Assert.Equal(0, greeting.GetProperty("kind").GetInt32());
			Assert.Equal(holder.PublicKeyString, greeting.GetProperty("filter").GetString());
			Assert.False(string.IsNullOrEmpty(first.Greeting.GetProperty("id").GetString()));

			// An `add` notification makes the client re-read the account.
			await first.Send("{\"add\":{}}");
			Assert.True(await delivered.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken));

			// Advance the ledger, then trip the oversize guard.
			Assert.True(await user.Send(recipient, 7, baseToken, cancellationToken: cancellationToken));
			try
			{
				await first.Send("{\"pad\":\"" + new string('x', 2 * 1024 * 1024) + "\"}");
			}
			catch (System.Net.WebSockets.WebSocketException)
			{
				// The client aborts mid-frame once the guard trips, so the
				// send may observe the closed connection.
			}

			first.Complete();

			// The client reconnects with backoff and greets again. The next
			// add delivers the advanced head.
			ScriptedP2pConnection second = await p2p.NextConnection(cancellationToken);
			Assert.Equal(
				holder.PublicKeyString,
				second.Greeting.GetProperty("greeting").GetProperty("filter").GetString());

			await second.Send("{\"add\":{}}");
			Assert.True(await delivered.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
			second.Complete();
		}

		Assert.Equal(2, heads.Count);

		string[] observed = heads.ToArray();
		Assert.NotEqual(observed[0], observed[1]);
		Assert.NotEmpty(observed[1]);

		harness.Shutdown();
	}

	[Fact]
	public async Task UpdateRepsRefreshesWeightsFromTheLiveNode()
	{
		CancellationToken cancellationToken = TestContext.Current.CancellationToken;
		using var harness = NodeHarness.Spawn("node");
		LedgerNode node = LedgerNode.Start(harness);

		using var runtime = WasmRuntime.Load();
		using Account holder = runtime.Accounts.FromSeed(E2eSeeds.Subject, 0, E2eSeeds.Secp256k1);
		using Account recipient = runtime.Accounts.FromSeed(E2eSeeds.Recipient, 0, E2eSeeds.Secp256k1);
		using UserClient user = runtime.CreateUserClient(node.Api, holder, network: node.Network);
		KeetaClient client = user.Client;
		Account baseToken = client.BaseToken!;

		node.Fund(E2eSeeds.Subject, Funding);

		// The ledger names the harness node as its sole weighted
		// representative.
		IReadOnlyList<NodeRepresentative> ledger = await client.GetAllRepresentativeInfo(cancellationToken);
		NodeRepresentative sole = Assert.Single(ledger);
		Assert.True(sole.Weight > BigInteger.Zero);

		// The refresh adopts the ledger weights. The discovery variant must
		// not duplicate the node the client already contacts.
		await client.UpdateReps(cancellationToken: cancellationToken);
		await client.UpdateReps(addNewRepresentatives: true, cancellationToken: cancellationToken);

		// The refreshed set still carries the write path end to end.
		Assert.True(await user.Send(recipient, 5, baseToken, cancellationToken: cancellationToken));
		Assert.Equal(new BigInteger(5), await client.GetBalance(recipient, baseToken, cancellationToken));

		harness.Shutdown();
	}

	/// <summary>The one base flag the ACL grant carries.</summary>
	private static readonly BaseFlag[] AccessFlag = { BaseFlag.Access };

	[Fact]
	public async Task ChainHistoryAndAclReadsRoundTripAgainstTheLiveNode()
	{
		CancellationToken cancellationToken = TestContext.Current.CancellationToken;
		using var harness = NodeHarness.Spawn("node");
		LedgerNode node = LedgerNode.Start(harness);

		using var runtime = WasmRuntime.Load();
		using Account holder = runtime.Accounts.FromSeed(E2eSeeds.Subject, 0, E2eSeeds.Secp256k1);
		using Account recipient = runtime.Accounts.FromSeed(E2eSeeds.Recipient, 0, E2eSeeds.Secp256k1);
		using UserClient user = runtime.CreateUserClient(node.Api, holder, network: node.Network);
		KeetaClient client = user.Client;
		Account baseToken = client.BaseToken!;

		node.Fund(E2eSeeds.Subject, Funding);

		// Drive the ledger through the client's own writes: a send opens the
		// chain, SET_INFO publishes metadata, and MODIFY_PERMISSIONS grants
		// the recipient access on the holder's account.
		const long Amount = 500;
		Assert.True(await user.Send(recipient, Amount, baseToken, cancellationToken: cancellationToken));
		Assert.True(await user.SetInfo("HOLDER", "ledger reads fixture", "meta", cancellationToken: cancellationToken));

		using Permissions access = runtime.Blocks.PermissionsFromFlags(AccessFlag);
		Assert.True(await user.UpdatePermissions(recipient, access, cancellationToken: cancellationToken));

		AccountState state = await user.GetState(cancellationToken);
		Assert.Equal("HOLDER", state.Info!.Name);
		Assert.NotNull(state.HeadBlock);

		// The head reads back as a live block originated by the holder, and
		// fetching it by hash yields the identical block. An unknown hash is
		// the node's "none" shape, not a failure.
		using Block? head = await client.GetHeadBlock(holder, cancellationToken);
		Assert.NotNull(head);
		Assert.Equal(state.HeadBlock!.Value, head!.Hash);

		using (Account originator = head.GetAccount())
		{
			Assert.Equal(holder.PublicKeyString, originator.PublicKeyString);
		}

		using Block? byHash = await client.GetBlock(head.Hash, cancellationToken: cancellationToken);
		Assert.Equal(head.Hash, byHash!.Hash);
		Assert.Null(await client.GetBlock(BlockHash.Parse(new string('0', 64)), cancellationToken: cancellationToken));

		// The chain lists most recent first. A limit of one pages with a
		// cursor, and the block behind the head names the head as successor.
		ChainPage newest = await user.GetChain(new ChainQuery(Limit: 1), cancellationToken);
		Assert.Equal(head.Hash, Assert.Single(newest.Blocks).Hash);
		Assert.NotNull(newest.NextKey);

		ChainPage chain = await user.GetChain(cancellationToken: cancellationToken);
		Assert.True(chain.Blocks.Count >= 2);
		Assert.Equal(head.Hash, chain.Blocks[0].Hash);

		using Block? successor = await client.GetSuccessorBlock(chain.Blocks[1].Hash, cancellationToken);
		Assert.Equal(head.Hash, successor!.Hash);

		// Account and global history both carry the committed staples.
		HistoryPage history = await user.GetHistory(cancellationToken: cancellationToken);
		Assert.NotEmpty(history.Entries);
		Assert.All(history.Entries, entry => Assert.NotEmpty(entry.StapleBytes));
		Assert.All(history.Entries, entry => Assert.NotNull(entry.Timestamp));

		HistoryPage global = await client.GetGlobalHistory(cancellationToken: cancellationToken);
		Assert.NotEmpty(global.Entries);

		// The settled head retains its votes. Nothing is pending, and an
		// unknown idempotent key resolves to no block.
		IReadOnlyList<Vote>? votes = await client.GetBlockVotes(head.Hash, cancellationToken: cancellationToken);
		Assert.NotNull(votes);
		Assert.NotEmpty(votes!);
		foreach (Vote vote in votes!)
		{
			vote.Dispose();
		}

		Assert.Null(await user.GetPendingBlock(cancellationToken));
		Assert.Null(await user.GetBlockFromIdempotent(Guid.NewGuid().ToString("N"), cancellationToken: cancellationToken));

		// The grant reads back typed from both directions: the recipient as
		// principal, the holder as entity, carrying the access flag.
		IReadOnlyList<Acl> granted = await client.ListAclsByPrincipal(recipient, cancellationToken);
		Acl grant = Assert.Single(granted);
		AclAccountPrincipal principal = Assert.IsType<AclAccountPrincipal>(grant.Principal);
		Assert.Equal(recipient.PublicKeyString, principal.Account.PublicKeyString);
		Assert.Equal(holder.PublicKeyString, grant.Entity!.PublicKeyString);
		Assert.Contains(BaseFlag.Access, grant.Granted.Flags);

		IReadOnlyList<Acl> byEntity = await client.ListAclsByEntity(holder, cancellationToken);
		Assert.Contains(byEntity, entry => entry.Principal is AclAccountPrincipal account
			&& account.Account.PublicKeyString == recipient.PublicKeyString);

		// Pre-fetched vote quotes ride the transmit's temporary round, each
		// routed back to the representative that issued it.
		using (Block quoted = BuildSend(runtime, user, recipient, Amount, state.HeadBlock))
		{
			IReadOnlyList<VoteQuote> quotes = await user.GetQuotes(new[] { quoted }, cancellationToken);
			VoteQuote quote = Assert.Single(quotes);
			Assert.NotEmpty(quote.Bytes);
			Assert.Equal(node.Api, quote.IssuerApiUrl);

			TransmitOptions options = TransmitOptions.WithFeeSigner(holder);
			options.Quotes.Add(quote);
			Assert.True(await client.Transmit(quoted, options, cancellationToken));
		}

		BigInteger credited = await client.GetBalance(recipient, baseToken, cancellationToken);
		Assert.Equal(new BigInteger(Amount * 2), credited);

		// A builder without a position publishes through the one-call path:
		// the user client positions it on the live head and pays the fee.
		using (BlockOperation send = runtime.Blocks.Send(recipient, Amount, baseToken))
		using (BlockBuilder builder = user.InitBuilder())
		{
			builder.AddOperation(send);
			Assert.True(await user.PublishBuilder(builder, cancellationToken: cancellationToken));
		}

		credited = await client.GetBalance(recipient, baseToken, cancellationToken);
		Assert.Equal(new BigInteger(Amount * 3), credited);

		// The one-call identifier claim derives against the pre-claim head,
		// publishes the CREATE_IDENTIFIER block, and returns the account.
		AccountState beforeClaim = await user.GetState(cancellationToken);
		using Account tokenId = await user.GenerateIdentifier(IdentifierKind.Token, cancellationToken: cancellationToken);
		using Account expectedId = holder.GenerateIdentifier(IdentifierKind.Token, beforeClaim.HeadBlock);
		Assert.Equal(expectedId.PublicKeyString, tokenId.PublicKeyString);

		AccountState afterClaim = await user.GetState(cancellationToken);
		Assert.NotEqual(beforeClaim.HeadBlock, afterClaim.HeadBlock);

		harness.Shutdown();
	}

	[Fact]
	public async Task TokenSetupSupplyAndSendRoundTripAgainstTheLiveNode()
	{
		CancellationToken cancellationToken = TestContext.Current.CancellationToken;
		using var harness = NodeHarness.Spawn("node");
		LedgerNode node = LedgerNode.Start(harness);

		using var runtime = WasmRuntime.Load();
		using Account holder = runtime.Accounts.FromSeed(E2eSeeds.Subject, 0, E2eSeeds.Secp256k1);
		using UserClient user = runtime.CreateUserClient(node.Api, holder, network: node.Network);
		KeetaClient client = user.Client;

		node.Fund(E2eSeeds.Subject, Funding);

		// The one-call claim creates the token identifier under the holder.
		using Account token = await user.GenerateIdentifier(IdentifierKind.Token, cancellationToken: cancellationToken);

		// The setup block opens the token's own chain, signed by the owner:
		// info with a public default permission, then the initial supply.
		string metadata = Convert.ToBase64String("{\"decimalPlaces\":10}"u8.ToArray());
		using Permissions access = runtime.Blocks.PermissionsFromFlags(new[] { BaseFlag.Access });
		using BlockOperation setInfo = runtime.Blocks.SetInfo("TKNA", "Example Token", metadata, access);
		using BlockOperation supply = runtime.Blocks.TokenAdminSupply(50_000, AdjustMethod.Add);

		using BlockBuilder setupBuilder = runtime.Blocks.NewBuilder();
		setupBuilder
			.WithVersion(2)
			.WithNetwork(node.Network)
			.WithAccount(token)
			.WithSigner(holder)
			.WithDate(DateTimeOffset.UtcNow)
			.AsOpening()
			.AddOperation(setInfo)
			.AddOperation(supply);
		using Block setup = setupBuilder.Build();

		// The send distributes part of the fresh supply from the token to the
		// holder, chained atop the setup block.
		using BlockOperation send = runtime.Blocks.Send(holder, 200, token);
		using BlockBuilder sendBuilder = runtime.Blocks.NewBuilder();
		sendBuilder
			.WithVersion(2)
			.WithNetwork(node.Network)
			.WithAccount(token)
			.WithSigner(holder)
			.WithDate(DateTimeOffset.UtcNow)
			.WithPrevious(setup.Hash)
			.AddOperation(send);
		using Block distribute = sendBuilder.Build();

		// Both blocks ride one transmit. The holder pays the demanded fee.
		Assert.True(await client.Transmit(
			new[] { setup, distribute },
			TransmitOptions.WithFeeSigner(holder),
			cancellationToken));

		// The reads confirm the info, the supply, and the distribution.
		AccountState tokenState = await client.GetAccountInfo(token, cancellationToken);
		Assert.Equal("TKNA", tokenState.Info?.Name);
		Assert.Equal("Example Token", tokenState.Info?.Description);
		Assert.Equal(metadata, tokenState.Info?.Metadata);
		Assert.Equal(new BigInteger(50_000), tokenState.Info?.Supply);

		BigInteger distributed = await client.GetBalance(holder, token, cancellationToken);
		Assert.Equal(new BigInteger(200), distributed);

		harness.Shutdown();
	}

	[Fact]
	public async Task CertificateWritesRoundTripAgainstTheLiveNode()
	{
		CancellationToken cancellationToken = TestContext.Current.CancellationToken;
		using var harness = NodeHarness.Spawn("node");
		LedgerNode node = LedgerNode.Start(harness);

		using var runtime = WasmRuntime.Load();
		using Account holder = runtime.Accounts.FromSeed(E2eSeeds.Subject, 0, E2eSeeds.Secp256k1);
		using UserClient user = runtime.CreateUserClient(node.Api, holder, network: node.Network);

		node.Fund(E2eSeeds.Subject, Funding);

		// The TypeScript harness issues the chain for the holder because the
		// node's graph check demands CA extensions that only its builder emits.
		IssuedChain issued = node.IssueChain(E2eSeeds.Subject);
		using CryptoCertificate leaf = runtime.Certificates.Parse(issued.Leaf);
		using CryptoCertificate authority = runtime.Certificates.Parse(issued.Ca);
		Assert.Equal(CertificateHash.Parse(issued.LeafHash), leaf.Hash);

		// The add publishes the leaf with the authority recorded as its
		// bundle, and the account's certificate reads serve both back.
		Assert.True(await user.ModifyCertificate(
			AdjustMethod.Add, leaf, new[] { authority }, cancellationToken: cancellationToken));

		IReadOnlyList<Certificate> published = await user.GetAllCertificates(cancellationToken);
		Certificate record = Assert.Single(published);
		using (CryptoCertificate readBack = runtime.Certificates.Parse(record.Value))
		{
			Assert.Equal(leaf.Hash, readBack.Hash);
		}

		Assert.Single(record.Intermediates);
		Assert.NotNull(await user.GetCertificateByHash(leaf.Hash, cancellationToken));

		// The subtract retires the leaf by its hash. The reads empty out.
		Assert.True(await user.ModifyCertificate(
			AdjustMethod.Subtract, leaf, cancellationToken: cancellationToken));

		Assert.Empty(await user.GetAllCertificates(cancellationToken));
		Assert.Null(await user.GetCertificateByHash(leaf.Hash, cancellationToken));

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
		NodeRepresentative own = await client.GetRepresentativeInfo(cancellationToken: cancellationToken);
		Assert.Equal(node.Representative, own.Account.PublicKeyString);
		Assert.True(own.Weight > BigInteger.Zero);

		NodeRepresentative named = await client.GetRepresentativeInfo(own.Account, cancellationToken);
		Assert.Equal(node.Representative, named.Account.PublicKeyString);
		Assert.Equal(own.Weight, named.Weight);

		// Only the plural read advertises the REST endpoint.
		IReadOnlyList<NodeRepresentative> all = await client.GetAllRepresentativeInfo(cancellationToken);
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

		JsonElement peers = await client.GetPeers(cancellationToken);
		Assert.Equal(JsonValueKind.Object, peers.ValueKind);
	}
}
