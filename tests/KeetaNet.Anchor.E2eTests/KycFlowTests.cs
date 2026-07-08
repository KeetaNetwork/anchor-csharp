using System.Numerics;

using KeetaNet.Anchor.Crypto;
using Xunit;
using CryptoCertificate = KeetaNet.Anchor.Crypto.Certificate;

namespace KeetaNet.Anchor.E2eTests;

/// <summary>
/// The full KYC client path against a live reference anchor: discover the
/// provider from on-chain metadata through the real node API, then create a
/// verification, poll its status, and fetch certificates - once per signing
/// algorithm to prove every curve signs requests the anchor accepts.
/// </summary>
public sealed class KycFlowTests
{
	private static readonly string[] Countries = { "US" };

	[Theory]
	[MemberData(nameof(E2eSeeds.Algorithms), MemberType = typeof(E2eSeeds))]
	public async Task VerificationPathRunsAgainstTheLiveAnchor(string algorithm)
	{
		CancellationToken cancellationToken = TestContext.Current.CancellationToken;
		using var harness = NodeHarness.Spawn("kyc");
		KycAnchor anchor = KycAnchor.Start(harness);

		using var runtime = WasmRuntime.Load();
		using Account signer = runtime.Accounts.FromSeed(E2eSeeds.Caller, 0, algorithm);
		using KycClient client = runtime.CreateKycClient(anchor.NodeApi, anchor.Root, signer);

		IReadOnlyList<KycProvider> providers = await client.GetProviders(Countries, cancellationToken);
		KycProvider provider = Assert.Single(providers);
		Assert.Equal(anchor.ProviderId, provider.Id);

		// The anchor advertises exactly the US, so the aggregate coverage is
		// that one country, not worldwide.
		SupportedCountries supported = await client.GetSupportedCountries(cancellationToken);
		Assert.False(supported.Worldwide);
		Assert.Equal(Countries, supported.Countries);

		using CryptoCertificate ca = client.GetCA(provider);
		Assert.NotEmpty(ca.SubjectPublicKey);

		VerificationOutcome created = await client.StartVerification(provider, Countries, cancellationToken: cancellationToken);
		Assert.NotNull(created.Ready);
		Verification verification = created.Ready!;
		Assert.NotEmpty(verification.Id);
		Assert.NotEmpty(verification.WebUrl);
		Assert.NotEmpty(verification.ExpectedCost.Token);

		// A redirect URL rides the signed create body. The server must accept
		// the extra field and still assign a verification.
		VerificationOutcome redirected = await client.StartVerification(provider, Countries, "https://example.test/done", cancellationToken);
		Assert.NotNull(redirected.Ready);
		Assert.NotEmpty(redirected.Ready!.Id);

		StatusOutcome status = await client.GetVerificationStatus(provider, verification.Id, cancellationToken);
		Assert.NotNull(status.Ready);
		Assert.Equal("pending", status.Ready!.Status);
		Assert.True(status.Ready.RequiresManualVerification);

		CertificatesOutcome pending = await client.GetCertificates(provider, "pending", cancellationToken);
		Assert.Null(pending.Ready);
		Assert.NotNull(pending.RetryAfterMs);

		CertificatesOutcome ready = await client.GetCertificates(provider, "ready", cancellationToken);
		Assert.NotNull(ready.Ready);
		Assert.NotEmpty(ready.Ready!.Results);

		// A leaf issued for a verification is served back as its full
		// `[leaf, ca]` chain over the same signed-URL certificate path.
		IssuedLeaf issued = IssuedLeaf.Issue(harness);

		CertificatesOutcome chain = await client.GetCertificates(provider, issued.VerificationId, cancellationToken);
		Assert.NotNull(chain.Ready);
		Assert.Equal(2, chain.Ready!.Results.Count);

		harness.Shutdown();
	}

	[Fact]
	public async Task LedgerReadServesEveryPublishedCertificateRecord()
	{
		CancellationToken cancellationToken = TestContext.Current.CancellationToken;
		using var harness = NodeHarness.Spawn("kyc");
		KycAnchor anchor = KycAnchor.Start(harness);

		using var runtime = WasmRuntime.Load();
		using Account observer = runtime.Accounts.FromSeed(E2eSeeds.Caller, 0, E2eSeeds.Secp256k1);
		using NodeClient client = runtime.CreateNodeClient(anchor.NodeApi);

		// An account that never published anything reads back as an empty list.
		// The Account overload resolves the address itself, as the reference does.
		IReadOnlyList<Certificate> none = await client.GetAllCertificates(observer, cancellationToken);
		Assert.Empty(none);

		// The harness records two certificates for a fresh holder: a leaf with
		// the CA as its intermediate bundle, and a bare leaf without one.
		PublishedChain chain = PublishedChain.Publish(harness);
		using Account holder = runtime.Accounts.FromAccount(chain.Account);

		IReadOnlyList<Certificate> records = await client.GetAllCertificates(holder, cancellationToken);
		Assert.Equal(2, records.Count);

		Certificate chained = Assert.Single(records, record => record.Intermediates.Count == 1);
		Certificate bare = Assert.Single(records, record => record.Intermediates.Count == 0);

		// Each served PEM must parse to the exact certificate the harness
		// published, byte-for-byte at the DER level.
		AssertSameCertificate(runtime, chain.Leaf, chained.Value);
		AssertSameCertificate(runtime, chain.Ca, chained.Intermediates[0]);
		AssertSameCertificate(runtime, chain.Bare, bare.Value);

		// The C#-computed certificate hash is the exact key the ledger stores
		// the record under.
		using CryptoCertificate publishedLeaf = runtime.Certificates.Parse(chain.Leaf);
		Assert.Equal(CertificateHash.Parse(chain.LeafHash), publishedLeaf.Hash);

		// The leaf is individually addressable by its hash, intermediates
		// intact. An unpublished hash resolves to null, as the reference does.
		Certificate? byHash = await client.GetCertificateByHash(holder, publishedLeaf.Hash, cancellationToken);
		Assert.NotNull(byHash);
		AssertSameCertificate(runtime, chain.Leaf, byHash!.Value);
		Assert.Single(byHash.Intermediates);

		CertificateHash unpublished = CertificateHash.Parse(new string('0', 64));
		Certificate? unknown = await client.GetCertificateByHash(holder, unpublished, cancellationToken);
		Assert.Null(unknown);

		harness.Shutdown();
	}

