using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KeetaNet.Anchor.Crypto;

/// <summary>One KYC attribute: its OID <see cref="Name"/> and whether its value is encrypted.</summary>
[SuppressMessage(
	"Naming",
	"CA1711:Identifiers should not have incorrect suffix",
	Justification = "A KYC certificate attribute is the domain term, matching the TypeScript client; it is not a System.Attribute.")]
public sealed record KycAttribute(string Name, bool Sensitive);

/// <summary>
/// A proof attesting to a sensitive attribute's committed value. It validates
/// against the certificate with only the subject's public key, so a holder can
/// disclose a single attribute without revealing the private key. <see cref="Value"/>
/// is the base64 attribute value revealed; <see cref="Salt"/> its base64 commitment salt.
/// </summary>
public sealed record AttributeProof(string Value, string Salt);

/// <summary>
/// A KYC leaf certificate: a base certificate plus parsed KYC attributes, some
/// plain and some encrypted to the subject.
/// </summary>
public sealed class KycCertificate : IDisposable
{
	private static readonly JsonSerializerOptions Json = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	private readonly WasmRuntime _runtime;
	private bool _disposed;

	/// <summary>The core-module handle backing this certificate.</summary>
	internal int Handle { get; }

	private KycCertificate(WasmRuntime runtime, int handle)
	{
		_runtime = runtime;
		Handle = handle;
	}

	/// <summary>Adopt an existing core-module leaf handle.</summary>
	internal static KycCertificate Adopt(WasmRuntime runtime, int handle) => new(runtime, handle);

	/// <summary>Parse a PEM-encoded KYC certificate.</summary>
	public static KycCertificate Parse(WasmRuntime runtime, string pem) =>
		new(runtime, runtime.KycCertificateParse(pem));

	/// <summary>Begin issuing a new KYC leaf certificate under <paramref name="runtime"/>.</summary>
	public static KycCertificateBuilder Builder(WasmRuntime runtime) => new(runtime);

	/// <summary>The PEM encoding of the certificate.</summary>
	public string Pem() => _runtime.KycCertificatePem(Handle);

	/// <summary>The base certificate, as an independently owned certificate object.</summary>
	public Certificate Base() => new(_runtime, _runtime.KycCertificateBase(Handle));

	/// <summary>Whether the certificate is valid at <paramref name="moment"/>.</summary>
	public bool ValidAt(DateTimeOffset moment) => _runtime.KycCertificateValidAt(Handle, moment.ToUnixTimeMilliseconds());

	/// <summary>
	/// Whether the certificate chains to one of <paramref name="trustedRoots"/> at
	/// <paramref name="moment"/>, using <paramref name="intermediates"/> to bridge the path.
	/// </summary>
	public bool Verify(
		IEnumerable<Certificate> trustedRoots,
		IEnumerable<Certificate> intermediates,
		DateTimeOffset moment)
	{
		int[] roots = trustedRoots.Select(certificate => certificate.Handle).ToArray();
		int[] bridges = intermediates.Select(certificate => certificate.Handle).ToArray();
		return _runtime.KycCertificateVerify(Handle, roots, bridges, moment.ToUnixTimeMilliseconds());
	}

	/// <summary>The KYC attributes the certificate carries.</summary>
	public IReadOnlyList<KycAttribute> Attributes()
	{
		byte[] payload = _runtime.KycCertificateAttributes(Handle);
		return JsonSerializer.Deserialize<List<KycAttribute>>(payload, Json) ?? new List<KycAttribute>();
	}

	/// <summary>A plain (unencrypted) attribute by <paramref name="name"/>.</summary>
	public byte[] PlainAttribute(string name) => _runtime.KycCertificatePlainAttribute(Handle, name);

	/// <summary>Decrypt a sensitive attribute by <paramref name="name"/> using <paramref name="subject"/>.</summary>
	public byte[] DecryptAttribute(string name, Account subject) =>
		_runtime.KycCertificateDecryptAttribute(Handle, name, subject.Handle);

	/// <summary>
	/// Prove sensitive attribute <paramref name="name"/>, decrypting it with
	/// <paramref name="subject"/>. The returned proof validates against this
	/// certificate without the private key, for selective disclosure.
	/// </summary>
	public AttributeProof Prove(string name, Account subject)
	{
		byte[] payload = _runtime.KycCertificateProve(Handle, name, subject.Handle);
		return JsonSerializer.Deserialize<AttributeProof>(payload, Json)
			?? throw new KeetaException("PROOF", "the proof payload was empty");
	}

