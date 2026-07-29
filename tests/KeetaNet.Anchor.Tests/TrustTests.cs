using KeetaNet.Anchor.Crypto;
using Xunit;

// `Certificate` also names the KYC DTO record in `KeetaNet.Anchor`.
using CryptoCertificate = KeetaNet.Anchor.Crypto.Certificate;

namespace KeetaNet.Anchor.Tests;

/// <summary>
/// The certificate-chain trust gate: published records evaluated against a
/// trust set at a moment, the port of the reference
/// <c>evaluate_certificate_chain</c> cases.
/// </summary>
public sealed class TrustTests
{
	/// <summary>A moment inside the shared validity window.</summary>
	private static readonly DateTimeOffset Moment = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

	[Fact]
	public void ChainStatusMatchesTheTrustSet()
	{
		using var runtime = WasmRuntime.Load();
		using KeetaClient client = runtime.CreateKeetaClient(TestSeeds.NonRoutableAnchor);

		using Account caAccount = runtime.Accounts.FromSeed(TestSeeds.Issuer, 0, TestSeeds.DefaultAlgorithm);
		using Account subject = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, TestSeeds.DefaultAlgorithm);
		using Account foreignAccount = runtime.Accounts.FromSeed(TestSeeds.Recipient, 0, TestSeeds.DefaultAlgorithm);

		using KycCertificate ca = IssueAuthority(runtime, caAccount, "Anchor Test CA");
		using KycCertificate foreign = IssueAuthority(runtime, foreignAccount, "Foreign CA");
		using KycCertificate leaf = runtime.KycCertificates.Builder()
			.Subject(subject)
			.Issuer(caAccount)
			.SubjectName("Anchor Test Leaf")
			.IssuerName("Anchor Test CA")
			.Serial(2)
			.Validity(TestSeeds.NotBefore, TestSeeds.NotAfter)
			.Build();

		using CryptoCertificate trustedRoot = ca.Base();
		CryptoCertificate[] trusted = { trustedRoot };
		CryptoCertificate[] emptyTrust = Array.Empty<CryptoCertificate>();

		Certificate leafRecord = Record(leaf);
		Certificate caRecord = Record(ca);
		Certificate foreignRecord = Record(foreign);
		Certificate malformedRecord = new("not a pem", Array.Empty<string>());

		// One case per reference outcome. A malformed record is skipped, never
		// trusted, and still counts as published (untrusted, not no-certs).
		(string Label, Certificate[] Records, CryptoCertificate[] Trusted, CertificateChainStatus Expected)[] cases =
		{
			("leaf chains to trusted CA", new[] { leafRecord }, trusted, CertificateChainStatus.Trusted),
			("trusted CA presented directly", new[] { caRecord }, trusted, CertificateChainStatus.Trusted),
			("foreign cert is untrusted", new[] { foreignRecord }, trusted, CertificateChainStatus.Untrusted),
			("no records means no certs", Array.Empty<Certificate>(), trusted, CertificateChainStatus.NoCerts),
			("empty trust set is untrusted", new[] { leafRecord }, emptyTrust, CertificateChainStatus.Untrusted),
			("malformed record reports untrusted", new[] { malformedRecord }, trusted, CertificateChainStatus.Untrusted),
		};

		var expected = cases.Select(current => (current.Label, current.Expected)).ToArray();
		var evaluated = cases
			.Select(current => (current.Label, client.EvaluateCertificateChain(current.Records, current.Trusted, Moment)))
			.ToArray();

		Assert.Equal(expected, evaluated);
	}

	/// <summary>A self-issued certificate authority under the shared validity window.</summary>
	private static KycCertificate IssueAuthority(WasmRuntime runtime, Account account, string commonName) =>
		runtime.KycCertificates.Builder()
			.Subject(account)
			.Issuer(account)
			.SubjectName(commonName)
			.IssuerName(commonName)
			.Serial(1)
			.Validity(TestSeeds.NotBefore, TestSeeds.NotAfter)
			.AsCertificateAuthority()
			.Build();

	/// <summary>The published-record shape of an issued certificate, without intermediates.</summary>
	private static Certificate Record(KycCertificate certificate) =>
		new(certificate.ToPem(), Array.Empty<string>());
}
