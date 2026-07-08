using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KeetaNet.Anchor.Crypto;
using Xunit;

namespace KeetaNet.Anchor.E2eTests;

/// <summary>
/// External blob references across implementations: a bundle built by one side
/// with the referenced blob decrypted, digest-verified, and inlined must yield
/// the exact plaintext to a reader on the other side.
/// </summary>
public sealed class SharableReferenceTests : IDisposable
{
	/// <summary>The attribute carrying the external reference.</summary>
	private const string License = "documentDriversLicense";

	/// <summary>The referenced blob's plaintext, digest-certified by the reference.</summary>
	private static readonly byte[] BlobPlaintext = Encoding.UTF8.GetBytes("NOT REALLY A PNG");

	/// <summary>The reference id: the uppercase-hex SHA3-256 digest of the plaintext.</summary>
	private const string ReferenceId =
		"6DD92D3B9D488B3C09660664F4B2C5DE3830526EEB96E5BA7D47C70AFB893CBB";

	private readonly WasmRuntime _runtime = WasmRuntime.Load();
	private readonly Account _subject;
	private readonly Account _issuer;
	private readonly Account _recipient;

	public SharableReferenceTests()
	{
		_subject = _runtime.Accounts.FromSeed(E2eSeeds.Subject, 0, E2eSeeds.Secp256k1);
		_issuer = _runtime.Accounts.FromSeed(E2eSeeds.Issuer, 0, E2eSeeds.Secp256k1);
		_recipient = _runtime.Accounts.FromSeed(E2eSeeds.Recipient, 0, E2eSeeds.Secp256k1);
	}

	public void Dispose()
	{
		_recipient.Dispose();
		_issuer.Dispose();
		_subject.Dispose();
		_runtime.Dispose();
	}

	[Fact]
	public void TypescriptResolvesTheCsharpInlinedBlob()
	{
		byte[] sealedBlob = SealToSubject();
		using KycCertificate leaf = IssueLicenseLeaf(DataUrl(sealedBlob));

		// The discovery walk decrypts the sensitive value and reports the
		// reference the fetch layer would retrieve.
		AttributeReference reference = Assert.Single(leaf.GetExternalReferences(_subject, new[] { License }));
		Assert.Equal(License, reference.Attribute);
		Assert.Equal(ReferenceId, reference.Id);
		Assert.Equal("image/png", reference.ContentType);

		var blobs = new Dictionary<string, byte[]> { [reference.Id] = sealedBlob };
		using SharableCertificateAttributes bundle =
			_runtime.Sharables.FromCertificate(leaf, _subject, blobs, names: new[] { License });
		Assert.Equal(BlobPlaintext, bundle.GetReferenceBlob(License, ReferenceId));

		bundle.GrantAccess(new[] { _recipient });

		// The reference reader resolves `$blob` on the C#-built bundle and
		// hash-verifies the payload itself (it throws on a mismatch).
		using var harness = NodeHarness.Spawn("sharable");
		var arguments = new JsonObject
		{
			["pem"] = bundle.ToPem(),
			["recipientSeed"] = E2eSeeds.Recipient,
			["attributes"] = IssueAttributes.NameArray(new[] { License }),
		};
		JsonElement opened = harness.Request("openSharable", arguments);

		harness.Shutdown();

		string resolved = opened
			.GetProperty("blobs")
			.GetProperty(License)
			.GetProperty(ReferenceId)
			.GetProperty("data")
			.GetString()!;
		Assert.Equal(BlobPlaintext, Convert.FromBase64String(resolved));
	}

