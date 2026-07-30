using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KeetaNet.Anchor.Crypto;
using Xunit;

namespace KeetaNet.Anchor.E2eTests;

/// <summary>
/// Cross-implementation encrypted-container interop. The C#-to-TypeScript
/// leg is quarantined: the reference's zlib output differs byte-for-byte from
/// the core's, and the detached signature covers the compressed payload, so
/// the reference never validates a C#-produced signature.
/// </summary>
public sealed class ContainerInteropTests
{
	private const string TsAlgorithm = "secp256k1";
	private static readonly byte[] Payload = Encoding.UTF8.GetBytes("cross-implementation container payload");

	[Fact]
	public void CsharpDecryptsAndVerifiesTheTypescriptContainer()
	{
		using var harness = NodeHarness.Spawn("container");
		var encodeArguments = new JsonObject
		{
			["plaintext"] = Convert.ToBase64String(Payload),
			["principalSeeds"] = new JsonArray(E2eSeeds.Subject),
			["principalAlgorithm"] = TsAlgorithm,
			["signerSeed"] = E2eSeeds.Issuer,
			["signerAlgorithm"] = TsAlgorithm,
		};
		JsonElement encoded = harness.Request("encodeEncrypted", encodeArguments);
		byte[] container = Convert.FromBase64String(encoded.GetProperty("encoded").GetString()!);

		harness.Shutdown();

		using var runtime = WasmRuntime.Load();
		using Account principal = runtime.Accounts.FromSeed(E2eSeeds.Subject, 0, E2eSeeds.Secp256k1);
		using Account tsSigner = runtime.Accounts.FromSeed(E2eSeeds.Issuer, 0, E2eSeeds.Secp256k1);
		using EncryptedContainer opened = runtime.Containers.FromEncrypted(container, new[] { principal });

		Assert.Equal(Payload, opened.GetPlaintext());
		Assert.True(opened.IsEncrypted);
		Assert.True(opened.IsSigned);
		Assert.True(opened.VerifySignature());

		byte[]? recoveredSigner = opened.GetSigningAccount();
		Assert.NotNull(recoveredSigner);
		Assert.Equal(tsSigner.PublicKeyAndType, Convert.ToHexString(recoveredSigner!), ignoreCase: true);
	}

	[Fact]
	public void TypescriptDecryptsAndVerifiesTheCsharpContainer()
	{
		using var runtime = WasmRuntime.Load();
		using Account principal = runtime.Accounts.FromSeed(E2eSeeds.Subject, 0, E2eSeeds.Secp256k1);
		using Account signer = runtime.Accounts.FromSeed(E2eSeeds.Issuer, 0, E2eSeeds.Secp256k1);
		using EncryptedContainer container = runtime.Containers.FromPlaintext(Payload, new[] { principal }, locked: false, signer: signer);
		byte[] encoded = container.GetEncoded();

		using var harness = NodeHarness.Spawn("container");
		var decodeArguments = new JsonObject
		{
			["encoded"] = Convert.ToBase64String(encoded),
			["principalSeeds"] = new JsonArray(E2eSeeds.Subject),
			["principalAlgorithm"] = TsAlgorithm,
		};
		JsonElement decoded = harness.Request("decode", decodeArguments);

		harness.Shutdown();

		Assert.Equal(Convert.ToBase64String(Payload), decoded.GetProperty("plaintext").GetString());
		Assert.True(decoded.GetProperty("encrypted").GetBoolean());
		Assert.True(decoded.GetProperty("isSigned").GetBoolean());
		Assert.True(decoded.GetProperty("signatureValid").GetBoolean());
		Assert.Equal(
			signer.PublicKeyAndType,
			decoded.GetProperty("signerPublicKey").GetString(),
			ignoreCase: true);
	}
}
