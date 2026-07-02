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
		using EncryptedContainer plain = runtime.Containers.FromPlaintext(Payload);

		byte[] encoded = plain.GetEncoded();
		using EncryptedContainer restored = runtime.Containers.FromEncoded(encoded);
		Assert.Equal(Payload, restored.GetPlaintext());
		Assert.False(restored.IsEncrypted);
	}

	[Theory]
	[MemberData(nameof(TestSeeds.Algorithms), MemberType = typeof(TestSeeds))]
	public void PrincipalDecryptsTheSealedPayload(string algorithm)
	{
		using var runtime = WasmRuntime.Load();
		using Account owner = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, algorithm);

		byte[] encoded;
		using (EncryptedContainer encrypted =
			runtime.Containers.FromPlaintext(Payload, new[] { owner }, locked: false))
		{
			Assert.True(encrypted.IsEncrypted);
			encoded = encrypted.GetEncoded();
		}

		using EncryptedContainer opened = runtime.Containers.FromEncrypted(encoded, new[] { owner });
		Assert.Equal(Payload, opened.GetPlaintext());
		Assert.True(opened.IsEncrypted);
	}

	[Theory]
	[MemberData(nameof(TestSeeds.Algorithms), MemberType = typeof(TestSeeds))]
	public void SignedContainerVerifiesAndRecoversItsSigner(string algorithm)
	{
		using var runtime = WasmRuntime.Load();
		using Account signer = runtime.Accounts.FromSeed(TestSeeds.Issuer, 0, algorithm);

		byte[] encoded;
		using (EncryptedContainer signed =
			runtime.Containers.FromPlaintext(Payload, locked: false, signer: signer))
		{
			encoded = signed.GetEncoded();
		}

		using EncryptedContainer restored = runtime.Containers.FromEncoded(encoded);
		Assert.True(restored.IsSigned);
		Assert.True(restored.VerifySignature());

		byte[]? recovered = restored.GetSigningAccount();
		Assert.NotNull(recovered);
		Assert.Equal(signer.PublicKey, Convert.ToHexString(recovered!), ignoreCase: true);
	}

	[Fact]
	public void GrantAndRevokeCyclePrincipals()
	{
		using var runtime = WasmRuntime.Load();
		using Account owner = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, "ecdsa_secp256k1");
		using Account reader = runtime.Accounts.FromSeed(TestSeeds.Recipient, 0, "ecdsa_secp256k1");
		using EncryptedContainer container =
			runtime.Containers.FromPlaintext(Payload, new[] { owner }, locked: false);

		container.GrantAccess(new[] { reader });
		Assert.Equal(2, container.GetPrincipals().Count);

		byte[] readerKey = Convert.FromHexString(reader.PublicKey);
		container.RevokeAccess(readerKey);
		Assert.Single(container.GetPrincipals());
	}
}
