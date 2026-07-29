using System.Numerics;

namespace KeetaNet.Anchor;

/// <summary>
/// A <see cref="KeetaClient"/> bound to an operating account: reads imply the
/// account, writes originate from it and are signed by the bound signer, which
/// also pays any required fee by default. Without a signer the client is
/// read-only and writes throw <c>SIGNER_REQUIRED</c>.
/// </summary>
public sealed class UserClient : IDisposable
{
	private readonly WasmRuntime _runtime;

	private readonly KeetaClient _client;

	/// <summary>The operating account when it differs from the signer.</summary>
	private readonly Crypto.Account? _account;

	private readonly Crypto.Account? _signer;

	/// <summary>
	/// An owned <see cref="KeetaClient"/> for <paramref name="nodeUrl"/>
	/// bound to <paramref name="signer"/>, operating as
	/// <paramref name="account"/> when given and as the signer itself
	/// otherwise. Both accounts are borrowed, not disposed.
	/// </summary>
	internal UserClient(
		WasmRuntime runtime,
		string nodeUrl,
		HttpClient? http,
		long? network,
		Crypto.Account? signer,
		Crypto.Account? account)
	{
		_runtime = runtime;
		_client = new KeetaClient(runtime, nodeUrl, http, network);
		_signer = signer;
		_account = account;
	}

	/// <summary>The underlying client, for reads beyond the operating account.</summary>
	public KeetaClient Client => _client;

	/// <summary>The bound signer, if any.</summary>
	public Crypto.Account? Signer => _signer;

	/// <summary>Whether this client has no signer and therefore rejects writes.</summary>
	public bool IsReadOnly => _signer is null;

	/// <summary>
	/// The operating account: the configured account, then the signer.
	/// Throws <c>SIGNER_REQUIRED</c> when neither is bound.
	/// </summary>
	public Crypto.Account Account =>
		_account
		?? _signer
		?? throw new KeetaException("SIGNER_REQUIRED", "bind a signer or an operating account to the user client");

	/// <summary>The full state of the operating account.</summary>
	public Task<AccountState> GetState(CancellationToken cancellationToken = default) =>
		_client.GetAccountState(Account, cancellationToken);

	/// <summary>The settled balance of <paramref name="token"/> held by the operating account.</summary>
	public Task<BigInteger> GetBalance(Crypto.Account token, CancellationToken cancellationToken = default) =>
		_client.GetAccountBalance(Account, token, cancellationToken);

	/// <summary>Every token balance held by the operating account.</summary>
	public Task<IReadOnlyList<TokenBalance>> GetAllBalances(CancellationToken cancellationToken = default) =>
		_client.GetAccountBalances(Account, cancellationToken);

	/// <summary>The certificates published by the operating account.</summary>
	public Task<IReadOnlyList<Certificate>> GetAllCertificates(CancellationToken cancellationToken = default) =>
		_client.GetAllCertificates(Account, cancellationToken);

	/// <summary>
	/// The certificate the operating account published under
	/// <paramref name="certificateHash"/>, or null when it never did.
	/// </summary>
	public Task<Certificate?> GetCertificateByHash(
		Crypto.CertificateHash certificateHash,
		CancellationToken cancellationToken = default) =>
		_client.GetCertificateByHash(Account, certificateHash, cancellationToken);

	/// <summary>The head block of the operating account's chain, or null for a fresh account.</summary>
	public Task<Crypto.Block?> GetHeadBlock(CancellationToken cancellationToken = default) =>
		_client.GetHeadBlock(Account, cancellationToken);

	/// <summary>The next pending (unreceived) block for the operating account, if any.</summary>
	public Task<Crypto.Block?> GetPendingBlock(CancellationToken cancellationToken = default) =>
		_client.GetPendingBlock(Account, cancellationToken);

	/// <summary>
	/// The block the operating account produced for the idempotent
	/// <paramref name="key"/>, if any.
	/// </summary>
	public Task<Crypto.Block?> GetBlockFromIdempotent(
		string key,
		LedgerSide? side = null,
		CancellationToken cancellationToken = default) =>
		_client.GetBlockFromIdempotent(Account, key, side, cancellationToken);

	/// <summary>A page of the operating account's block chain, most recent first.</summary>
	public Task<ChainPage> GetChain(ChainQuery? query = null, CancellationToken cancellationToken = default) =>
		_client.GetAccountChain(Account, query, cancellationToken);

	/// <summary>A page of the operating account's committed staple history.</summary>
	public Task<HistoryPage> GetHistory(HistoryQuery? query = null, CancellationToken cancellationToken = default) =>
		_client.GetAccountHistory(Account, query, cancellationToken);