	[Fact]
	public void CsharpReadsTheTypescriptInlinedBlob()
	{
		byte[] sealedBlob = SealToSubject();

		// The reference builder walks `$blob`, fetches the data: URL, decrypts,
		// and in-lines the verified payload into the bundle it exports.
		using var harness = NodeHarness.Spawn("sharable");
		var attributes = new JsonArray
		{
			new JsonObject
			{
				["name"] = License,
				["sensitive"] = true,
				["value"] = LicenseValue(DataUrl(sealedBlob)),
			},
		};
		var arguments = new JsonObject
		{
			["subjectSeed"] = E2eSeeds.Subject,
			["recipientSeed"] = E2eSeeds.Recipient,
			["attributes"] = attributes,
		};
		JsonElement built = harness.Request("buildSharable", arguments);
		string pem = built.GetProperty("pem").GetString()!;

		harness.Shutdown();

		using SharableCertificateAttributes opened = _runtime.Sharables.FromPem(pem, new[] { _recipient });
		Assert.Equal(BlobPlaintext, opened.GetReferenceBlob(License, ReferenceId));
		Assert.Null(opened.GetReferenceBlob(License, new string('0', 64)));
	}

	[Fact]
	public async Task ADataUrlDecodesWithoutTheNetwork()
	{
		CancellationToken cancellationToken = TestContext.Current.CancellationToken;
		AttributeReference reference = LicenseReference(DataUrl(BlobPlaintext));

		using var httpClient = new HttpClient();
		IReadOnlyDictionary<string, byte[]> blobs =
			await ExternalReferences.FetchBlobs(httpClient, new[] { reference }, cancellationToken);

		Assert.Equal(BlobPlaintext, blobs[ReferenceId]);
	}

	[Fact]
	public async Task AWrappedHttpBlobBuildsInOneCall()
	{
		CancellationToken cancellationToken = TestContext.Current.CancellationToken;

		// The harness serves the sealed blob wrapped in the storage-service
		// {data, mimeType} JSON convention. The fetch layer must unwrap it.
		byte[] sealedBlob = SealToSubject();
		using var harness = NodeHarness.Spawn("sharable");
		string url = ServeBlob(harness, sealedBlob, wrap: true);

		using KycCertificate leaf = IssueLicenseLeaf(url);
		using var httpClient = new HttpClient();
		using SharableCertificateAttributes bundle = await _runtime.Sharables.FromCertificate(
			leaf, _subject, httpClient, names: new[] { License }, cancellationToken: cancellationToken);

		harness.Shutdown();

		Assert.Equal(BlobPlaintext, bundle.GetReferenceBlob(License, ReferenceId));
	}

	[Fact]
	public async Task ARawHttpBlobPassesThroughTheFetchHelper()
	{
		CancellationToken cancellationToken = TestContext.Current.CancellationToken;

		// The harness serves the sealed blob raw (no JSON wrapper). The fetch
		// layer must pass the body through untouched, still sealed.
		byte[] sealedBlob = SealToSubject();
		using var harness = NodeHarness.Spawn("sharable");
		string url = ServeBlob(harness, sealedBlob, wrap: false);

		using KycCertificate leaf = IssueLicenseLeaf(url);
		AttributeReference reference = Assert.Single(leaf.GetExternalReferences(_subject, new[] { License }));

		using var httpClient = new HttpClient();
		IReadOnlyDictionary<string, byte[]> blobs =
			await ExternalReferences.FetchBlobs(httpClient, new[] { reference }, cancellationToken);

		harness.Shutdown();

		Assert.Equal(sealedBlob, blobs[reference.Id]);

		using SharableCertificateAttributes bundle =
			_runtime.Sharables.FromCertificate(leaf, _subject, blobs, names: new[] { License });
		Assert.Equal(BlobPlaintext, bundle.GetReferenceBlob(License, ReferenceId));
	}

	[Fact]
	public async Task AMissingBlobUrlFailsTheFetchLoud()
	{
		CancellationToken cancellationToken = TestContext.Current.CancellationToken;
		using var harness = NodeHarness.Spawn("sharable");
		string served = ServeBlob(harness, BlobPlaintext, wrap: false);
		AttributeReference reference = LicenseReference(served + "-nope");

		using var httpClient = new HttpClient();
		KeetaException failure = await Assert.ThrowsAsync<KeetaException>(
			() => ExternalReferences.FetchBlobs(httpClient, new[] { reference }, cancellationToken));

		harness.Shutdown();

		Assert.Equal("REFERENCE_FETCH", failure.Code);
	}

