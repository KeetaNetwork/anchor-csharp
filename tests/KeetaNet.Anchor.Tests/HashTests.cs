using KeetaNet.Anchor.Crypto;
using Xunit;

namespace KeetaNet.Anchor.Tests;

/// <summary>
/// The hash value types' public contract: parsing normalizes case, equality
/// is byte equality, and malformed hex is refused with a stable code.
/// </summary>
public sealed class HashTests
{
	private const string UpperHex =
		"0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

	private const string OtherHex =
		"0000000000000000000000000000000000000000000000000000000000000001";

	private const string NonHex =
		"zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz";

	[Fact]
	public void BlockHashesCompareByValue() =>
		AssertValueSemantics(
			BlockHash.Parse,
			static hash => hash.ToBytes(),
			static (left, right) => left == right,
			static (left, right) => left != right);

	[Fact]
	public void CertificateHashesCompareByValue() =>
		AssertValueSemantics(
			CertificateHash.Parse,
			static hash => hash.ToBytes(),
			static (left, right) => left == right,
			static (left, right) => left != right);

	[Theory]
	[InlineData("", "HASH_LENGTH")]
	[InlineData("abc123", "HASH_LENGTH")]
	[InlineData(UpperHex + "00", "HASH_LENGTH")]
	[InlineData(NonHex, "HASH_FORMAT")]
	public void MalformedHexIsRefusedWithAStableCode(string hex, string expectedCode)
	{
		KeetaException blockFailure = Assert.Throws<KeetaException>(() => BlockHash.Parse(hex));
		Assert.Equal(expectedCode, blockFailure.Code);

		KeetaException certificateFailure = Assert.Throws<KeetaException>(() => CertificateHash.Parse(hex));
		Assert.Equal(expectedCode, certificateFailure.Code);
	}

	/// <summary>
	/// The value semantics both hash types share: case-insensitive parsing to
	/// one lowercase identity, byte equality across every equality surface,
	/// and a default instance that behaves as an empty value.
	/// </summary>
	private static void AssertValueSemantics<T>(
		Func<string, T> parse,
		Func<T, byte[]> toBytes,
		Func<T, T, bool> equal,
		Func<T, T, bool> notEqual)
		where T : struct, IEquatable<T>
	{
		T upper = parse(UpperHex);
		T lower = parse(UpperHex.ToLowerInvariant());
		T other = parse(OtherHex);

		Assert.Equal(UpperHex.ToLowerInvariant(), upper.ToString());
		Assert.True(equal(upper, lower));
		Assert.True(upper.Equals((object)lower));
		Assert.Equal(upper.GetHashCode(), lower.GetHashCode());

		Assert.True(notEqual(upper, other));
		Assert.False(upper.Equals(null));

		Assert.Equal(Convert.FromHexString(UpperHex), toBytes(upper));

		Assert.Equal("", default(T).ToString());
		Assert.True(equal(default, default));
	}
}
