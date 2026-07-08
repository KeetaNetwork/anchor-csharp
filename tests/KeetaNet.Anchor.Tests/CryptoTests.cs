using System.Text;
using KeetaNet.Anchor.Crypto;
using Xunit;

// `Certificate` also names the KYC DTO record in `KeetaNet.Anchor`.
using CryptoCertificate = KeetaNet.Anchor.Crypto.Certificate;

namespace KeetaNet.Anchor.Tests;

/// <summary>
/// The <c>crypto</c> account surface: derivation, signing, and
/// encryption round-trips through the embedded core module.
/// </summary>
public sealed class CryptoTests
{
	[Theory]
	[MemberData(nameof(TestSeeds.Algorithms), MemberType = typeof(TestSeeds))]
	public void AccountDerivesSignsAndVerifies(string algorithm)
	{
		using var runtime = WasmRuntime.Load();
		using Account account = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, algorithm);

		Assert.Equal(algorithm, account.Algorithm);
		Assert.StartsWith("keeta_", account.PublicKeyString, StringComparison.Ordinal);

		byte[] message = Encoding.UTF8.GetBytes("crypto over p1");
		byte[] signature = account.Sign(message);
		Assert.NotEmpty(signature);
		Assert.True(account.Verify(message, signature));

		byte[] tampered = Encoding.UTF8.GetBytes("tampered");
		Assert.False(account.Verify(tampered, signature));
	}

	[Theory]
	[MemberData(nameof(TestSeeds.Algorithms), MemberType = typeof(TestSeeds))]
	public void EncryptToSelfRoundTrips(string algorithm)
	{
		using var runtime = WasmRuntime.Load();
		using Account account = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, algorithm);

		byte[] secret = Encoding.UTF8.GetBytes("for my eyes only");
		byte[] ciphertext = account.Encrypt(secret);
		byte[] decrypted = account.Decrypt(ciphertext);
		Assert.Equal(secret, decrypted);
	}

	[Fact]
	public void ReadOnlyAccountsShareTheSignerIdentityButCannotSign()
	{
		using var runtime = WasmRuntime.Load();
		using Account signer = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, TestSeeds.DefaultAlgorithm);

		byte[] message = Encoding.UTF8.GetBytes("watch-only verification");
		byte[] signature = signer.Sign(message);

		// An address-parsed account is the same identity and verifies the
		// signer's work, but carries no key material to sign with.
		using Account fromAccount = runtime.Accounts.FromPublicKeyString(signer.PublicKeyString);
		Assert.Equal(signer.PublicKeyString, fromAccount.PublicKeyString);
		Assert.True(fromAccount.Verify(message, signature));
		Assert.Throws<KeetaException>(() => fromAccount.Sign(message));

		// The type-prefixed transport key round-trips to the same identity.
		using Account fromPublicKey = runtime.Accounts.FromPublicKeyAndType(signer.PublicKeyAndType);
		Assert.Equal(signer.PublicKeyString, fromPublicKey.PublicKeyString);
		Assert.True(fromPublicKey.Verify(message, signature));
	}

	[Fact]
	public void ImportedPrivateKeyYieldsAWorkingSignerWithAStablePublicIdentity()
	{
		using var runtime = WasmRuntime.Load();
		using Account imported = runtime.Accounts.FromPrivateKey(TestSeeds.Issuer, TestSeeds.DefaultAlgorithm);

		Assert.StartsWith("keeta_", imported.PublicKeyString, StringComparison.Ordinal);

		byte[] message = Encoding.UTF8.GetBytes("imported key signer");
		byte[] signature = imported.Sign(message);
		Assert.True(imported.Verify(message, signature));

		// The raw public half plus its algorithm names the same on-ledger identity.
		string rawPublicKey = imported.PublicKeyAndType[2..];
		using Account publicHalf = runtime.Accounts.FromPublicKey(rawPublicKey, TestSeeds.DefaultAlgorithm);
		Assert.Equal(imported.PublicKeyString, publicHalf.PublicKeyString);
	}

	[Fact]
	public void IdentifierTypedPublicKeysAreRejectedWithACodedError()
	{
		using var runtime = WasmRuntime.Load();
		using Account signer = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, TestSeeds.DefaultAlgorithm);

		// A token identifier (type byte 3) carries no signing key, so the
		// factory must refuse it rather than mislabel the algorithm.
		string tokenTyped = "03" + signer.PublicKeyAndType[2..];
		KeetaException rejected = Assert.Throws<KeetaException>(() =>
		{
			using Account unexpected = runtime.Accounts.FromPublicKeyAndType(tokenTyped);
		});
		Assert.Equal("INVALID_ALGORITHM", rejected.Code);

		KeetaException notHex = Assert.Throws<KeetaException>(() =>
		{
			using Account unexpected = runtime.Accounts.FromPublicKeyAndType("not hex");
		});
		Assert.Equal("INVALID_PUBLIC_KEY", notHex.Code);
	}

	[Fact]
	public void GeneratedSeedsAreUniqueAndDeriveSigners()
	{
		using var runtime = WasmRuntime.Load();

		string first = runtime.Accounts.GenerateRandomSeed();
		string second = runtime.Accounts.GenerateRandomSeed();
		Assert.NotEqual(first, second);

		using Account account = runtime.Accounts.FromSeed(first, 0, TestSeeds.DefaultAlgorithm);
		byte[] message = Encoding.UTF8.GetBytes("generated seed signer");
		Assert.True(account.Verify(message, account.Sign(message)));
	}

	[Fact]
	public void GeneratedPassphraseDerivesASigner()
	{
		using var runtime = WasmRuntime.Load();

		IReadOnlyList<string> mnemonic = runtime.Accounts.GeneratePassphrase();
		Assert.True(mnemonic.Count is 12 or 24);

		using Account account = runtime.Accounts.FromPassphrase(mnemonic, 0, "ed25519");
		byte[] message = Encoding.UTF8.GetBytes("mnemonic signer");
		byte[] signature = account.Sign(message);
		Assert.True(account.Verify(message, signature));
	}

	[Fact]
	public void FixtureCertificateExposesItsFields()
	{
		using var runtime = WasmRuntime.Load();
		using Account subject = runtime.Accounts.FromSeed(KycFixture.SubjectSeed, 0, KycFixture.Algorithm);
		using CryptoCertificate certificate = runtime.Certificates.Parse(KycFixture.Pem);

		Assert.Contains("BEGIN CERTIFICATE", certificate.ToPem(), StringComparison.Ordinal);
		Assert.True(certificate.IsValidAt(KycFixture.ValidAt));

		DateTimeOffset epoch = DateTimeOffset.FromUnixTimeSeconds(0);
		Assert.False(certificate.IsValidAt(epoch));

		Assert.Contains("Test Subject", certificate.Subject, StringComparison.Ordinal);
		Assert.Contains("Test Issuer", certificate.Issuer, StringComparison.Ordinal);
		Assert.Equal("12345", certificate.Serial);
		Assert.True(certificate.NotBefore < certificate.NotAfter);
		Assert.InRange(KycFixture.ValidAt, certificate.NotBefore, certificate.NotAfter);
		Assert.Equal(subject.PublicKeyAndType, certificate.SubjectPublicKey);
	}

	[Fact]
	public void FixtureCertificateRoundTripsThroughDer()
	{
		using var runtime = WasmRuntime.Load();
		using CryptoCertificate parsed = runtime.Certificates.Parse(KycFixture.Pem);

		byte[] der = parsed.ToDer();
		Assert.NotEmpty(der);

		// Both transport encodings describe the same certificate.
		using CryptoCertificate fromDer = runtime.Certificates.ParseDer(der);
		Assert.Equal(parsed.Serial, fromDer.Serial);
		Assert.Equal(parsed.Subject, fromDer.Subject);
		Assert.Equal(parsed.ToPem(), fromDer.ToPem());
	}

	[Fact]
	public void FixtureCertificateHashIsAStableLedgerKey()
	{
		using var runtime = WasmRuntime.Load();
		using CryptoCertificate parsed = runtime.Certificates.Parse(KycFixture.Pem);

		CertificateHash hash = parsed.Hash;
		Assert.Equal(CertificateHash.Length, hash.ToBytes().Length);
		Assert.Equal(hash, parsed.Hash);
		Assert.Equal(hash, CertificateHash.Parse(hash.ToString().ToUpperInvariant()));

		using CryptoCertificate fromDer = runtime.Certificates.ParseDer(parsed.ToDer());
		Assert.Equal(hash, fromDer.Hash);
	}

	[Fact]
	public void FixtureKycCertificateReadsAndDecryptsAttributes()
	{
		using var runtime = WasmRuntime.Load();
		using Account subject = runtime.Accounts.FromSeed(KycFixture.SubjectSeed, 0, KycFixture.Algorithm);
		using KycCertificate kyc = runtime.KycCertificates.Parse(KycFixture.Pem);

		// The fixture was issued with raw OID attribute names.
		IReadOnlyList<string> attributeNames = kyc.GetAttributeNames();
		Assert.Equal(3, attributeNames.Count);
		Assert.All(attributeNames, name => Assert.Matches(@"^[\d.]+$", name));

		byte[] postalCode = kyc.GetAttributeBuffer("postalCode");
		Assert.Equal("12345", Encoding.UTF8.GetString(postalCode));

		byte[] email = kyc.GetAttributeBuffer("email", subject);
		Assert.Equal("john@example.com", Encoding.UTF8.GetString(email));

		using CryptoCertificate baseCertificate = kyc.Base();
		Assert.Contains("BEGIN CERTIFICATE", baseCertificate.ToPem(), StringComparison.Ordinal);

		using CryptoCertificate trustRoot = runtime.Certificates.Parse(KycFixture.Pem);
		CryptoCertificate[] roots = { trustRoot };
		CryptoCertificate[] none = Array.Empty<CryptoCertificate>();
		Assert.True(kyc.Verify(roots, none, KycFixture.ValidAt));
		Assert.False(kyc.Verify(none, none, KycFixture.ValidAt));
	}
}

