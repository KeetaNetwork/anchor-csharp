using Xunit;

namespace KeetaNet.Anchor.Tests;

/// <summary>
/// The blocker-exception decode contract at the core error boundary: every
/// recognized blocker payload rehydrates typed, and anything else falls back
/// to the plain failure path so a newer core never breaks an older binding.
/// </summary>
public sealed class BlockerExceptionTests
{
	private const string ShareCode = "KEETA_ANCHOR_ASSET_MOVEMENT_KYC_SHARE_NEEDED";

	[Theory]
	[InlineData(
		"""{"type":"kycShareNeeded","tosFlow":null,"neededAttributes":["fullName"],"shareWithPrincipals":["keeta_p"],"acceptedIssuers":[]}""",
		typeof(AssetKycShareNeededBlocker))]
	[InlineData(
		"""{"type":"additionalKycNeeded","toCompleteFlow":null}""",
		typeof(AssetAdditionalKycNeededBlocker))]
	[InlineData(
		"""{"type":"operationNotSupported","forAsset":"asset","forRail":"ACH_DEBIT"}""",
		typeof(AssetOperationNotSupportedBlocker))]
	[InlineData(
		"""{"type":"userActionNeeded","actionsNeeded":[]}""",
		typeof(AssetUserActionNeededBlocker))]
	public void ARecognizedPayloadDecodesToItsTypedBlocker(string payload, Type blockerType)
	{
		KeetaBlockerException? decoded = KeetaBlockerException.TryDecode(ShareCode, payload);

		Assert.NotNull(decoded);
		Assert.Equal(ShareCode, decoded!.Code);
		Assert.IsType(blockerType, decoded.Blocker);
	}

	// A non-blocker code, a blocker type this binding does not know (a newer
	// core), an unrecognized "other" shape, a non-JSON message, and a JSON
	// null must all fall back to the plain failure path instead of throwing
	// out of the decoder.
	[Theory]
	[InlineData("SERVICE", """{"type":"kycShareNeeded"}""")]
	[InlineData("KEETA_ANCHOR_ASSET_MOVEMENT_SOMETHING_NEW", """{"type":"somethingNew"}""")]
	[InlineData(ShareCode, """{"type":"other","name":"SomeError","code":"SOMETHING_ELSE","message":"boom"}""")]
	[InlineData(ShareCode, "not json at all")]
	[InlineData(ShareCode, "null")]
	public void AnUnrecognizedPayloadFallsBackToAPlainFailure(string code, string payload) =>
		Assert.Null(KeetaBlockerException.TryDecode(code, payload));
}
