using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KeetaNet.Anchor.Crypto;

/// <summary>One KYC attribute: its OID <see cref="Name"/> and whether its value is encrypted.</summary>
public sealed record KycAttribute(string Name, bool Sensitive);

/// <summary>
/// A proof attesting to a sensitive attribute's committed value. It validates
/// against the certificate with only the subject's public key, so a holder can
/// disclose a single attribute without revealing the private key.
/// </summary>
public sealed record AttributeProof(string Value, string Salt);

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

	/// <summary>Parse a PEM-encoded KYC certificate.</summary>
	public static KycCertificate Parse(WasmRuntime runtime, string pem)
	{
		int handle = runtime.KycCertificateParse(pem);
		return new(runtime, handle);
	}

	/// <summary>Begin issuing a new KYC leaf certificate under <paramref name="runtime"/>.</summary>
	public static KycCertificateBuilder Builder(WasmRuntime runtime) => new(runtime);

	/// <summary>The PEM encoding of the certificate.</summary>
	public string Pem() => Runtime.KycCertificatePem(Handle);

	/// <summary>The base certificate, as an independently owned certificate object.</summary>
	public Certificate Base()
	{
		int handle = Runtime.KycCertificateBase(Handle);
		return new(Runtime, handle);
	}

	/// <summary>Whether the certificate is valid at <paramref name="moment"/>.</summary>
	public bool ValidAt(DateTimeOffset moment)
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

	/// <summary>The KYC attributes the certificate carries.</summary>
	public IReadOnlyList<KycAttribute> Attributes()
	{
		byte[] payload = Runtime.KycCertificateAttributes(Handle);
		return KeetaJson.ReadList<KycAttribute>(payload);
	}

	/// <summary>A plain (unencrypted) attribute by <paramref name="name"/>.</summary>
	public byte[] PlainAttribute(string name) => Runtime.KycCertificatePlainAttribute(Handle, name);

	/// <summary>Decrypt a sensitive attribute by <paramref name="name"/> using <paramref name="subject"/>.</summary>
	public byte[] DecryptAttribute(string name, Account subject) =>
		Runtime.KycCertificateDecryptAttribute(Handle, name, subject.Handle);

	/// <summary>
	/// Prove sensitive attribute <paramref name="name"/>, decrypting it with
	/// <paramref name="subject"/>. The returned proof validates against this
	/// certificate without the private key, for selective disclosure.
	/// </summary>
	public AttributeProof Prove(string name, Account subject)
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

	/// <summary>
	/// A plain scalar attribute decoded as text. Scalar and date attributes
	/// decode to a UTF-8 string (dates as an ISO-8601 timestamp).
	/// </summary>
	public string GetText(string name)
	{
		byte[] value = PlainAttribute(name);
		return Encoding.UTF8.GetString(value);
	}

	/// <summary>
	/// A sensitive scalar attribute decrypted with <paramref name="subject"/> and
	/// decoded as text (dates as an ISO-8601 timestamp).
	/// </summary>
	public string GetText(string name, Account subject)
	{
		byte[] value = DecryptAttribute(name, subject);
		return Encoding.UTF8.GetString(value);
	}

	/// <summary>
	/// A plain structured attribute decoded as JSON. Structured attributes
	/// (e.g. address, entity type) decode to a JSON object or array matching the
	/// TypeScript client's value shape.
	/// </summary>
	public JsonElement GetJson(string name)
	{
		byte[] value = PlainAttribute(name);
		return ParseJson(value);
	}

	/// <summary>
	/// A sensitive structured attribute decrypted with <paramref name="subject"/>
	/// and decoded as JSON.
	/// </summary>
	public JsonElement GetJson(string name, Account subject)
	{
		byte[] value = DecryptAttribute(name, subject);
		return ParseJson(value);
	}

	private static JsonElement ParseJson(byte[] payload)
	{
		using var document = JsonDocument.Parse(payload);
		return document.RootElement.Clone();
	}

	private protected override void Release(WasmRuntime runtime, int handle) => runtime.KycCertificateFree(handle);
}

/// <summary>
/// A fluent builder for a KYC leaf certificate: collect a subject, issuer,
/// validity window, and attributes, then <see cref="Issue"/> the signed
/// leaf. Sensitive attributes are encrypted to the subject; the issuer
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
	private DateTimeOffset? _notBefore;
	private DateTimeOffset? _notAfter;
	private bool _isCertificateAuthority;

	internal KycCertificateBuilder(WasmRuntime runtime) => _runtime = runtime;

	/// <summary>The subject the leaf is issued to; sensitive attributes encrypt to its key.</summary>
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

	/// <summary>The subject distinguished-name common name (defaults to the subject's address).</summary>
	public KycCertificateBuilder SubjectName(string name)
	{
		_subjectName = name;
		return this;
	}

	/// <summary>The issuer distinguished-name common name (defaults to the issuer's address).</summary>
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
		_notBefore = notBefore;
		_notAfter = notAfter;

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

	/// <summary>Issue the signed leaf certificate.</summary>
	public KycCertificate Issue()
	{
		Account subject = _subject ?? throw new InvalidOperationException("a subject account is required to issue a certificate");
		Account issuer = _issuer ?? throw new InvalidOperationException("an issuer account is required to issue a certificate");
		long notBefore = _notBefore?.ToUnixTimeSeconds() ?? throw new InvalidOperationException("a validity window is required to issue a certificate");
		long notAfter = _notAfter?.ToUnixTimeSeconds() ?? throw new InvalidOperationException("a validity window is required to issue a certificate");

		var parameters = new IssueParamsDto(
			_subjectName ?? subject.Address,
			_issuerName ?? issuer.Address,
			_serial,
			notBefore,
			notAfter,
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
