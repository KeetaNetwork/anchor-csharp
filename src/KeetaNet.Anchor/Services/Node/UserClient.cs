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
		_client.Transmit(block, OrDefaultFeePayer(options), cancellationToken);

	/// <summary>
	/// Publish <paramref name="blocks"/> as one atomic staple, paying any
	/// required fee with the bound signer unless <paramref name="options"/>
	/// carries a fee-block factory.
	/// </summary>
	public Task<bool> Transmit(
		IReadOnlyList<Crypto.Block> blocks,
		TransmitOptions? options = null,
		CancellationToken cancellationToken = default) =>
		_client.Transmit(blocks, OrDefaultFeePayer(options), cancellationToken);

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

	/// <summary>Release the owned <see cref="KeetaClient"/>; the bound accounts stay with the caller.</summary>
	public void Dispose() => _client.Dispose();

	/// <summary>
	/// Build the operating account's one-operation block against its ledger
	/// head (opening a fresh chain when it has none) and transmit it.
	/// </summary>
	private async Task<bool> BuildAndTransmit(
		Crypto.BlockOperation operation,
		TransmitOptions? options,
		CancellationToken cancellationToken)
	{
		TransmitOptions resolved = OrDefaultFeePayer(options);
		AccountState state = await GetState(cancellationToken).ConfigureAwait(false);

		using Crypto.BlockBuilder builder = InitBuilder();
		KeetaClient.PositionAfter(builder, state.HeadBlock?.ToString());
		using Crypto.Block block = builder.AddOperation(operation).Build();

		return await _client.Transmit(block, resolved, cancellationToken).ConfigureAwait(false);
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
