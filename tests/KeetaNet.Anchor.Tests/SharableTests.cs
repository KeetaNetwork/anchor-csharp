using System.Text;
using KeetaNet.Anchor.Crypto;
using Xunit;

namespace KeetaNet.Anchor.Tests;

/// <summary>
/// The sharable certificate-attributes surface: seal a subset of a leaf's
/// attributes for a recipient, reject a recipient-less export, then open the
/// PEM envelope and read the disclosed values back.
/// </summary>
public sealed class SharableTests
{
	private static readonly string[] EmailOnly = { "email" };
	private static readonly string[] BothAttributes = { "postalCode", "email" };

	[Fact]
	public void ExportWithoutARecipientIsRejected()
	{
		using var runtime = WasmRuntime.Load();
		using Account subject = Account.FromSeed(runtime, TestSeeds.Subject, 0, "ecdsa_secp256k1");
		using Account issuer = Account.FromSeed(runtime, TestSeeds.Issuer, 0, "ecdsa_secp256k1");
		using KycCertificate leaf = IssueLeaf(runtime, subject, issuer);
		using SharableCertificateAttributes bundle = SharableCertificateAttributes.FromCertificate(runtime, leaf, subject, names: EmailOnly);

		Assert.Throws<KeetaException>(() => bundle.Export());
	}

	[Theory]
	[MemberData(nameof(TestSeeds.Algorithms), MemberType = typeof(TestSeeds))]
	public void RecipientReadsTheDisclosedBundleBack(string algorithm)
	{
		using var runtime = WasmRuntime.Load();
		using Account subject = Account.FromSeed(runtime, TestSeeds.Subject, 0, algorithm);
		using Account issuer = Account.FromSeed(runtime, TestSeeds.Issuer, 0, algorithm);
		using Account recipient = Account.FromSeed(runtime, TestSeeds.Recipient, 0, algorithm);
		using KycCertificate leaf = IssueLeaf(runtime, subject, issuer);

		using SharableCertificateAttributes bundle = SharableCertificateAttributes.FromCertificate(runtime, leaf, subject, names: BothAttributes);
		bundle.GrantAccess(new[] { recipient });

		string pem = bundle.ToPem();

		using SharableCertificateAttributes opened = SharableCertificateAttributes.FromPem(runtime, pem, new[] { recipient });

		byte[]? postalCode = opened.AttributeValue("postalCode");
		Assert.Equal("12345", Encoding.UTF8.GetString(postalCode!));

		byte[]? email = opened.AttributeValue("email");
		Assert.Equal("john@example.com", Encoding.UTF8.GetString(email!));

		Assert.Null(opened.AttributeBuffer("doesNotExist"));

		using KycCertificate embedded = opened.LeafCertificate();
		Assert.Contains("BEGIN CERTIFICATE", embedded.Pem(), StringComparison.Ordinal);

		IReadOnlyList<byte[]> principals = opened.Principals();
		Assert.Single(principals);
		Assert.Equal(recipient.PublicKey, Convert.ToHexString(principals[0]), ignoreCase: true);

		Assert.Equal(2, opened.AttributeNames().Count);
	}

	private static KycCertificate IssueLeaf(WasmRuntime runtime, Account subject, Account issuer)
	{
		return KycCertificate.Builder(runtime)
			.Subject(subject)
			.Issuer(issuer)
			.SubjectName("Subject")
			.IssuerName("Issuer")
			.Serial(7)
			.Validity(TestSeeds.NotBefore, TestSeeds.NotAfter)
			.SetAttribute("postalCode", sensitive: false, "12345")
			.SetAttribute("email", sensitive: true, "john@example.com")
			.Issue();
	}
}
