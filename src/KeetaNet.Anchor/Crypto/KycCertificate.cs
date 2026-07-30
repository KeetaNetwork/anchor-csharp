using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// A proof attesting to a sensitive attribute's committed value. It validates
/// against the certificate with only the subject's public key, so a holder can
/// disclose a single attribute without revealing the private key.
/// </summary>
public sealed record AttributeProof(string Value, string Salt);

/// <summary>
/// One external blob reference discovered in an attribute's decoded value: the
/// carrying <see cref="Attribute"/> name, the uppercase-hex digest <see cref="Id"/>
/// keying the blob, where the stored blob lives (<see cref="Url"/>,
/// <see cref="ContentType"/>), and the digest/encryption algorithms as
/// symbolic names.
/// </summary>
public sealed record AttributeReference(
	string Attribute,
	string Id,
	string Url,
	string ContentType,
	string DigestAlgorithm,
	string EncryptionAlgorithm);

/// <summary>
/// A KYC leaf certificate: a base certificate plus parsed KYC attributes, some
/// plain and some encrypted to the subject.
/// </summary>
public sealed class KycCertificate : WasmObject
{
	private KycCertificate(WasmRuntime runtime, int handle)
		: base(runtime, handle)
	{
	}

	/// <summary>Adopt an existing core-module leaf handle.</summary>
	internal static KycCertificate Adopt(WasmRuntime runtime, int handle) => new(runtime, handle);

	/// <summary>The PEM encoding of the certificate.</summary>
	public string ToPem() => Runtime.KycCertificatePem(Handle);

	/// <summary>The base certificate, as an independently owned certificate object.</summary>
	public Certificate Base()
	{
		int handle = Runtime.KycCertificateBase(Handle);
		return new(Runtime, handle);
	}

	/// <summary>
	/// The leaf as the native .NET X.509 type, through its base certificate.
	/// See <see cref="Certificate.ToX509Certificate"/> for the trust caveat.
	/// </summary>
	public System.Security.Cryptography.X509Certificates.X509Certificate2 ToX509Certificate()
	{
		using Certificate baseCertificate = Base();
		return baseCertificate.ToX509Certificate();
	}

	/// <summary>Whether the certificate is valid at <paramref name="moment"/>.</summary>
	public bool IsValidAt(DateTimeOffset moment)
	{
		long unixMillis = moment.ToUnixTimeMilliseconds();
		return Runtime.KycCertificateValidAt(Handle, unixMillis);
	}

	/// <summary>
	/// Whether the certificate chains to one of <paramref name="trustedRoots"/> at
	/// <paramref name="moment"/>, using <paramref name="intermediates"/> to bridge the path.
	/// </summary>
	public bool Verify(
		IEnumerable<Certificate> trustedRoots,
		IEnumerable<Certificate> intermediates,
		DateTimeOffset moment)
	{
		int[] roots = Handles.Of(trustedRoots);
		int[] bridges = Handles.Of(intermediates);
		long unixMillis = moment.ToUnixTimeMilliseconds();

		return Runtime.KycCertificateVerify(Handle, roots, bridges, unixMillis);
	}

	/// <summary>The names of the KYC attributes the certificate carries.</summary>
	public IReadOnlyList<string> GetAttributeNames()
	{
		byte[] payload = Runtime.KycCertificateAttributes(Handle);
		IReadOnlyList<AttributeEntry> entries = KeetaJson.ReadList<AttributeEntry>(payload);

		return entries.Select(entry => entry.Name).ToList();
	}

	/// <summary>The undecoded semantic bytes of plain (unencrypted) attribute <paramref name="name"/>.</summary>
	public byte[] GetAttributeBuffer(string name) => Runtime.KycCertificatePlainAttribute(Handle, name);

	/// <summary>
	/// The undecoded semantic bytes of sensitive attribute <paramref name="name"/>,
	/// decrypted with <paramref name="subject"/>.
	/// </summary>
	public byte[] GetAttributeBuffer(string name, Account subject) =>
		Runtime.KycCertificateDecryptAttribute(Handle, name, subject.Handle);