	[Fact]
	public void ACorruptedBlobFailsTheBuildLoud()
	{
		// The reference certifies the fixture plaintext but the supplied blob
		// seals different content, so decryption succeeds and the digest check fails.
		byte[] corrupted = Seal(Encoding.UTF8.GetBytes("tampered content"));
		using KycCertificate leaf = IssueLicenseLeaf(DataUrl(corrupted));
		var blobs = new Dictionary<string, byte[]> { [ReferenceId] = corrupted };

		KeetaException failure = Assert.Throws<KeetaException>(() =>
		{
			using SharableCertificateAttributes bundle =
				_runtime.Sharables.FromCertificate(leaf, _subject, blobs, names: new[] { License });
		});
		Assert.Equal("REFERENCE_DIGEST_MISMATCH", failure.Code);
	}

	/// <summary>The license reference as the discovery walk reports it, at <paramref name="url"/>.</summary>
	private static AttributeReference LicenseReference(string url) =>
		new(License, ReferenceId, url, "image/png", "sha3-256", "KeetaEncryptedContainerV1");

	/// <summary>Serve <paramref name="stored"/> from the harness blob store, returning its URL.</summary>
	private static string ServeBlob(NodeHarness harness, byte[] stored, bool wrap)
	{
		var arguments = new JsonObject
		{
			["data"] = Convert.ToBase64String(stored),
			["mimeType"] = "image/png",
			["wrap"] = wrap,
		};
		JsonElement served = harness.Request("serveBlob", arguments);

		return served.GetProperty("url").GetString()!;
	}

	/// <summary>Seal the blob plaintext to the subject, the raw stored form.</summary>
	private byte[] SealToSubject() => Seal(BlobPlaintext);

	/// <summary>Seal <paramref name="plaintext"/> to the subject as a locked container.</summary>
	private byte[] Seal(byte[] plaintext)
	{
		using EncryptedContainer container =
			_runtime.Containers.FromPlaintext(plaintext, new[] { _subject }, locked: true);

		return container.GetEncoded();
	}

	/// <summary>A <c>data:</c> URL carrying <paramref name="bytes"/> base64-inline.</summary>
	private static string DataUrl(byte[] bytes) =>
		"data:application/octet-string;base64," + Convert.ToBase64String(bytes);

	/// <summary>
	/// The drivers-license attribute value referencing <paramref name="url"/>
	/// with a digest certifying the blob plaintext, in the shape both
	/// implementations decode.
	/// </summary>
	private static JsonObject LicenseValue(string url)
	{
		var digestBytes = new JsonArray();
		foreach (byte value in Convert.FromHexString(ReferenceId))
		{
			digestBytes.Add(value);
		}

		return new JsonObject
		{
			["documentNumber"] = "DL-7",
			["front"] = new JsonObject
			{
				["external"] = new JsonObject
				{
					["url"] = url,
					["contentType"] = "image/png",
				},
				["digest"] = new JsonObject
				{
					["digestAlgorithm"] = "sha3-256",
					["digest"] = new JsonObject
					{
						["type"] = "Buffer",
						["data"] = digestBytes,
					},
				},
				["encryptionAlgorithm"] = "1.3.6.1.4.1.62675.2",
			},
		};
	}

	/// <summary>Issue a leaf for the subject carrying the sensitive license value at <paramref name="url"/>.</summary>
	private KycCertificate IssueLicenseLeaf(string url)
	{
		JsonObject value = LicenseValue(url);
		var attribute = new AttributeCase(
			License, Encoding.UTF8.GetBytes(value.ToJsonString()), Sensitive: true, value);

		return LocalLeaf.Issue(_runtime, _subject, _issuer, new[] { attribute });
	}
}
