using System.Text.Json;
using System.Text.Json.Nodes;
using KeetaNet.Anchor.Crypto;

namespace KeetaNet.Anchor.E2eTests;

/// <summary>
/// The fixture leaf the harness issued under the running anchor's CA, with the
/// verification id the anchor serves its <c>[leaf, ca]</c> chain back under.
/// </summary>
internal sealed record IssuedLeaf(string Pem, string CaPem, string VerificationId)
{
	/// <summary>Issue the full fixture for the shared subject through the harness.</summary>
	public static IssuedLeaf Issue(NodeHarness harness)
	{
		var arguments = new JsonObject
		{
			["subjectSeed"] = E2eSeeds.Subject,
			["attributes"] = IssueAttributes.ForHarness(),
		};
		JsonElement issued = harness.Request("issueCertificate", arguments);

		return new IssuedLeaf(
			issued.GetProperty("leaf").GetString()!,
			issued.GetProperty("ca").GetString()!,
			issued.GetProperty("verificationID").GetString()!);
	}
}

/// <summary>The fixture leaf the C# builder issues locally.</summary>
internal static class LocalLeaf
{
	/// <summary>Issue a leaf carrying <paramref name="cases"/> under the shared validity window.</summary>
	public static KycCertificate Issue(
		WasmRuntime runtime,
		Account subject,
		Account issuer,
		IReadOnlyList<AttributeCase> cases)
	{
		KycCertificateBuilder builder = KycCertificate.Builder(runtime)
			.Subject(subject)
			.Issuer(issuer)
			.SubjectName("Subject")
			.IssuerName("Issuer")
			.Serial(4)
			.Validity(E2eSeeds.NotBefore, E2eSeeds.NotAfter);

		foreach (AttributeCase attribute in cases)
		{
			builder.SetAttribute(attribute.Name, attribute.Sensitive, attribute.Semantic);
		}

		return builder.Issue();
	}
}