	/// <summary>Plain attribute <paramref name="name"/> as a typed value box.</summary>
	public KycAttributeValue GetAttribute(string name)
	{
		byte[] buffer = GetAttributeBuffer(name);
		return new(name, buffer);
	}

	/// <summary>
	/// Sensitive attribute <paramref name="name"/>, decrypted with
	/// <paramref name="subject"/>, as a typed value box.
	/// </summary>
	public KycAttributeValue GetAttribute(string name, Account subject)
	{
		byte[] buffer = GetAttributeBuffer(name, subject);
		return new(name, buffer);
	}

	/// <summary>
	/// The external blob references carried by the <paramref name="names"/>
	/// attributes, discovered with <paramref name="subject"/> (sensitive values
	/// are decrypted to walk them), one record per reference.
	/// </summary>
	public IReadOnlyList<AttributeReference> GetExternalReferences(Account subject, IEnumerable<string> names)
	{
		string labels = JsonSerializer.Serialize(names.ToArray());
		byte[] payload = Runtime.KycCertificateExternalReferences(Handle, subject.Handle, labels);

		return KeetaJson.ReadList<AttributeReference>(payload);
	}

	/// <summary>
	/// A proof of sensitive attribute <paramref name="name"/>, decrypted with
	/// <paramref name="subject"/>. The proof validates against this certificate
	/// without the private key, for selective disclosure.
	/// </summary>
	public AttributeProof GetProof(string name, Account subject)
	{
		byte[] payload = Runtime.KycCertificateProve(Handle, name, subject.Handle);
		return JsonSerializer.Deserialize<AttributeProof>(payload, KeetaJson.Options) ?? throw new KeetaException("PROOF", "the proof payload was empty");
	}

	/// <summary>
	/// Whether <paramref name="proof"/> attests to sensitive attribute
	/// <paramref name="name"/>, validated with <paramref name="subject"/>'s public key.
	/// </summary>
	public bool ValidateProof(string name, Account subject, AttributeProof proof)
	{
		string proofJson = JsonSerializer.Serialize(proof, KeetaJson.Options);
		return Runtime.KycCertificateValidateProof(Handle, name, subject.Handle, proofJson);
	}

	private protected override void Release(WasmRuntime runtime, int handle) => runtime.KycCertificateFree(handle);

	/// <summary>The core's attribute-list transport shape.</summary>
	private sealed record AttributeEntry(string Name, bool Sensitive);
}

/// <summary>
/// A fluent builder for a KYC leaf certificate: collect a subject, issuer,
/// validity window, and attributes, then <see cref="Build"/> the signed
/// leaf. Sensitive attributes are encrypted to the subject. The issuer
/// signs. The subject and issuer may use different signing algorithms.
/// </summary>
public sealed class KycCertificateBuilder
{
	private readonly WasmRuntime _runtime;
	private readonly List<IssueAttributeDto> _attributes = new();
	private Account? _subject;
	private Account? _issuer;
	private string? _subjectName;
	private string? _issuerName;
	private ulong _serial = 1;
	private (DateTimeOffset NotBefore, DateTimeOffset NotAfter)? _validity;
	private bool _isCertificateAuthority;

	internal KycCertificateBuilder(WasmRuntime runtime) => _runtime = runtime;

	/// <summary>The subject the leaf is issued to. Sensitive attributes encrypt to its key.</summary>
	/// <remarks>A read-only (public-key) account suffices to issue.</remarks>
	public KycCertificateBuilder Subject(Account subject)
	{
		_subject = subject;
		return this;
	}

	/// <summary>The issuer that signs the leaf.</summary>
	public KycCertificateBuilder Issuer(Account issuer)
	{
		_issuer = issuer;
		return this;
	}

	/// <summary>The subject distinguished-name common name (defaults to the subject's public-key string).</summary>
	public KycCertificateBuilder SubjectName(string name)
	{
		_subjectName = name;
		return this;
	}

