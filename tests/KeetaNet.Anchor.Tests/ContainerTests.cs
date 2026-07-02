using System.Text;
using KeetaNet.Anchor.Crypto;
using Xunit;

namespace KeetaNet.Anchor.Tests;

/// <summary>
/// The offline encrypted-container surface: plaintext and encrypted encoding
/// round-trips, detached signatures, and the grant/revoke principal cycle.
/// </summary>
public sealed class ContainerTests
{
	private static readonly byte[] Payload = Encoding.UTF8.GetBytes("container over p1");

	[Fact]
	public void PlaintextRoundTripsThroughItsEncoding()
	{
		using var runtime = WasmRuntime.Load();
		using EncryptedContainer plain = EncryptedContainer.FromPlaintext(runtime, Payload);

		byte[] encoded = plain.Encoded();
		using EncryptedContainer restored = EncryptedContainer.FromEncoded(runtime, encoded);
		Assert.Equal(Payload, restored.Plaintext());
		Assert.False(restored.IsEncrypted);
	}

	[Theory]
	[MemberData(nameof(TestSeeds.Algorithms), MemberType = typeof(TestSeeds))]
	public void PrincipalDecryptsTheSealedPayload(string algorithm)
	{
		using var runtime = WasmRuntime.Load();
		using Account owner = Account.FromSeed(runtime, TestSeeds.Subject, 0, algorithm);

		byte[] encoded;
		using (EncryptedContainer encrypted =
			EncryptedContainer.FromPlaintext(runtime, Payload, new[] { owner }, locked: false))
		{
			Assert.True(encrypted.IsEncrypted);
			encoded = encrypted.Encoded();
		}

		using EncryptedContainer opened = EncryptedContainer.FromEncrypted(runtime, encoded, new[] { owner });
		Assert.Equal(Payload, opened.Plaintext());
		Assert.True(opened.IsEncrypted);
	}

	[Theory]
	[MemberData(nameof(TestSeeds.Algorithms), MemberType = typeof(TestSeeds))]
	public void SignedContainerVerifiesAndRecoversItsSigner(string algorithm)
	{
		using var runtime = WasmRuntime.Load();
		using Account signer = Account.FromSeed(runtime, TestSeeds.Issuer, 0, algorithm);

		byte[] encoded;
		using (EncryptedContainer signed =
			EncryptedContainer.FromPlaintext(runtime, Payload, locked: false, signer: signer))
		{
			encoded = signed.Encoded();
		}

		using EncryptedContainer restored = EncryptedContainer.FromEncoded(runtime, encoded);
		Assert.True(restored.IsSigned);
		Assert.True(restored.VerifySignature());

		byte[]? recovered = restored.SigningAccount();
		Assert.NotNull(recovered);
		Assert.Equal(signer.PublicKey, Convert.ToHexString(recovered!), ignoreCase: true);
	}

	[Fact]
	public void GrantAndRevokeCyclePrincipals()
	{
		using var runtime = WasmRuntime.Load();
		using Account owner = Account.FromSeed(runtime, TestSeeds.Subject, 0, "ecdsa_secp256k1");
		using Account reader = Account.FromSeed(runtime, TestSeeds.Recipient, 0, "ecdsa_secp256k1");
		using EncryptedContainer container =
			EncryptedContainer.FromPlaintext(runtime, Payload, new[] { owner }, locked: false);

		container.GrantAccess(new[] { reader });
		Assert.Equal(2, container.Principals().Count);

		byte[] readerKey = Convert.FromHexString(reader.PublicKey);
		container.RevokeAccess(readerKey);
		Assert.Single(container.Principals());
	}
}