/// <summary>
/// The embedded KYC fixture leaf: issued to the secp256k1 account at index 0 of
/// <see cref="SubjectSeed"/>, carrying one plain and two sensitive attributes.
/// </summary>
internal static class KycFixture
{
	public const string SubjectSeed = "D6986115BE7334E50DA8D73B1A4670A510E8BF47E8C5C9960B8F5248EC7D6E3D";
	public const string Algorithm = "ecdsa_secp256k1";
	public static readonly DateTimeOffset ValidAt = DateTimeOffset.FromUnixTimeSeconds(1_797_292_800);

	public const string Pem =
		"""
		-----BEGIN CERTIFICATE-----
		MIIDzTCCA3SgAwIBAgICMDkwCgYIKoZIzj0EAwIwFjEUMBIGA1UEAxYLVGVzdCBJ
		c3N1ZXIwIhgPMjAyNjA2MjgyMzIwNDVaGA8yMDI3MDYyODIzMjA0NVowFzEVMBMG
		A1UEAxYMVGVzdCBTdWJqZWN0MDYwEAYHKoZIzj0CAQYFK4EEAAoDIgACpkFiKH+5
		y+/csZUSPRIZwON061asGjraczszX1LL2HujggLOMIICyjAOBgNVHQ8BAf8EBAMC
		AMAwggK2BgorBgEEAYPpUwAABIICpjCCAqIwggFKBgorBgEEAYPpUwEDgYIBOjCC
		ATYCAQAwga0GCWCGSAFlAwQBLgQMfrJEYqEtjXoXFJrDBIGRBFEOgNX6ho8+Fil3
		91HDLYxx5u/l5UuOQFnJizMqoBkD/64XdrGWeURzt5ERG33SBxNJLaIbGLfU+w+a
		mu8HII50cSOjYYGalY7HbfAxqp0QStJZC9FTnr5+jHXQLSrfLnViXjPSz9sk7+xq
		eptUlXaromEIBaKAzavrUB8xlayBDh6hXNEToOjxmSai5f4khTBfBDD4fEMxz1aM
		wJbcmH5fi75NVNQH//2775k63qU3kWwuGu4yMrwa0TVvAd274S0xbC8GCWCGSAFl
		AwQCCAQgEj0cBCSSIdCPXWPhbdFGvSuSbegC0XhbAG82dmNRkbIEIA87wpxepdKD
		7qOY7UUEd9YUxIeSSBFwM2KPhO30zl+DMIIBQgYKKwYBBAGD6VMBAIGCATIwggEu
		AgEAMIGtBglghkgBZQMEAS4EDKznmG0IQycoVdJ9VQSBkQT/6Qumd90HGs1cof3u
		5derYnULnG3pbLxExHPqdzIwnOcXyFvGR8DDgBXYmUCspHjH3AQN6wYDfQ0IQ89F
		uakNlpGpGMWy152544+VG3fbrJmPkRhxKHPpYmQfiUGMqF0kGE7tLwzbC7cLx0ni
		jkkXUwlX5/UV3kJT3wBQciD1gKgl4euhYNxAfuyLtkZaZhkwXwQwJXrikAzhMr8q
		kKtaDkAohxfngm3mLEzsE+MmuI7hobUEIm59Uze8K3JG35L7OfVABglghkgBZQME
		AggEIGJ8nq65ul0UKAY3UL84Mg0Iddj9VYVNBa3oTnANZXYfBBgqlBgcLrd4of/W
		Hu4NJE0IKwCL+Gnbok4wDAYDVQURgAUxMjM0NTAKBggqhkjOPQQDAgNHADBEAiBY
		mcOwl1yNkItpFWeWby4gqa0rHOw7U0bHxpk9kYWHbgIgVbO0xyOAB7ByOqMO40Qh
		or6z8/Cbh+JIKGADPmGawrE=
		-----END CERTIFICATE-----
		""";
}
