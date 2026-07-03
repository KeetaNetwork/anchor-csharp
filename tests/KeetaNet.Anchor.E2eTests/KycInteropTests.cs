using System.Text.Json;
using System.Text.Json.Nodes;
using KeetaNet.Anchor.Crypto;
using Xunit;
using CryptoCertificate = KeetaNet.Anchor.Crypto.Certificate;

namespace KeetaNet.Anchor.E2eTests;

/// <summary>
/// Cross-implementation KYC-certificate interop: each side must read,
/// verify, and prove against leaves the other side issued.
/// </summary>
public sealed class KycInteropTests
{
	/// <summary>The sensitive scalar the proof round-trips exercise.</summary>
	private const string ProvenAttribute = "email";

	[Fact]
	public void CsharpReadsAndProvesTheTypescriptIssuedLeaf()
	{
		IReadOnlyList<AttributeCase> cases = IssueAttributes.Cases();
		using var harness = NodeHarness.Spawn("kyc");
		KycAnchor.Start(harness);
		IssuedLeaf issued = IssuedLeaf.Issue(harness);

		using var runtime = WasmRuntime.Load();
		using Account subject = runtime.Accounts.FromSeed(E2eSeeds.Subject, 0, E2eSeeds.Secp256k1);
		using KycCertificate leaf = runtime.KycCertificates.Parse(issued.Pem);

		foreach (AttributeCase attribute in cases)
		{
			byte[] decoded = leaf.GetAttributeBuffer(attribute.Name, subject);
			IssueAttributes.AssertMatches(attribute, decoded);
		}

		// A C# proof must validate on both sides, and a TypeScript proof must
		// validate through the C# reader.
		AttributeProof localProof = leaf.GetProof(ProvenAttribute, subject);
		Assert.True(leaf.ValidateProof(ProvenAttribute, subject, localProof));
		Assert.True(ValidateThroughHarness(harness, issued.Pem, localProof));

		AttributeProof referenceProof = ProveThroughHarness(harness, issued.Pem);
		Assert.True(leaf.ValidateProof(ProvenAttribute, subject, referenceProof));

		harness.Shutdown();
	}

	[Fact]
	public void CsharpVerifiesTheTypescriptIssuedChain()
	{
		using var harness = NodeHarness.Spawn("kyc");
		KycAnchor.Start(harness);
		IssuedLeaf issued = IssuedLeaf.Issue(harness);
		harness.Shutdown();

		using var runtime = WasmRuntime.Load();
		using KycCertificate leaf = runtime.KycCertificates.Parse(issued.Pem);
		using CryptoCertificate ca = runtime.Certificates.Parse(issued.CaPem);

		Assert.True(
			leaf.Verify(new[] { ca }, Array.Empty<CryptoCertificate>(), DateTimeOffset.UtcNow),
			"the TypeScript-issued leaf must verify against its CA");
	}

	[Fact]
	public void TypescriptReadsAndValidatesTheCsharpIssuedLeaf()
	{
		IReadOnlyList<AttributeCase> cases = IssueAttributes.Cases();

		using var runtime = WasmRuntime.Load();
		using Account subject = runtime.Accounts.FromSeed(E2eSeeds.Subject, 0, E2eSeeds.Secp256k1);
		using Account issuer = runtime.Accounts.FromSeed(E2eSeeds.Issuer, 0, E2eSeeds.Secp256k1);
		using KycCertificate leaf = LocalLeaf.Issue(runtime, subject, issuer, cases);
		string leafPem = leaf.ToPem();

		using var harness = NodeHarness.Spawn("kyc");
		JsonElement attributes = DecodeThroughHarness(harness, leafPem, cases);

		foreach (AttributeCase attribute in cases)
		{
			JsonNode? actual = JsonNode.Parse(attributes.GetProperty(attribute.Name).GetRawText());
			IssueAttributes.AssertJsonEqual(attribute.Name, attribute.Expected, actual);
		}

		AttributeProof proof = leaf.GetProof(ProvenAttribute, subject);
		Assert.True(
			ValidateThroughHarness(harness, leafPem, proof),
			"the reference must validate a C# proof over a C#-issued leaf");

		harness.Shutdown();
	}

	/// <summary>Read the cases' attribute values from a leaf through the reference reader.</summary>
	private static JsonElement DecodeThroughHarness(
		NodeHarness harness,
		string leafPem,
		IReadOnlyList<AttributeCase> cases)
	{
		var arguments = new JsonObject
		{
			["leaf"] = leafPem,
			["subjectSeed"] = E2eSeeds.Subject,
			["attributes"] = IssueAttributes.NameArray(cases.Select(attribute => attribute.Name)),
		};

		JsonElement decoded = harness.Request("decodeCertificate", arguments);
		return decoded.GetProperty("attributes");
	}

	/// <summary>
	/// Validate a proof for the proven attribute through the reference reader.
	/// The reference proof shape nests the salt: <c>{ value, hash: { salt } }</c>.
	/// </summary>
	private static bool ValidateThroughHarness(NodeHarness harness, string leafPem, AttributeProof proof)
	{
		var arguments = new JsonObject
		{
			["leaf"] = leafPem,
			["subjectSeed"] = E2eSeeds.Subject,
			["name"] = ProvenAttribute,
			["proof"] = new JsonObject
			{
				["value"] = proof.Value,
				["hash"] = new JsonObject { ["salt"] = proof.Salt },
			},
		};

		JsonElement validated = harness.Request("validateProof", arguments);
		return validated.GetProperty("valid").GetBoolean();
	}

	/// <summary>Generate a proof for the proven attribute through the reference reader.</summary>
	private static AttributeProof ProveThroughHarness(NodeHarness harness, string leafPem)
	{
		var arguments = new JsonObject
		{
			["leaf"] = leafPem,
			["subjectSeed"] = E2eSeeds.Subject,
			["name"] = ProvenAttribute,
		};
		JsonElement proved = harness.Request("proveAttribute", arguments);
		JsonElement proof = proved.GetProperty("proof");

		return new AttributeProof(
			proof.GetProperty("value").GetString()!,
			proof.GetProperty("hash").GetProperty("salt").GetString()!);
	}
}
