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
		using Account subject = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, subjectAlgorithm);
		using Account issuer = runtime.Accounts.FromSeed(TestSeeds.Issuer, 0, "ecdsa_secp256k1");

		JsonElement address = JsonSerializer.Deserialize<JsonElement>("""{"addressType":"HOME","postalCode":"34677","townName":"Oldsmar"}""");

		DateTimeOffset dateOfBirth = DateTimeOffset.FromUnixTimeSeconds(315_532_800);

		using KycCertificate leaf = runtime.KycCertificates.Builder()
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
			.Build();

		string pem = leaf.ToPem();
		Assert.Contains("BEGIN CERTIFICATE", pem, StringComparison.Ordinal);

		using KycCertificate parsed = runtime.KycCertificates.Parse(pem);

		byte[] postalCode = parsed.GetAttributeBuffer("postalCode");
		Assert.Equal("12345", Encoding.UTF8.GetString(postalCode));
		Assert.Equal("user@example.com", parsed.GetAttribute("email", subject).AsText());
		Assert.Equal(dateOfBirth, parsed.GetAttribute("dateOfBirth", subject).AsTimestamp());

		JsonElement decodedAddress = parsed.GetAttribute("address", subject).AsJson();
		JsonElement addressPostalCode = decodedAddress.GetProperty("postalCode");
		Assert.Equal("34677", addressPostalCode.GetString());
	}

	[Fact]
	public void WrongShapeAccessorsRejectWithTheDecodeCode()
	{
		using var runtime = WasmRuntime.Load();
		using Account subject = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, TestSeeds.DefaultAlgorithm);
		using Account issuer = runtime.Accounts.FromSeed(TestSeeds.Issuer, 0, "ecdsa_secp256k1");

		using KycCertificate leaf = runtime.KycCertificates.Builder()
			.Subject(subject)
			.Issuer(issuer)
			.SubjectName("Subject")
			.IssuerName("Issuer")
			.Serial(7)
			.Validity(TestSeeds.NotBefore, TestSeeds.NotAfter)
			.SetAttribute("email", sensitive: true, "user@example.com")
			.Build();

		KycAttributeValue email = leaf.GetAttribute("email", subject);

		// An email is neither a timestamp nor JSON. Each typed accessor must
		// refuse with the stable decode code instead of returning garbage.
		KeetaException notATimestamp = Assert.Throws<KeetaException>(() => email.AsTimestamp());
		Assert.Equal("ATTRIBUTE_DECODE", notATimestamp.Code);

		KeetaException notJson = Assert.Throws<KeetaException>(() => email.AsJson());
		Assert.Equal("ATTRIBUTE_DECODE", notJson.Code);

		// The undecoded bytes stay reachable regardless of decode failures.
		Assert.Equal("user@example.com", Encoding.UTF8.GetString(email.Buffer));
	}
}
