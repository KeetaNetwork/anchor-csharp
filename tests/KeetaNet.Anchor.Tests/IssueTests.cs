using System.Text;
using System.Text.Json;
using KeetaNet.Anchor.Crypto;
using Xunit;

namespace KeetaNet.Anchor.Tests;

/// <summary>
/// The fluent KYC leaf builder: issue a leaf, then read every attribute shape
/// back through a re-parse — plain scalar, decrypted scalar, decrypted date,
/// and decrypted structured value.
/// </summary>
public sealed class IssueTests
{
	[Theory]
	[MemberData(nameof(TestSeeds.Algorithms), MemberType = typeof(TestSeeds))]
	public void IssuedLeafRoundTripsEveryAttributeShape(string subjectAlgorithm)
	{
		using var runtime = WasmRuntime.Load();
		using Account subject = Account.FromSeed(runtime, TestSeeds.Subject, 0, subjectAlgorithm);
		using Account issuer = Account.FromSeed(runtime, TestSeeds.Issuer, 0, "ecdsa_secp256k1");

		JsonElement address = JsonSerializer.Deserialize<JsonElement>(
			"""{"addressType":"HOME","postalCode":"34677","townName":"Oldsmar"}""");

		using KycCertificate leaf = KycCertificate.Builder(runtime)
			.Subject(subject)
			.Issuer(issuer)
			.SubjectName("Subject")
			.IssuerName("Issuer")
			.Serial(7)
			.Validity(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), DateTimeOffset.FromUnixTimeSeconds(1_900_000_000))
			.SetAttribute("postalCode", sensitive: false, "12345")
			.SetAttribute("email", sensitive: true, "user@example.com")
			.SetAttribute("dateOfBirth", sensitive: true, DateTimeOffset.FromUnixTimeSeconds(315_532_800))
			.SetAttribute("address", sensitive: true, address)
			.Issue();

		string pem = leaf.Pem();
		Assert.Contains("BEGIN CERTIFICATE", pem, StringComparison.Ordinal);

		using KycCertificate parsed = KycCertificate.Parse(runtime, pem);
		Assert.Equal("12345", Encoding.UTF8.GetString(parsed.PlainAttribute("postalCode")));
		Assert.Equal("user@example.com", parsed.GetText("email", subject));
		Assert.Equal("1980-01-01T00:00:00.000Z", parsed.GetText("dateOfBirth", subject));
		Assert.Equal("34677", parsed.GetJson("address", subject).GetProperty("postalCode").GetString());
	}
}