	[Fact]
	public async Task BasicLedgerReadsReportTheHolderStateAndBalances()
	{
		CancellationToken cancellationToken = TestContext.Current.CancellationToken;
		using var harness = NodeHarness.Spawn("kyc");
		KycAnchor anchor = KycAnchor.Start(harness);

		using var runtime = WasmRuntime.Load();
		using NodeClient client = runtime.CreateNodeClient(anchor.NodeApi);

		string version = await client.GetNodeVersion(cancellationToken);
		Assert.NotEmpty(version);

		// The chain holder was funded with base tokens before publishing (fees
		// then nibble at the amount), so the three balance reads must agree on
		// one positive settled amount under the base token.
		PublishedChain chain = PublishedChain.Publish(harness);
		using Account holder = runtime.Accounts.FromAccount(chain.Account);

		AccountState state = await client.GetAccountState(holder, cancellationToken);
		Assert.NotNull(state.HeadBlock);
		TokenBalance funding = Assert.Single(state.Balances);
		Assert.True(funding.Balance > BigInteger.Zero);

		IReadOnlyList<TokenBalance> balances = await client.GetAccountBalances(holder, cancellationToken);
		TokenBalance listed = Assert.Single(balances);
		Assert.Equal(funding.Token.Address, listed.Token.Address);
		Assert.Equal(funding.Balance, listed.Balance);

		// The token account the state read returned round-trips as the typed
		// argument of the direct balance read.
		BigInteger direct = await client.GetAccountBalance(holder, funding.Token, cancellationToken);
		Assert.Equal(funding.Balance, direct);

		harness.Shutdown();
	}

	[Fact]
	public async Task PublishedChainStatusMatchesTheTrustSet()
	{
		CancellationToken cancellationToken = TestContext.Current.CancellationToken;
		using var harness = NodeHarness.Spawn("kyc");
		KycAnchor anchor = KycAnchor.Start(harness);

		using var runtime = WasmRuntime.Load();
		using NodeClient client = runtime.CreateNodeClient(anchor.NodeApi);

		PublishedChain chain = PublishedChain.Publish(harness);
		using Account holder = runtime.Accounts.FromAccount(chain.Account);
		using CryptoCertificate publisherCa = runtime.Certificates.Parse(chain.Ca);
		CryptoCertificate[] trusted = { publisherCa };
		DateTimeOffset now = DateTimeOffset.UtcNow;

		// The published records chain to the anchor's CA, so trusting it
		// reports trusted at the present moment.
		CertificateChainStatus status = await client.VerifyAccountCertificateChain(holder, trusted, now, cancellationToken);
		Assert.Equal(CertificateChainStatus.Trusted, status);

		// A trust set holding only a CA that never signed anything published
		// leaves the same records untrusted.
		using KycCertificate foreignAuthority = IssueForeignAuthority(runtime);
		using CryptoCertificate foreignCa = foreignAuthority.Base();
		CryptoCertificate[] foreignTrust = { foreignCa };
		CertificateChainStatus foreign = await client.VerifyAccountCertificateChain(holder, foreignTrust, now, cancellationToken);
		Assert.Equal(CertificateChainStatus.Untrusted, foreign);

		// The harness leaves expire an hour after publishing. Two days out the
		// same trusted set no longer accepts them.
		CertificateChainStatus expired = await client.VerifyAccountCertificateChain(holder, trusted, now.AddDays(2), cancellationToken);
		Assert.Equal(CertificateChainStatus.Untrusted, expired);

		// An account that never published reports no-certs, not untrusted.
		using Account observer = runtime.Accounts.FromSeed(E2eSeeds.Caller, 0, E2eSeeds.Secp256k1);
		CertificateChainStatus none = await client.VerifyAccountCertificateChain(observer, trusted, now, cancellationToken);
		Assert.Equal(CertificateChainStatus.NoCerts, none);

		harness.Shutdown();
	}

	/// <summary>A self-issued CA unrelated to anything the harness published.</summary>
	private static KycCertificate IssueForeignAuthority(WasmRuntime runtime)
	{
		using Account account = runtime.Accounts.FromSeed(E2eSeeds.Recipient, 0, E2eSeeds.Secp256k1);

		return runtime.KycCertificates.Builder()
			.Subject(account)
			.Issuer(account)
			.SubjectName("Foreign CA")
			.IssuerName("Foreign CA")
			.Serial(9)
			.Validity(E2eSeeds.NotBefore, E2eSeeds.NotAfter)
			.AsCertificateAuthority()
			.Build();
	}

	private static void AssertSameCertificate(WasmRuntime runtime, string expectedPem, string servedPem)
	{
		using CryptoCertificate expected = runtime.Certificates.Parse(expectedPem);
		using CryptoCertificate served = runtime.Certificates.Parse(servedPem);
		Assert.Equal(expected.ToDer(), served.ToDer());
	}
}
