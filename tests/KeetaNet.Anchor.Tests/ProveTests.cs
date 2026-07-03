using KeetaNet.Anchor.Crypto;
using Xunit;

namespace KeetaNet.Anchor.Tests;

/// <summary>
/// Selective disclosure over an issued leaf: a proof for a sensitive attribute
/// validates against the leaf, and a proof for a different attribute does not.
/// </summary>
public sealed class ProveTests
{
	private const string EmailAttribute = "email";

	[Theory]
	[MemberData(nameof(TestSeeds.Algorithms), MemberType = typeof(TestSeeds))]
	public void ProofValidatesForItsAttributeOnly(string subjectAlgorithm)
	{
		using var runtime = WasmRuntime.Load();
		using Account subject = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, subjectAlgorithm);
		using Account issuer = runtime.Accounts.FromSeed(TestSeeds.Issuer, 0, "ecdsa_secp256k1");

		using KycCertificate leaf = runtime.KycCertificates.Builder()
			.Subject(subject)
			.Issuer(issuer)
			.SubjectName("Subject")
			.IssuerName("Issuer")
			.Serial(7)
			.Validity(TestSeeds.NotBefore, TestSeeds.NotAfter)
			.SetAttribute(EmailAttribute, sensitive: true, "user@example.com")
			.SetAttribute("fullName", sensitive: true, "Test User")
			.Build();

		AttributeProof proof = leaf.GetProof(EmailAttribute, subject);
		Assert.NotEmpty(proof.Value);
		Assert.NotEmpty(proof.Salt);
		Assert.True(leaf.ValidateProof(EmailAttribute, subject, proof));

		AttributeProof other = leaf.GetProof("fullName", subject);
		Assert.False(leaf.ValidateProof(EmailAttribute, subject, other));
	}
}