	/// <summary>
	/// Whether <paramref name="proof"/> attests to sensitive attribute
	/// <paramref name="name"/>, validated with <paramref name="subject"/>'s public key.
	/// </summary>
	public bool ValidateProof(string name, Account subject, AttributeProof proof) =>
		_runtime.KycCertificateValidateProof(Handle, name, subject.Handle, JsonSerializer.Serialize(proof, Json));

	/// <summary>
	/// A plain scalar attribute decoded as text. Scalar and date attributes
	/// decode to a UTF-8 string (dates as an ISO-8601 timestamp).
	/// </summary>
	public string GetText(string name) => Encoding.UTF8.GetString(PlainAttribute(name));

	/// <summary>
	/// A sensitive scalar attribute decrypted with <paramref name="subject"/> and
	/// decoded as text (dates as an ISO-8601 timestamp).
	/// </summary>
	public string GetText(string name, Account subject) =>
		Encoding.UTF8.GetString(DecryptAttribute(name, subject));

	/// <summary>
	/// A plain structured attribute decoded as JSON. Structured attributes
	/// (e.g. address, entity type) decode to a JSON object or array matching the
	/// TypeScript client's value shape.
	/// </summary>
	public JsonElement GetJson(string name) => ParseJson(PlainAttribute(name));

	/// <summary>
	/// A sensitive structured attribute decrypted with <paramref name="subject"/>
	/// and decoded as JSON.
	/// </summary>
	public JsonElement GetJson(string name, Account subject) =>
		ParseJson(DecryptAttribute(name, subject));

	private static JsonElement ParseJson(byte[] payload)
	{
		using var document = JsonDocument.Parse(payload);
		return document.RootElement.Clone();
	}

	/// <summary>Release the core-module certificate handle.</summary>
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_runtime.KycCertificateFree(Handle);
	}
}

/// <summary>
/// A fluent builder for a KYC leaf certificate: collect a subject, issuer,
/// validity window, and attributes, then <see cref="Issue"/> the signed
/// leaf. Sensitive attributes are encrypted to the subject; the issuer
/// signs. The subject and issuer may use different signing algorithms.
/// </summary>
public sealed class KycCertificateBuilder
{
	private static readonly JsonSerializerOptions Json = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

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
	public KycCertificateBuilder SetAttribute(string name, bool sensitive, string value) =>
		SetAttribute(name, sensitive, Encoding.UTF8.GetBytes(value));

	/// <summary>Set a date attribute, encoded as an RFC-3339 timestamp.</summary>
	public KycCertificateBuilder SetAttribute(string name, bool sensitive, DateTimeOffset value) =>
		SetAttribute(
			name,
			sensitive,
			Encoding.UTF8.GetBytes(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));

	/// <summary>Set a structured attribute from its JSON value (camelCase fields).</summary>
	public KycCertificateBuilder SetAttribute(string name, bool sensitive, JsonElement value) =>
		SetAttribute(name, sensitive, Encoding.UTF8.GetBytes(value.GetRawText()));

	/// <summary>Set an attribute from its already-encoded semantic <paramref name="value"/> bytes.</summary>
	public KycCertificateBuilder SetAttribute(string name, bool sensitive, byte[] value)
	{
		_attributes.Add(new IssueAttributeDto(name, sensitive, Array.ConvertAll(value, b => (int)b)));
		return this;
	}

	/// <summary>Issue the signed leaf certificate.</summary>
	public KycCertificate Issue()
	{
		Account subject = _subject ?? throw new InvalidOperationException("a subject account is required to issue a certificate");
		Account issuer = _issuer ?? throw new InvalidOperationException("an issuer account is required to issue a certificate");
		DateTimeOffset notBefore = _notBefore ?? throw new InvalidOperationException("a validity window is required to issue a certificate");
		DateTimeOffset notAfter = _notAfter ?? throw new InvalidOperationException("a validity window is required to issue a certificate");

		var parameters = new IssueParamsDto(
			_subjectName ?? subject.Address,
			_issuerName ?? issuer.Address,
			_serial,
			notBefore.ToUnixTimeSeconds(),
			notAfter.ToUnixTimeSeconds(),
			_isCertificateAuthority,
			_attributes);

		string json = JsonSerializer.Serialize(parameters, Json);
		return KycCertificate.Adopt(_runtime, _runtime.KycCertificateIssue(subject.Handle, issuer.Handle, json));
	}

	// The issuance transport form the P1 core decodes. `value` is a number array (not
	// base64) so it deserializes into the core's `Vec<u8>`.
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
