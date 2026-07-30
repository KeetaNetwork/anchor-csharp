using Xunit;

namespace KeetaNet.Anchor.Tests;

/// <summary>
/// KYC model fold
/// </summary>
public sealed class KycModelTests
{
	private static readonly string[] UsAndGermany = { "US", "DE" };
	private static readonly string[] GermanyAndFrance = { "DE", "FR" };
	private static readonly string[] SortedUnion = { "DE", "FR", "US" };
	private static readonly string[] UsOnly = { "US" };

	[Fact]
	public void SupportedCountriesUnionIsSortedAndDeduplicated()
	{
		var providers = new[] { Provider("a", UsAndGermany), Provider("b", GermanyAndFrance) };

		SupportedCountries folded = SupportedCountries.FromProviders(providers);

		Assert.False(folded.Worldwide);
		Assert.Equal(SortedUnion, folded.Countries);
	}

	[Fact]
	public void AWorldwideProviderFoldsToWorldwide()
	{
		var providers = new[] { Provider("a", UsOnly), Provider("b", null) };

		SupportedCountries folded = SupportedCountries.FromProviders(providers);

		Assert.True(folded.Worldwide);
		Assert.Empty(folded.Countries);
	}

	[Fact]
	public void NoProvidersFoldToAnEmptyUnion()
	{
		SupportedCountries folded = SupportedCountries.FromProviders(Array.Empty<KycProviderInfo>());

		Assert.False(folded.Worldwide);
		Assert.Empty(folded.Countries);
	}

	/// <summary>A provider snapshot advertising <paramref name="countryCodes"/>, or worldwide when null.</summary>
	private static KycProviderInfo Provider(string id, string[]? countryCodes) =>
		new(id, "ca-pem", new KycOperations(null, null, null, null, null), countryCodes);
}
