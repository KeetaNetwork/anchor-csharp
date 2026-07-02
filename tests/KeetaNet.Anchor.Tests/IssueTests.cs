using System.Text;
using System.Text.Json;
using KeetaNet.Anchor.Crypto;
using Xunit;

namespace KeetaNet.Anchor.Tests;

/// <summary>
/// The fluent KYC leaf builder: issue a leaf, then read every attribute shape
/// back through a re-parse - plain scalar, decrypted scalar, decrypted date,
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

		JsonElement address = JsonSerializer.Deserialize<JsonElement>("""{"addressType":"HOME","postalCode":"34677","townName":"Oldsmar"}""");

		DateTimeOffset dateOfBirth = DateTimeOffset.FromUnixTimeSeconds(315_532_800);

		using KycCertificate leaf = KycCertificate.Builder(runtime)
			.Subject(subject)
			.Issuer(issuer)
			.SubjectName("Subject")
			.IssuerName("Issuer")
			.Serial(7)
			.Validity(TestSeeds.NotBefore, TestSeeds.NotAfter)
			.SetAttribute("postalCode", sensitive: false, "12345")
			.SetAttribute("email", sensitive: true, "user@example.com")
			.SetAttribute("dateOfBirth", sensitive: true, dateOfBirth)
			.SetAttribute("address", sensitive: true, address)
			.Issue();

		string pem = leaf.Pem();
		Assert.Contains("BEGIN CERTIFICATE", pem, StringComparison.Ordinal);

		using KycCertificate parsed = KycCertificate.Parse(runtime, pem);

		byte[] postalCode = parsed.PlainAttribute("postalCode");
		Assert.Equal("12345", Encoding.UTF8.GetString(postalCode));
		Assert.Equal("user@example.com", parsed.GetText("email", subject));
		Assert.Equal("1980-01-01T00:00:00.000Z", parsed.GetText("dateOfBirth", subject));

		JsonElement decodedAddress = parsed.GetJson("address", subject);
		JsonElement addressPostalCode = decodedAddress.GetProperty("postalCode");
		Assert.Equal("34677", addressPostalCode.GetString());
	}
}
