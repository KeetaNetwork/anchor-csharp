using KeetaNet.Anchor.Crypto;
using Xunit;

namespace KeetaNet.Anchor.Tests;

/// <summary>
/// Selective disclosure over an issued leaf: a proof for a sensitive attribute
/// validates against the leaf, and a proof for a different attribute does not.
/// </summary>
public sealed class ProveTests
{
	[Theory]
	[MemberData(nameof(TestSeeds.Algorithms), MemberType = typeof(TestSeeds))]
	public void ProofValidatesForItsAttributeOnly(string subjectAlgorithm)
	{
		using var runtime = WasmRuntime.Load();
		using Account subject = Account.FromSeed(runtime, TestSeeds.Subject, 0, subjectAlgorithm);
		using Account issuer = Account.FromSeed(runtime, TestSeeds.Issuer, 0, "ecdsa_secp256k1");

		using KycCertificate leaf = KycCertificate.Builder(runtime)
			.Subject(subject)
			.Issuer(issuer)
			.SubjectName("Subject")
			.IssuerName("Issuer")
			.Serial(7)
			.Validity(TestSeeds.NotBefore, TestSeeds.NotAfter)
			.SetAttribute("email", sensitive: true, "user@example.com")
			.SetAttribute("fullName", sensitive: true, "Test User")
			.Issue();

		AttributeProof proof = leaf.Prove("email", subject);
		Assert.NotEmpty(proof.Value);
		Assert.NotEmpty(proof.Salt);
		Assert.True(leaf.ValidateProof("email", subject, proof));

		AttributeProof other = leaf.Prove("fullName", subject);
		Assert.False(leaf.ValidateProof("email", subject, other));
	}
}
