using System.Text.Json;
using System.Text.Json.Nodes;
using KeetaNet.Anchor.Crypto;
using Xunit;

namespace KeetaNet.Anchor.E2eTests;

/// <summary>
/// Cross-implementation sharable-bundle interop: a bundle sealed by one
/// implementation must open for the recipient on the other, with every
/// disclosed attribute buffer matching byte-for-byte.
/// </summary>
public sealed class SharableInteropTests
{
	/// <summary>
	/// The shared fixture subset the reference's sharable path supports:
	/// its known sensitive string attributes.
	/// </summary>
	private static readonly string[] Names = { "email", "fullName" };

	[Theory]
	[MemberData(nameof(E2eSeeds.Algorithms), MemberType = typeof(E2eSeeds))]
	public void CsharpOpensTheTypescriptBuiltBundle(string algorithm)
	{
		using var harness = NodeHarness.Spawn("sharable");
		var buildArguments = new JsonObject
		{
			["subjectSeed"] = E2eSeeds.Subject,
			["recipientSeed"] = E2eSeeds.Recipient,
			["attributes"] = IssueAttributes.ForHarness(Names),
			["algorithm"] = E2eSeeds.HarnessAlgorithm(algorithm),
		};
		JsonElement built = harness.Request("buildSharable", buildArguments);
		string pem = built.GetProperty("pem").GetString()!;
		JsonElement referenceBuffers = built.GetProperty("buffers");

		harness.Shutdown();

		using var runtime = WasmRuntime.Load();
		using Account recipient = Account.FromSeed(runtime, E2eSeeds.Recipient, 0, algorithm);
		using SharableCertificateAttributes opened = SharableCertificateAttributes.FromPem(runtime, pem, new[] { recipient });

		foreach (string name in Names)
		{
			byte[] reference = Convert.FromBase64String(referenceBuffers.GetProperty(name).GetString()!);
			Assert.Equal(reference, opened.AttributeBuffer(name));
		}
	}

	[Theory]
	[MemberData(nameof(E2eSeeds.Algorithms), MemberType = typeof(E2eSeeds))]
	public void TypescriptOpensTheCsharpBuiltBundle(string algorithm)
	{
		IReadOnlyList<AttributeCase> cases = IssueAttributes.Cases(Names);

		using var runtime = WasmRuntime.Load();
		using Account subject = Account.FromSeed(runtime, E2eSeeds.Subject, 0, algorithm);
		using Account issuer = Account.FromSeed(runtime, E2eSeeds.Issuer, 0, algorithm);
		using Account recipient = Account.FromSeed(runtime, E2eSeeds.Recipient, 0, algorithm);

		using KycCertificate leaf = LocalLeaf.Issue(runtime, subject, issuer, cases);
		using SharableCertificateAttributes bundle = SharableCertificateAttributes.FromCertificate(runtime, leaf, subject, names: Names);
		bundle.GrantAccess(new[] { recipient });

		using var harness = NodeHarness.Spawn("sharable");
		var readArguments = new JsonObject
		{
			["pem"] = bundle.ToPem(),
			["recipientSeed"] = E2eSeeds.Recipient,
			["names"] = IssueAttributes.NameArray(Names),
			["algorithm"] = E2eSeeds.HarnessAlgorithm(algorithm),
		};
		JsonElement read = harness.Request("readSharable", readArguments);
		JsonElement referenceBuffers = read.GetProperty("buffers");

		harness.Shutdown();

		foreach (string name in Names)
		{
			byte[] reference = Convert.FromBase64String(referenceBuffers.GetProperty(name).GetString()!);
			Assert.Equal(bundle.AttributeBuffer(name), reference);
		}
	}
}
