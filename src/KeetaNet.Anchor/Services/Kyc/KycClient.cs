using System.Text.Json;

namespace KeetaNet.Anchor;

/// <summary>
/// A KYC anchor client bound to a signer and a metadata root. Discovery, request
/// signing, retries, and polling all run inside the wasm core. Discovery returns
/// <see cref="KycProvider"/> handles carrying the verification operations. The
/// client is thread-safe: operations serialize onto the runtime's dispatcher,
/// and every networked method honors its <see cref="CancellationToken"/> before
/// dispatch and during host HTTP and sleeps.
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
	public async Task<IReadOnlyList<KycProvider>> GetProviders(
		IEnumerable<string> countries,
		CancellationToken cancellationToken = default)
	{
		IReadOnlyList<KycProviderInfo> infos = await GetProviderInfos(countries, cancellationToken).ConfigureAwait(false);
		return infos.Select(Provider).ToArray();
	}

	/// <summary>
	/// The countries any provider can validate, folded across every root
	/// (the reference <c>getSupportedCountries</c>).
	/// </summary>
	public async Task<SupportedCountries> GetSupportedCountries(CancellationToken cancellationToken = default)
	{
		IReadOnlyList<KycProviderInfo> infos = await GetProviderInfos(Array.Empty<string>(), cancellationToken).ConfigureAwait(false);
		return SupportedCountries.FromProviders(infos);
	}

	/// <summary>Bind a stored metadata snapshot back to this client as an operable handle.</summary>
	public KycProvider Provider(KycProviderInfo info) => new(this, info);

	/// <summary>
	/// Start a verification with <paramref name="provider"/> for
	/// <paramref name="countries"/>, optionally redirecting the user to
	/// <paramref name="redirect"/> when the flow ends.
	/// </summary>
	internal async Task<VerificationOutcome> StartVerification(
		KycProviderInfo provider,
		IEnumerable<string> countries,
		string? redirect,
		CancellationToken cancellationToken)
	{
		string providerJson = JsonSerializer.Serialize(provider, KeetaJson.Options);
		string countriesJson = SerializeCountries(countries);

		byte[] payload = await Runtime
			.KycCreateVerification(Handle, providerJson, countriesJson, redirect ?? "", cancellationToken)
			.ConfigureAwait(false);

		return ParseOutcome<Verification, VerificationOutcome>(payload, "verification", ready => new VerificationOutcome(ready, null), retry => new VerificationOutcome(null, retry));
	}

	/// <summary>Fetch the certificates issued for verification <paramref name="id"/>.</summary>
	internal async Task<CertificatesOutcome> GetCertificates(
		KycProviderInfo provider,
		string id,
		CancellationToken cancellationToken)
	{
		string providerJson = JsonSerializer.Serialize(provider, KeetaJson.Options);
		byte[] payload = await Runtime
			.KycGetCertificates(Handle, providerJson, id, cancellationToken)
			.ConfigureAwait(false);

		return ParseOutcome<Certificates, CertificatesOutcome>(payload, "certificates", ready => new CertificatesOutcome(ready, null), retry => new CertificatesOutcome(null, retry));
	}

	/// <summary>Parse <paramref name="provider"/>'s advertised issuer CA certificate.</summary>
	internal Crypto.Certificate GetCA(KycProviderInfo provider) =>
		Runtime.Certificates.Parse(provider.Ca);

	/// <summary>Read the status of verification <paramref name="id"/>.</summary>
	internal async Task<StatusOutcome> GetVerificationStatus(
		KycProviderInfo provider,
		string id,
		CancellationToken cancellationToken)
	{
		string providerJson = JsonSerializer.Serialize(provider, KeetaJson.Options);
		byte[] payload = await Runtime
			.KycGetVerificationStatus(Handle, providerJson, id, cancellationToken)
			.ConfigureAwait(false);

		return ParseOutcome<VerificationStatus, StatusOutcome>(payload, "status", ready => new StatusOutcome(ready, null), retry => new StatusOutcome(null, retry));
	}

	/// <summary>The raw discovery payload decoded to metadata snapshots.</summary>
	private async Task<IReadOnlyList<KycProviderInfo>> GetProviderInfos(
		IEnumerable<string> countries,
		CancellationToken cancellationToken)
	{
		string countriesJson = SerializeCountries(countries);

		byte[] payload = await Runtime.KycProviders(Handle, countriesJson, cancellationToken).ConfigureAwait(false);
		return KeetaJson.ReadList<KycProviderInfo>(payload);
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