	/// <summary>The issuer distinguished-name common name (defaults to the issuer's public-key string).</summary>
	public KycCertificateBuilder IssuerName(string name)
	{
		_issuerName = name;
		return this;
	}

	/// <summary>The certificate serial number (defaults to <c>1</c>).</summary>
	public KycCertificateBuilder Serial(ulong serial)
	{
		_serial = serial;
		return this;
	}

	/// <summary>The validity window. Required, since a component has no clock.</summary>
	public KycCertificateBuilder Validity(DateTimeOffset notBefore, DateTimeOffset notAfter)
	{
		_validity = (notBefore, notAfter);
		return this;
	}

	/// <summary>Whether the leaf is a certificate authority (defaults to <c>false</c>).</summary>
	public KycCertificateBuilder AsCertificateAuthority(bool isCertificateAuthority = true)
	{
		_isCertificateAuthority = isCertificateAuthority;
		return this;
	}

	/// <summary>Set a scalar text attribute by <paramref name="name"/>.</summary>
	public KycCertificateBuilder SetAttribute(string name, bool sensitive, string value)
	{
		byte[] encoded = Encoding.UTF8.GetBytes(value);
		return SetAttribute(name, sensitive, encoded);
	}

	/// <summary>Set a date attribute, encoded as an RFC-3339 timestamp.</summary>
	public KycCertificateBuilder SetAttribute(string name, bool sensitive, DateTimeOffset value)
	{
		DateTimeOffset utc = value.ToUniversalTime();
		string timestamp = utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
		byte[] encoded = Encoding.UTF8.GetBytes(timestamp);

		return SetAttribute(name, sensitive, encoded);
	}

	/// <summary>Set a structured attribute from its JSON value (camelCase fields).</summary>
	public KycCertificateBuilder SetAttribute(string name, bool sensitive, JsonElement value)
	{
		string json = value.GetRawText();
		byte[] encoded = Encoding.UTF8.GetBytes(json);

		return SetAttribute(name, sensitive, encoded);
	}

	/// <summary>Set an attribute from its already-encoded semantic <paramref name="value"/> bytes.</summary>
	public KycCertificateBuilder SetAttribute(string name, bool sensitive, byte[] value)
	{
		int[] transport = Array.ConvertAll(value, b => (int)b);
		_attributes.Add(new IssueAttributeDto(name, sensitive, transport));

		return this;
	}

	/// <summary>Build (issue) the signed leaf certificate.</summary>
	public KycCertificate Build()
	{
		Account subject = _subject ?? throw new InvalidOperationException("a subject account is required to issue a certificate");
		Account issuer = _issuer ?? throw new InvalidOperationException("an issuer account is required to issue a certificate");
		(DateTimeOffset notBefore, DateTimeOffset notAfter) = _validity ?? throw new InvalidOperationException("a validity window is required to issue a certificate");

		var parameters = new IssueParamsDto(
			_subjectName ?? subject.PublicKeyString,
			_issuerName ?? issuer.PublicKeyString,
			_serial,
			notBefore.ToUnixTimeSeconds(),
			notAfter.ToUnixTimeSeconds(),
			_isCertificateAuthority,
			_attributes);

		string json = JsonSerializer.Serialize(parameters, KeetaJson.Options);
		int handle = _runtime.KycCertificateIssue(subject.Handle, issuer.Handle, json);

		return KycCertificate.Adopt(_runtime, handle);
	}

	// The issuance transport shape the P1 core decodes. `value` is a number array
	// (not base64) so it deserializes into the core's `Vec<u8>`.
	private sealed record IssueAttributeDto(string Name, bool Sensitive, int[] Value);

	private sealed record IssueParamsDto(
		string SubjectDn,
		string IssuerDn,
		ulong Serial,
		long NotBefore,
		long NotAfter,
		bool IsCa,
		IReadOnlyList<IssueAttributeDto> Attributes);
}
