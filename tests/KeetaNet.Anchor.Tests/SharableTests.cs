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
	private const string EmailAttribute = "email";

	private static readonly string[] EmailOnly = { EmailAttribute };
	private static readonly string[] BothAttributes = { "postalCode", EmailAttribute };

	[Fact]
	public void ExportWithoutARecipientIsRejected()
	{
		using var runtime = WasmRuntime.Load();
		using Account subject = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, "ecdsa_secp256k1");
		using Account issuer = runtime.Accounts.FromSeed(TestSeeds.Issuer, 0, "ecdsa_secp256k1");
		using KycCertificate leaf = IssueLeaf(runtime, subject, issuer);
		using SharableCertificateAttributes bundle = runtime.Sharables.FromCertificate(leaf, subject, names: EmailOnly);

		Assert.Throws<KeetaException>(() => bundle.Export());
	}

	[Theory]
	[MemberData(nameof(TestSeeds.Algorithms), MemberType = typeof(TestSeeds))]
	public void RecipientReadsTheDisclosedBundleBack(string algorithm)
	{
		using var runtime = WasmRuntime.Load();
		using Account subject = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, algorithm);
		using Account issuer = runtime.Accounts.FromSeed(TestSeeds.Issuer, 0, algorithm);
		using Account recipient = runtime.Accounts.FromSeed(TestSeeds.Recipient, 0, algorithm);
		using KycCertificate leaf = IssueLeaf(runtime, subject, issuer);

		using SharableCertificateAttributes bundle = runtime.Sharables.FromCertificate(leaf, subject, names: BothAttributes);
		bundle.GrantAccess(new[] { recipient });

		string pem = bundle.ToPem();

		using SharableCertificateAttributes opened = runtime.Sharables.FromPem(pem, new[] { recipient });

		byte[]? postalCode = opened.GetAttributeValue("postalCode");
		Assert.Equal("12345", Encoding.UTF8.GetString(postalCode!));

		byte[]? email = opened.GetAttributeValue(EmailAttribute);
		Assert.Equal("john@example.com", Encoding.UTF8.GetString(email!));

		Assert.Null(opened.GetAttributeBuffer("doesNotExist"));

		using KycCertificate embedded = opened.GetCertificate();
		Assert.Contains("BEGIN CERTIFICATE", embedded.ToPem(), StringComparison.Ordinal);

		IReadOnlyList<byte[]> principals = opened.GetPrincipals();
		Assert.Single(principals);
		Assert.Equal(recipient.PublicKeyAndType, Convert.ToHexString(principals[0]), ignoreCase: true);

		Assert.Equal(2, opened.GetAttributeNames().Count);
	}

	private static KycCertificate IssueLeaf(WasmRuntime runtime, Account subject, Account issuer)
	{
		return runtime.KycCertificates.Builder()
			.Subject(subject)
			.Issuer(issuer)
			.SubjectName("Subject")
			.IssuerName("Issuer")
			.Serial(7)
			.Validity(TestSeeds.NotBefore, TestSeeds.NotAfter)
			.SetAttribute("postalCode", sensitive: false, "12345")
			.SetAttribute(EmailAttribute, sensitive: true, "john@example.com")
			.Build();
	}
}