	/// <summary>ACL entries where the operating account is the principal.</summary>
	public Task<IReadOnlyList<Acl>> GetAcls(CancellationToken cancellationToken = default) =>
		_client.GetAclsByPrincipal(Account, cancellationToken);

	/// <summary>ACL entries granted to the operating account as an entity.</summary>
	public Task<IReadOnlyList<Acl>> GetAclsByEntity(CancellationToken cancellationToken = default) =>
		_client.GetAclsByEntity(Account, cancellationToken);

	/// <summary>
	/// A builder for the operating account, signed by the bound signer and
	/// pre-set with the client's defaults. The caller positions it, appends
	/// operations, and builds. Requires a signer and a bound network.
	/// </summary>
	public Crypto.BlockBuilder InitBuilder() => _client.InitBuilder(Account, RequireSigner());

	/// <summary>
	/// Publish one signed block, paying any required fee with the bound
	/// signer unless <paramref name="options"/> carries a fee-block factory.
	/// </summary>
	public Task<bool> Transmit(
		Crypto.Block block,
		TransmitOptions? options = null,
		CancellationToken cancellationToken = default) =>
		Transmit(new[] { block }, options, cancellationToken);

	/// <summary>
	/// Publish <paramref name="blocks"/> as one atomic staple, paying any
	/// required fee with the bound signer unless <paramref name="options"/>
	/// carries a fee-block factory.
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
	/// Position <paramref name="builder"/> atop the operating account's
	/// ledger head (opening a fresh chain when it has none), build its block,
	/// and transmit it, the reference <c>publishBuilder</c>. The builder must
	/// not carry a position of its own.
	/// </summary>
	public async Task<bool> Publish(
		Crypto.BlockBuilder builder,
		TransmitOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		// Require a signer to publish a block
		_ = RequireSigner();

		TransmitOptions resolved = OrDefaultFeePayer(options);
		AccountState state = await GetState(cancellationToken).ConfigureAwait(false);

		KeetaClient.PositionAfter(builder, state.HeadBlock?.ToString());
		using Crypto.Block block = builder.Build();

		return await _client.Transmit(block, resolved, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Create a <paramref name="kind"/> identifier under the operating account
	/// and publish the creating block, returning the derived account. The
	/// caller owns the returned account.
	/// </summary>
	public async Task<Crypto.Account> GenerateIdentifier(
		Crypto.IdentifierKind kind,
		TransmitOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		TransmitOptions resolved = OrDefaultFeePayer(options);
		AccountState state = await GetState(cancellationToken).ConfigureAwait(false);

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
	/// Send <paramref name="amount"/> of <paramref name="token"/> to
	/// <paramref name="to"/>, carrying an optional <paramref name="external"/>
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

	/// <summary>Set the operating account's representative to <paramref name="representative"/>.</summary>
	public async Task<bool> SetRep(
		Crypto.Account representative,
		TransmitOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		using Crypto.BlockOperation setRep = _runtime.Blocks.SetRep(representative);
		return await BuildAndTransmit(setRep, options, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Publish the operating account's on-chain info.
	/// <paramref name="defaultPermission"/> is required for identifier accounts.
	/// </summary>
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
	/// Apply <paramref name="permissions"/> to <paramref name="principal"/>
	/// with <paramref name="method"/>, optionally scoped to
	/// <paramref name="target"/> (the operating account when omitted).
	/// </summary>
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

	/// <summary>Release the owned <see cref="KeetaClient"/>; the bound accounts stay with the caller.</summary>
	public void Dispose() => _client.Dispose();

	/// <summary>Publish the operating account's one-operation block.</summary>
	private async Task<bool> BuildAndTransmit(
		Crypto.BlockOperation operation,
		TransmitOptions? options,
		CancellationToken cancellationToken)
	{
		using Crypto.BlockBuilder builder = InitBuilder();
		builder.AddOperation(operation);

		return await Publish(builder, options, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Absent a fee-block factory, the bound signer pays any required fee itself.</summary>
	private TransmitOptions OrDefaultFeePayer(TransmitOptions? options)
	{
		if (options?.FeeBlockFactory is not null)
		{
			return options;
		}

		TransmitOptions resolved = TransmitOptions.WithFeeSigner(RequireSigner());
		if (options is not null)
		{
			resolved.Quote = options.Quote;
			foreach (Crypto.Account token in options.FeeTokenPriority)
			{
				resolved.FeeTokenPriority.Add(token);
			}
		}

		return resolved;
	}

	/// <summary>The bound signer, required by every write.</summary>
	private Crypto.Account RequireSigner() =>
		_signer ?? throw new KeetaException("SIGNER_REQUIRED", "bind a signer to the user client to build or transmit blocks");
}
