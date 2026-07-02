using Xunit;

namespace KeetaNet.Anchor.Tests;

public sealed class SdkTests
{
	[Fact]
	public void NameMatchesPackageId()
	{
		Assert.Equal("KeetaNet.Anchor", Sdk.Name);
	}
}
