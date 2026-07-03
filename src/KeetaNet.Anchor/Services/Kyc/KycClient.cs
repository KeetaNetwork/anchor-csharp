using System.Text.Json;

namespace KeetaNet.Anchor;

/// <summary>
/// A KYC anchor client bound to a signer and a metadata root. Discovery, request
/// signing, retries, and polling all run inside the wasm core. The client is
/// thread-safe: operations serialize onto the runtime's dispatcher, and every
/// networked method honors its <see cref="CancellationToken"/> before dispatch
/// and during host HTTP and sleeps.
/// </summary>
public sealed class KycClient : WasmObject
{
	private KycClient(WasmRuntime runtime, int handle)
		: base(runtime, handle)
	{
	}

	/// <summary>
	/// Build a client signed by an existing <paramref name="account"/>, resolving
	/// providers from <paramref name="root"/>'s on-chain service metadata read via
	/// the node API at <paramref name="nodeUrl"/>. Reached through
	/// <see cref="WasmRuntime.CreateKycClient"/>.
	/// </summary>
	internal static KycClient WithAccount(WasmRuntime runtime, string nodeUrl, string root, Crypto.Account account)
	{
		int handle = runtime.KycWithAccount(nodeUrl, root, account.Handle);
		return new KycClient(runtime, handle);
	}

	/// <summary>Every provider that serves all <paramref name="countries"/> (ISO codes).</summary>
	public async Task<IReadOnlyList<KycProvider>> GetProvidersAsync(
		IEnumerable<string> countries,
		CancellationToken cancellationToken = default)
	{
		string countriesJson = SerializeCountries(countries);

		byte[] payload = await Runtime.KycProviders(Handle, countriesJson, cancellationToken).ConfigureAwait(false);
		return KeetaJson.ReadList<KycProvider>(payload);
	}

	/// <summary>
	/// Start a verification with <paramref name="provider"/> for
	/// <paramref name="countries"/>, optionally redirecting the user to
	/// <paramref name="redirect"/> when the flow ends.
	/// </summary>
	public async Task<VerificationOutcome> StartVerificationAsync(
		KycProvider provider,
		IEnumerable<string> countries,
		string? redirect = null,
		CancellationToken cancellationToken = default)
	{
		string providerJson = JsonSerializer.Serialize(provider, KeetaJson.Options);
		string countriesJson = SerializeCountries(countries);

		byte[] payload = await Runtime
			.KycCreateVerification(Handle, providerJson, countriesJson, redirect ?? "", cancellationToken)
			.ConfigureAwait(false);

		return ParseOutcome<Verification, VerificationOutcome>(payload, "verification", ready => new VerificationOutcome(ready, null), retry => new VerificationOutcome(null, retry));
	}

	/// <summary>Fetch the certificates issued for verification <paramref name="id"/>.</summary>
	public async Task<CertificatesOutcome> GetCertificatesAsync(
		KycProvider provider,
		string id,
		CancellationToken cancellationToken = default)
	{
		string providerJson = JsonSerializer.Serialize(provider, KeetaJson.Options);
		byte[] payload = await Runtime
			.KycGetCertificates(Handle, providerJson, id, cancellationToken)
			.ConfigureAwait(false);

		return ParseOutcome<Certificates, CertificatesOutcome>(payload, "certificates", ready => new CertificatesOutcome(ready, null), retry => new CertificatesOutcome(null, retry));
	}

	/// <summary>Parse <paramref name="provider"/>'s advertised issuer CA certificate.</summary>
	/// <remarks>Use it as a trusted root when verifying an issued <see cref="Crypto.KycCertificate"/>.</remarks>
	public Crypto.Certificate GetCA(KycProvider provider) =>
		Runtime.Certificates.Parse(provider.Ca);

	/// <summary>Read the status of verification <paramref name="id"/>.</summary>
	public async Task<StatusOutcome> GetVerificationStatusAsync(
		KycProvider provider,
		string id,
		CancellationToken cancellationToken = default)
	{
		string providerJson = JsonSerializer.Serialize(provider, KeetaJson.Options);
		byte[] payload = await Runtime
			.KycGetVerificationStatus(Handle, providerJson, id, cancellationToken)
			.ConfigureAwait(false);

		return ParseOutcome<VerificationStatus, StatusOutcome>(payload, "status", ready => new StatusOutcome(ready, null), retry => new StatusOutcome(null, retry));
	}

	private static string SerializeCountries(IEnumerable<string> countries) =>
		JsonSerializer.Serialize(countries.ToArray(), KeetaJson.Options);

	/// <summary>
	/// Shape a pending-or-ready outcome: a <c>retry</c> object yields
	/// <paramref name="retry"/> with its delay, otherwise the <paramref name="readyProperty"/>
	/// value is deserialized and passed to <paramref name="ready"/>.
	/// </summary>
	private static TOutcome ParseOutcome<TReady, TOutcome>(
		byte[] payload,
		string readyProperty,
		Func<TReady, TOutcome> ready,
		Func<uint, TOutcome> retry)
	{
		using var document = JsonDocument.Parse(payload);
		JsonElement root = document.RootElement;
		JsonElement type = root.GetProperty("type");
		if (type.GetString() == "retry")
		{
			JsonElement afterMs = root.GetProperty("afterMs");
			return retry(afterMs.GetUInt32());
		}

		JsonElement value = root.GetProperty(readyProperty);
		return ready(value.Deserialize<TReady>(KeetaJson.Options)!);
	}

	private protected override void Release(WasmRuntime runtime, int handle) => runtime.KycFree(handle);
}
