using KeetaNet.Anchor.Crypto;
using Xunit;
using CryptoCertificate = KeetaNet.Anchor.Crypto.Certificate;

namespace KeetaNet.Anchor.E2eTests;

/// <summary>
/// The full KYC client path against a live reference anchor: discover the
/// provider from on-chain metadata through the real node API, then create a
/// verification, poll its status, and fetch certificates - once per signing
/// algorithm to prove request-signing parity on every curve.
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
		using KycClient client = runtime.CreateKycClient(anchor.Api, anchor.Root, signer);

		IReadOnlyList<KycProvider> providers = await client.GetProvidersAsync(Countries, cancellationToken);
		KycProvider provider = Assert.Single(providers);
		Assert.Equal(anchor.ProviderId, provider.Id);

		using CryptoCertificate ca = client.GetCA(provider);
		Assert.NotEmpty(ca.SubjectPublicKey);

		VerificationOutcome created = await client.StartVerificationAsync(provider, Countries, cancellationToken: cancellationToken);
		Assert.NotNull(created.Ready);
		Verification verification = created.Ready!;
		Assert.NotEmpty(verification.Id);
		Assert.NotEmpty(verification.WebUrl);
		Assert.NotEmpty(verification.ExpectedCost.Token);

		// A redirect URL rides the signed create body; the server must accept
		// the extra field and still assign a verification.
		VerificationOutcome redirected = await client.StartVerificationAsync(provider, Countries, "https://example.test/done", cancellationToken);
		Assert.NotNull(redirected.Ready);
		Assert.NotEmpty(redirected.Ready!.Id);

		StatusOutcome status = await client.GetVerificationStatusAsync(provider, verification.Id, cancellationToken);
		Assert.NotNull(status.Ready);
		Assert.Equal("pending", status.Ready!.Status);
		Assert.True(status.Ready.RequiresManualVerification);

		CertificatesOutcome pending = await client.GetCertificatesAsync(provider, "pending", cancellationToken);
		Assert.Null(pending.Ready);
		Assert.NotNull(pending.RetryAfterMs);

		CertificatesOutcome ready = await client.GetCertificatesAsync(provider, "ready", cancellationToken);
		Assert.NotNull(ready.Ready);
		Assert.NotEmpty(ready.Ready!.Results);

		// A leaf issued for a verification is served back as its full
		// `[leaf, ca]` chain over the same signed-URL certificate path.
		IssuedLeaf issued = IssuedLeaf.Issue(harness);

		CertificatesOutcome chain = await client.GetCertificatesAsync(provider, issued.VerificationId, cancellationToken);
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
		using Account signer = runtime.Accounts.FromSeed(E2eSeeds.Caller, 0, E2eSeeds.Secp256k1);
		using KycClient client = runtime.CreateKycClient(anchor.Api, anchor.Root, signer);

		// An account that never published anything reads back as an empty list;
		// the Account overload resolves the address itself, as the reference does.
		IReadOnlyList<Certificate> none = await client.GetAllCertificatesAsync(signer, cancellationToken);
		Assert.Empty(none);

		// The harness records two certificates for a fresh holder: a leaf with
		// the CA as its intermediate bundle, and a bare leaf without one.
		PublishedChain chain = PublishedChain.Publish(harness);

		IReadOnlyList<Certificate> records = await client.GetAllCertificatesAsync(chain.Account, cancellationToken);
		Assert.Equal(2, records.Count);

		Certificate chained = Assert.Single(records, record => record.Intermediates.Count == 1);
		Certificate bare = Assert.Single(records, record => record.Intermediates.Count == 0);

		// Each served PEM must parse to the exact certificate the harness
		// published, byte-for-byte at the DER level.
		AssertSameCertificate(runtime, chain.Leaf, chained.Value);
		AssertSameCertificate(runtime, chain.Ca, chained.Intermediates[0]);
		AssertSameCertificate(runtime, chain.Bare, bare.Value);

		harness.Shutdown();
	}

	private static void AssertSameCertificate(WasmRuntime runtime, string expectedPem, string servedPem)
	{
		using CryptoCertificate expected = runtime.Certificates.Parse(expectedPem);
		using CryptoCertificate served = runtime.Certificates.Parse(servedPem);
		Assert.Equal(expected.ToDer(), served.ToDer());
	}
}
