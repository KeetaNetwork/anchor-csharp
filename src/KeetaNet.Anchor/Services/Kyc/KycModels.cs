using System.Text.Json.Serialization;

namespace KeetaNet.Anchor;

/// <summary>The KYC operation endpoint templates a provider advertises.</summary>
public sealed record KycOperations(
	string? CreateVerification,
	string? GetCertificates,
	string? GetVerificationStatus,
	string? CheckLocality,
	string? GetEstimate);

/// <summary>
/// A KYC provider's advertised metadata, discovered from on-chain service
/// metadata (the reference <c>KycProviderInfo</c>). Operations live on the
/// <see cref="KycProvider"/> handle bound through
/// <see cref="KycClient.Provider"/>.
/// </summary>
/// <remarks><see cref="CountryCodes"/> is null for a worldwide provider.</remarks>
public sealed record KycProviderInfo(
	string Id,
	string Ca,
	KycOperations Operations,
	IReadOnlyList<string>? CountryCodes);

/// <summary>
/// The countries KYC providers can validate, aggregated across every root
/// (the reference <c>getSupportedCountries</c>): <see cref="Worldwide"/> when
/// any provider publishes no country list, otherwise the sorted, deduplicated
/// union of the published codes.
/// </summary>
public sealed record SupportedCountries(bool Worldwide, IReadOnlyList<string> Countries)
{
	/// <summary>Fold discovered <paramref name="providers"/> into their aggregate coverage.</summary>
	public static SupportedCountries FromProviders(IEnumerable<KycProviderInfo> providers)
	{
		var countries = new List<string>();
		foreach (KycProviderInfo provider in providers)
		{
			if (provider.CountryCodes is null)
			{
				return new SupportedCountries(true, Array.Empty<string>());
			}

			countries.AddRange(provider.CountryCodes);
		}

		string[] union = countries
			.Distinct(StringComparer.Ordinal)
			.OrderBy(code => code, StringComparer.Ordinal)
			.ToArray();

		return new SupportedCountries(false, union);
	}
}

/// <summary>
/// The cost a provider expects to charge for a verification: a <see cref="Token"/>
/// and the <see cref="Min"/>/<see cref="Max"/> bounds, decimal strings in that token's units.
/// </summary>
public sealed record ExpectedCost(string Min, string Max, string Token);

/// <summary>An in-progress verification, the URL where the user completes it, and its expected cost.</summary>
public sealed record Verification(string Id, string WebUrl, ExpectedCost ExpectedCost);

/// <summary>
/// The provider-reported status of a verification, and whether the provider
/// requires a manual review to complete (null when not reported).
/// </summary>
public sealed record VerificationStatus(string Status, bool? RequiresManualVerification = null);

/// <summary>One issued, PEM-encoded certificate and the intermediates bridging it to a trust root.</summary>
public sealed record Certificate(
	[property: JsonPropertyName("certificate")] string Value,
	IReadOnlyList<string> Intermediates);

/// <summary>The certificates issued for a verification.</summary>
public sealed record Certificates(IReadOnlyList<Certificate> Results);

/// <summary>
/// A result the provider may report as pending: either <see cref="Ready"/> with a
/// value, or <see cref="RetryAfterMs"/> milliseconds before retrying.
/// </summary>
public sealed record VerificationOutcome(Verification? Ready, uint? RetryAfterMs);

/// <summary>A status result, ready or retry-after-millis.</summary>
public sealed record StatusOutcome(VerificationStatus? Ready, uint? RetryAfterMs);

/// <summary>A certificates result, ready or retry-after-millis.</summary>
public sealed record CertificatesOutcome(Certificates? Ready, uint? RetryAfterMs);
