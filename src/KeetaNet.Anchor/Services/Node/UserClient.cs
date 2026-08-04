using System.Globalization;
using System.Net.WebSockets;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace KeetaNet.Anchor;

/// <summary>
/// A <see cref="KeetaClient"/> bound to an operating account.
/// </summary>
/// <remarks>
/// Reads imply the account. Writes originate from it, and the bound signer
/// signs them and pays any required fee by default. Without a signer the
/// client is read-only and writes throw <c>SIGNER_REQUIRED</c>.
/// </remarks>
public sealed class UserClient : IDisposable
{
	private readonly WasmRuntime _runtime;

	[System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP008:Don't assign member with injected and created disposables",
		Justification = "Both constructors transfer ownership of the client to this instance; Dispose releases it.")]
	private readonly KeetaClient _client;

	/// <summary>The operating account when it differs from the signer.</summary>
	private readonly Crypto.Account? _account;

	private readonly Crypto.Account? _signer;

	/// <summary>The registered change handlers, keyed by subscription. The map is its own lock.</summary>
	private readonly Dictionary<Guid, Action<AccountState>> _changeHandlers = new();

	/// <summary>Serializes change detection so that the socket and the poll never race.</summary>
	private readonly SemaphoreSlim _changeGate = new(1, 1);

	/// <summary>The fallback poll. It runs while any change handler is registered.</summary>
	private Timer? _changeTimer;

	/// <summary>Cancels the WebSocket loop and any in-flight change detection.</summary>
	private CancellationTokenSource? _changeCancellation;

	/// <summary>The fingerprint of the last emitted account state.</summary>
	private string? _previousChangeFingerprint;

	/// <summary>
	/// Creates an owned <see cref="KeetaClient"/> for <paramref name="nodeUrl"/>
	/// bound to <paramref name="signer"/>.
	/// </summary>
	/// <remarks>
	/// The client operates as <paramref name="account"/> when given, or as
	/// the signer itself otherwise. Both accounts are borrowed and never disposed.
	/// </remarks>
	internal UserClient(
		WasmRuntime runtime,
		string nodeUrl,
		HttpClient? http,
		long? network,
		Crypto.Account? signer,
		Crypto.Account? account)
		: this(runtime, new KeetaClient(runtime, nodeUrl, http, network), signer, account)
	{
	}

	/// <summary>
	/// Adopts <paramref name="client"/> bound to <paramref name="signer"/>.
	/// </summary>
	/// <remarks>
	/// This instance owns the adopted client and disposes it. The client
	/// operates as <paramref name="account"/> when given, or as the signer
	/// itself otherwise. Both accounts are borrowed and never disposed.
	/// </remarks>
	internal UserClient(
		WasmRuntime runtime,
		KeetaClient client,
		Crypto.Account? signer,
		Crypto.Account? account)
	{
		_runtime = runtime;
		_client = client;
		_signer = signer;
		_account = account;
	}

	/// <summary>The underlying client for reads beyond the operating account.</summary>
	public KeetaClient Client => _client;

	/// <summary>The bound signer, if any.</summary>
	public Crypto.Account? Signer => _signer;

	/// <summary>Whether this client has no signer and therefore rejects writes.</summary>
	public bool IsReadOnly => _signer is null;

	/// <summary>
	/// The operating account. This is the configured account, or the signer
	/// when none is configured.
	/// </summary>
	/// <exception cref="KeetaException"><c>SIGNER_REQUIRED</c> when neither is bound.</exception>
	public Crypto.Account Account =>
		_account
		?? _signer
		?? throw new KeetaException("SIGNER_REQUIRED", "bind a signer or an operating account to the user client");

	/// <summary>Gets the full state of the operating account.</summary>
	public Task<AccountState> State(CancellationToken cancellationToken = default) =>
		_client.GetAccountInfo(Account, cancellationToken);

	/// <summary>Gets the settled balance of <paramref name="token"/> held by the operating account.</summary>
	public Task<BigInteger> Balance(Crypto.Account token, CancellationToken cancellationToken = default) =>
		_client.GetBalance(Account, token, cancellationToken);

	/// <summary>Gets every token balance held by the operating account.</summary>
	public Task<IReadOnlyList<TokenBalance>> AllBalances(CancellationToken cancellationToken = default) =>
		_client.GetAllBalances(Account, cancellationToken);

	/// <summary>Gets the certificates published by the operating account.</summary>
	public Task<IReadOnlyList<Certificate>> GetCertificates(CancellationToken cancellationToken = default) =>
		_client.GetAllCertificates(Account, cancellationToken);

	/// <summary>
	/// Gets the certificate that the operating account published under
	/// <paramref name="certificateHash"/>.
	/// </summary>
	/// <returns>The record, or null when the account never published it.</returns>
	public Task<Certificate?> GetCertificates(
		Crypto.CertificateHash certificateHash,
		CancellationToken cancellationToken = default) =>
		_client.GetCertificateByHash(Account, certificateHash, cancellationToken);

	/// <summary>Gets the hash of the operating account's head block, or null for a fresh account.</summary>
	public async Task<Crypto.BlockHash?> Head(CancellationToken cancellationToken = default)
	{
		using Crypto.Block? head = await _client.GetHeadBlock(Account, cancellationToken).ConfigureAwait(false);
		return head?.Hash;
	}

	/// <summary>Gets the next pending (unreceived) block for the operating account, if any.</summary>
	public Task<Crypto.Block?> PendingBlock(CancellationToken cancellationToken = default) =>
		_client.GetPendingBlock(Account, cancellationToken);

	/// <summary>
	/// Gets the block that the operating account produced for the idempotent
	/// <paramref name="key"/>, if any.
	/// </summary>
	public Task<Crypto.Block?> GetBlockFromIdempotent(
		string key,
		LedgerSide? side = null,
		CancellationToken cancellationToken = default) =>
		_client.GetBlockFromIdempotent(Account, key, side, cancellationToken);

	/// <summary>Gets one page of the operating account's block chain, most recent first.</summary>
	public Task<ChainPage> Chain(ChainQuery? query = null, CancellationToken cancellationToken = default) =>
		_client.GetChain(Account, query, cancellationToken);

	/// <summary>Gets one page of the operating account's committed staple history.</summary>
	public Task<HistoryPage> History(HistoryQuery? query = null, CancellationToken cancellationToken = default) =>
		_client.GetHistory(Account, query, cancellationToken);

	/// <summary>Lists the ACL entries where the operating account is the principal.</summary>
	public Task<IReadOnlyList<Acl>> ListAclsByPrincipal(CancellationToken cancellationToken = default) =>
		_client.ListAclsByPrincipal(Account, cancellationToken);

	/// <summary>Lists the ACL entries granted to the operating account as an entity.</summary>
	public Task<IReadOnlyList<Acl>> ListAclsByEntity(CancellationToken cancellationToken = default) =>
		_client.ListAclsByEntity(Account, cancellationToken);

	/// <summary>
	/// Requests non-binding vote quotes for <paramref name="blocks"/> from
	/// every representative.
	/// </summary>
	/// <remarks>Attach the quotes to a transmit through <see cref="TransmitOptions.Quotes"/>.</remarks>
	public Task<IReadOnlyList<VoteQuote>> GetQuotes(
		IReadOnlyList<Crypto.Block> blocks,
		CancellationToken cancellationToken = default) =>
		_client.GetVoteQuotes(blocks, cancellationToken);

	/// <summary>
	/// Creates a builder for the operating account, signed by the bound
	/// signer and pre-set with the client's defaults.
	/// </summary>
	/// <remarks>
	/// The caller positions the builder, appends operations, and builds. The
	/// method requires a signer and a bound network.
	/// </remarks>
	public Crypto.BlockBuilder InitBuilder() => _client.InitBuilder(Account, RequireSigner());

	/// <summary>
	/// Publishes one signed block. The bound signer pays any required fee
	/// unless <paramref name="options"/> carries a fee-block factory.
	/// </summary>
	public Task<bool> Transmit(
		Crypto.Block block,
		TransmitOptions? options = null,
		CancellationToken cancellationToken = default) =>
		Transmit(new[] { block }, options, cancellationToken);

	/// <summary>
	/// Publishes <paramref name="blocks"/> as one atomic staple. The bound
	/// signer pays any required fee unless <paramref name="options"/> carries
	/// a fee-block factory.
	/// </summary>
	public Task<bool> Transmit(
		IReadOnlyList<Crypto.Block> blocks,
		TransmitOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		_ = RequireSigner();

		return _client.Transmit(blocks, OrDefaultFeePayer(options), cancellationToken);
	}

	/// <summary>
	/// Positions <paramref name="builder"/> atop the operating account's
	/// ledger head, builds its block, and transmits it.
	/// </summary>
	/// <remarks>
	/// A fresh account opens a new chain. The builder must not carry a
	/// position of its own.
	/// </remarks>
	public async Task<bool> PublishBuilder(
		Crypto.BlockBuilder builder,
		TransmitOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		_ = RequireSigner();

		TransmitOptions resolved = OrDefaultFeePayer(options);
		AccountState state = await State(cancellationToken).ConfigureAwait(false);

		KeetaClient.PositionAfter(builder, state.HeadBlock?.ToString());
		using Crypto.Block block = builder.Build();

		return await _client.Transmit(block, resolved, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Creates a <paramref name="kind"/> identifier under the operating
	/// account and publishes the creating block.
	/// </summary>
	/// <returns>The derived account. The caller owns it.</returns>
	public async Task<Crypto.Account> GenerateIdentifier(
		Crypto.IdentifierKind kind,
		TransmitOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		TransmitOptions resolved = OrDefaultFeePayer(options);
		AccountState state = await State(cancellationToken).ConfigureAwait(false);

		Crypto.Account identifier = Account.GenerateIdentifier(kind, state.HeadBlock);
		try
		{
			using Crypto.BlockOperation claim = _runtime.Blocks.CreateIdentifier(identifier);
			using Crypto.BlockBuilder builder = InitBuilder();
			KeetaClient.PositionAfter(builder, state.HeadBlock?.ToString());
			using Crypto.Block block = builder.AddOperation(claim).Build();

			await _client.Transmit(block, resolved, cancellationToken).ConfigureAwait(false);
			return identifier;
		}
		catch
		{
			identifier.Dispose();
			throw;
		}
	}

	/// <summary>
	/// Sends <paramref name="amount"/> of <paramref name="token"/> to
	/// <paramref name="to"/> with an optional <paramref name="external"/>
	/// reference.
	/// </summary>
	public async Task<bool> Send(
		Crypto.Account to,
		BigInteger amount,
		Crypto.Account token,
		string? external = null,
		TransmitOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		using Crypto.BlockOperation send = _runtime.Blocks.Send(to, amount, token, external);
		return await BuildAndTransmit(send, options, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Sets the operating account's representative to <paramref name="representative"/>.</summary>
	public async Task<bool> SetRep(
		Crypto.Account representative,
		TransmitOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		using Crypto.BlockOperation setRep = _runtime.Blocks.SetRep(representative);
		return await BuildAndTransmit(setRep, options, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Adds or removes <paramref name="certificate"/> on the operating account.
	/// </summary>
	/// <remarks>
	/// An add records <paramref name="intermediates"/> alongside the
	/// certificate. A subtract retires the certificate by its hash and
	/// ignores the intermediates.
	/// </remarks>
	public async Task<bool> ModifyCertificate(
		Crypto.AdjustMethod method,
		Crypto.Certificate certificate,
		IReadOnlyList<Crypto.Certificate>? intermediates = null,
		TransmitOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		if (method == Crypto.AdjustMethod.Subtract)
		{
			return await ModifyCertificate(method, certificate.Hash, options, cancellationToken).ConfigureAwait(false);
		}

		RequireAdjust(method, Crypto.AdjustMethod.Add);
		using Crypto.BlockOperation add = _runtime.Blocks.ManageCertificateAdd(certificate, intermediates);

		return await BuildAndTransmit(add, options, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Removes the operating account's published certificate addressed by
	/// <paramref name="hash"/>.
	/// </summary>
	/// <remarks>
	/// Only <see cref="Crypto.AdjustMethod.Subtract"/> applies. An add needs
	/// the certificate itself.
	/// </remarks>
	public async Task<bool> ModifyCertificate(
		Crypto.AdjustMethod method,
		Crypto.CertificateHash hash,
		TransmitOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		RequireAdjust(method, Crypto.AdjustMethod.Subtract);
		using Crypto.BlockOperation remove = _runtime.Blocks.ManageCertificateRemove(hash);

		return await BuildAndTransmit(remove, options, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Publishes the operating account's on-chain info.
	/// </summary>
	/// <remarks><paramref name="defaultPermission"/> is required for identifier accounts.</remarks>
	public async Task<bool> SetInfo(
		string name,
		string description,
		string metadata,
		Crypto.Permissions? defaultPermission = null,
		TransmitOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		using Crypto.BlockOperation setInfo = _runtime.Blocks.SetInfo(name, description, metadata, defaultPermission);
		return await BuildAndTransmit(setInfo, options, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Applies <paramref name="permissions"/> to <paramref name="principal"/>
	/// with <paramref name="method"/>.
	/// </summary>
	/// <remarks>
	/// The grant scopes to <paramref name="target"/>, or to the operating
	/// account when omitted.
	/// </remarks>
	public async Task<bool> UpdatePermissions(
		Crypto.Account principal,
		Crypto.Permissions permissions,
		Crypto.Account? target = null,
		Crypto.AdjustMethod method = Crypto.AdjustMethod.Set,
		TransmitOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		using Crypto.BlockOperation modify = _runtime.Blocks.ModifyPermissions(principal, permissions, method, target);
		return await BuildAndTransmit(modify, options, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Registers <paramref name="handler"/> for changes to the operating account.
	/// </summary>
	/// <remarks>
	/// A WebSocket filtered to the operating account reacts to a new staple
	/// immediately. A fallback poll (see
	/// <see cref="ChangeListenerOptions.FallbackFrequency"/>) finds the
	/// updates that the socket missed. Either path re-reads the account and
	/// invokes the handlers only when its state changed. The delivered state
	/// is valid only for the duration of the callback. Dispose the returned
	/// subscription to unregister. The last disposal stops the socket and the poll.
	/// </remarks>
	public IDisposable OnChange(Action<AccountState> handler, ChangeListenerOptions? options = null)
	{
		ChangeListenerOptions resolved = options ?? new ChangeListenerOptions();
		var id = Guid.NewGuid();

		lock (_changeHandlers)
		{
			_changeHandlers.Add(id, handler);
			if (_changeHandlers.Count == 1)
			{
				StartChangeListener(resolved);
			}
		}

		return new ChangeSubscription(this, id);
	}

	/// <summary>Starts the poll and, when the representative advertises one, the socket.</summary>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP003:Dispose previous before re-assigning",
		Justification = "Only called under the handler lock when no listener runs; StopChangeListener disposed and nulled the previous instances.")]
	private void StartChangeListener(ChangeListenerOptions options)
	{
		var cancellation = new CancellationTokenSource();
		_changeCancellation = cancellation;
		_changeTimer = new Timer(
			_ => _ = EmitIfChanged(cancellation.Token),
			state: null,
			options.FallbackFrequency,
			options.FallbackFrequency);

		if (_client.PrimaryP2pUrl is { } p2pUrl)
		{
			_ = RunChangeSocket(p2pUrl, cancellation.Token);
		}
	}

	/// <summary>Unregisters one subscription. The last removal stops the listener.</summary>
	private void RemoveChangeHandler(Guid id)
	{
		lock (_changeHandlers)
		{
			if (!_changeHandlers.Remove(id) || _changeHandlers.Count > 0)
			{
				return;
			}

			StopChangeListener();
		}
	}

	/// <summary>Stops the poll and the socket loop. Callers hold the handler lock.</summary>
	private void StopChangeListener()
	{
		_changeCancellation?.Cancel();
		_changeCancellation?.Dispose();
		_changeCancellation = null;
		_changeTimer?.Dispose();
		_changeTimer = null;
		_previousChangeFingerprint = null;
	}

	/// <summary>
	/// Runs the socket loop against the representative's P2P endpoint.
	/// </summary>
	/// <remarks>
	/// The loop greets as a participant filtered to the operating account and
	/// re-checks the account whenever a staple lands. It reconnects with
	/// exponential backoff.
	/// </remarks>
	private async Task RunChangeSocket(string p2pUrl, CancellationToken cancellationToken)
	{
		int attempts = 0;
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				using var socket = new ClientWebSocket();
				await socket.ConnectAsync(new Uri(p2pUrl), cancellationToken).ConfigureAwait(false);
				await GreetParticipant(socket, cancellationToken).ConfigureAwait(false);
				attempts = 0;

				await ListenForStaples(socket, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception exception) when (exception is WebSocketException or JsonException or IOException)
			{
				// Fall through to the reconnect delay. The poll still covers changes.
			}

			attempts++;
			TimeSpan backoff = TimeSpan.FromSeconds(Math.Pow(2, Math.Min(attempts, 6)));
			try
			{
				await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
	}

	/// <summary>Sends the participant greeting filtered to the operating account.</summary>
	private async Task GreetParticipant(ClientWebSocket socket, CancellationToken cancellationToken)
	{
		string greeting = JsonSerializer.Serialize(new
		{
			id = Guid.NewGuid().ToString(),
			greeting = new
			{
				kind = 0,
				filter = Account.PublicKeyString,
			},
		});

		await socket.SendAsync(
			Encoding.UTF8.GetBytes(greeting),
			WebSocketMessageType.Text,
			endOfMessage: true,
			cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Consumes socket messages until the socket closes and reacts to <c>add</c> notifications.</summary>
	private async Task ListenForStaples(ClientWebSocket socket, CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[64 * 1024];
		while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
		{
			using var message = new MemoryStream();
			WebSocketReceiveResult result;
			do
			{
				result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
				message.Write(buffer, 0, result.Count);
			}
			while (!result.EndOfMessage);

			if (result.MessageType == WebSocketMessageType.Close)
			{
				return;
			}

			using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(message.ToArray()));
			if (document.RootElement.TryGetProperty("add", out _))
			{
				await EmitIfChanged(cancellationToken).ConfigureAwait(false);
			}
		}
	}

	/// <summary>
	/// Re-reads the operating account and invokes the handlers when its state
	/// differs from the last emission. The state's accounts are released once
	/// the handlers return.
	/// </summary>
	private async Task EmitIfChanged(CancellationToken cancellationToken)
	{
		try
		{
			await _changeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return;
		}

		try
		{
			AccountState state = await State(cancellationToken).ConfigureAwait(false);
			try
			{
				string fingerprint = FingerprintOf(state);
				Action<AccountState>[] handlers;
				lock (_changeHandlers)
				{
					if (_previousChangeFingerprint == fingerprint)
					{
						return;
					}

					_previousChangeFingerprint = fingerprint;
					handlers = _changeHandlers.Values.ToArray();
				}

				foreach (Action<AccountState> handler in handlers)
				{
					handler(state);
				}
			}
			finally
			{
				ReleaseState(state);
			}
		}
		catch (OperationCanceledException)
		{
			// Torn down while reading. Nothing to emit.
		}
		catch (Exception failure) when (failure is KeetaException or HttpRequestException)
		{
			// A failed poll emits nothing. The next tick retries.
		}
		finally
		{
			try
			{
				_changeGate.Release();
			}
			catch (ObjectDisposedException)
			{
				// Dispose raced an in-flight check. The gate is gone with it.
			}
		}
	}

	/// <summary>Returns a stable digest of the state fields used for change detection.</summary>
	private static string FingerprintOf(AccountState state)
	{
		var digest = new StringBuilder();
		digest.Append(state.HeadBlock?.ToString() ?? "-");
		digest.Append('|').Append(state.HeadHeight?.ToString(CultureInfo.InvariantCulture) ?? "-");
		digest.Append('|').Append(state.Representative?.PublicKeyString ?? "-");
		digest.Append('|').Append(state.Info?.Name ?? "-");
		digest.Append('|').Append(state.Info?.Description ?? "-");
		digest.Append('|').Append(state.Info?.Metadata ?? "-");

		foreach (TokenBalance balance in state.Balances.OrderBy(entry => entry.Token.PublicKeyString, StringComparer.Ordinal))
		{
			digest.Append('|').Append(balance.Token.PublicKeyString)
				.Append(':').Append(balance.Balance.ToString(CultureInfo.InvariantCulture))
				.Append(':').Append(balance.Pending.ToString(CultureInfo.InvariantCulture));
		}

		return digest.ToString();
	}

	/// <summary>Releases the disposable accounts that a delivered state carries.</summary>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP007:Don't dispose injected",
		Justification = "The change listener owns the states it reads; handlers only borrow them for the callback.")]
	private static void ReleaseState(AccountState state)
	{
		state.Representative?.Dispose();
		foreach (TokenBalance balance in state.Balances)
		{
			balance.Token.Dispose();
		}
	}

	/// <summary>One registered change handler. Disposing it unregisters the handler.</summary>
	private sealed class ChangeSubscription : IDisposable
	{
		private readonly UserClient _owner;

		private readonly Guid _id;

		private bool _disposed;

		public ChangeSubscription(UserClient owner, Guid id)
		{
			_owner = owner;
			_id = id;
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			_owner.RemoveChangeHandler(_id);
		}
	}

	/// <summary>
	/// Stops any change listener and releases the owned
	/// <see cref="KeetaClient"/>. The bound accounts stay with the caller.
	/// </summary>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP007:Don't dispose injected",
		Justification = "The adopting constructor transfers ownership of the client to this instance.")]
	public void Dispose()
	{
		lock (_changeHandlers)
		{
			_changeHandlers.Clear();
			StopChangeListener();
		}

		_changeGate.Dispose();
		_client.Dispose();
	}

	/// <summary>Publishes the operating account's one-operation block.</summary>
	private async Task<bool> BuildAndTransmit(
		Crypto.BlockOperation operation,
		TransmitOptions? options,
		CancellationToken cancellationToken)
	{
		using Crypto.BlockBuilder builder = InitBuilder();
		builder.AddOperation(operation);

		return await PublishBuilder(builder, options, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Defaults the fee payer to the bound signer when no fee-block factory is set.</summary>
	private TransmitOptions OrDefaultFeePayer(TransmitOptions? options)
	{
		if (options?.FeeBlockFactory is not null)
		{
			return options;
		}

		TransmitOptions resolved = TransmitOptions.WithFeeSigner(RequireSigner());
		if (options is not null)
		{
			foreach (VoteQuote quote in options.Quotes)
			{
				resolved.Quotes.Add(quote);
			}

			foreach (Crypto.Account token in options.FeeTokenPriority)
			{
				resolved.FeeTokenPriority.Add(token);
			}
		}

		return resolved;
	}

	/// <summary>Rejects any certificate adjust method other than <paramref name="expected"/>.</summary>
	private static void RequireAdjust(Crypto.AdjustMethod method, Crypto.AdjustMethod expected)
	{
		if (method != expected)
		{
			throw new KeetaException(
				"ADJUST_METHOD",
				$"certificates support add and subtract; this overload handles {expected}");
		}
	}

	/// <summary>Returns the bound signer, which every write requires.</summary>
	private Crypto.Account RequireSigner() =>
		_signer ?? throw new KeetaException("SIGNER_REQUIRED", "bind a signer to the user client to build or transmit blocks");
}
