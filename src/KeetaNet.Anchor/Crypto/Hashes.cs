namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// The hash of a block, as the reference implementations model it (a 32-byte
/// value type, not a wasm handle). Hex is the transport form: parse with
/// <see cref="Parse"/>, emit with <see cref="ToString"/>.
/// </summary>
public readonly struct BlockHash : IEquatable<BlockHash>
{
	/// <summary>The block hash length in bytes.</summary>
	public const int Length = 32;

	private readonly string _hex;

	private BlockHash(string normalizedHex)
	{
		_hex = normalizedHex;
	}

	/// <summary>Parse a 64-character hex block hash. Case is normalized away.</summary>
	public static BlockHash Parse(string hex) => new(HexValue.Normalize(hex, Length));

	/// <summary>The raw 32 hash bytes.</summary>
	public byte[] ToBytes() => Convert.FromHexString(_hex);

	/// <summary>The lowercase hex transport form.</summary>
	public override string ToString() => _hex ?? "";

	/// <inheritdoc />
	public bool Equals(BlockHash other) => string.Equals(_hex, other._hex, StringComparison.Ordinal);

	/// <inheritdoc />
	public override bool Equals(object? obj) => obj is BlockHash other && Equals(other);

	/// <inheritdoc />
	public override int GetHashCode() => _hex is null ? 0 : _hex.GetHashCode(StringComparison.Ordinal);

	/// <summary>Value equality over the hash bytes.</summary>
	public static bool operator ==(BlockHash left, BlockHash right) => left.Equals(right);

	/// <summary>Value inequality over the hash bytes.</summary>
	public static bool operator !=(BlockHash left, BlockHash right) => !left.Equals(right);
}

/// <summary>
/// The hash a published certificate is stored under on the ledger: the
/// SHA3-256 of its DER, as the reference implementations model it.
/// </summary>
public readonly struct CertificateHash : IEquatable<CertificateHash>
{
	/// <summary>The SHA3-256 certificate hash length in bytes.</summary>
	public const int Length = 32;

	private readonly string _hex;

	private CertificateHash(string normalizedHex)
	{
		_hex = normalizedHex;
	}

	/// <summary>Parse a 64-character hex certificate hash. Case is normalized away.</summary>
	public static CertificateHash Parse(string hex) => new(HexValue.Normalize(hex, Length));

	/// <summary>The raw 32 hash bytes.</summary>
	public byte[] ToBytes() => Convert.FromHexString(_hex);

	/// <summary>The lowercase hex transport form.</summary>
	public override string ToString() => _hex ?? "";

	/// <inheritdoc />
	public bool Equals(CertificateHash other) => string.Equals(_hex, other._hex, StringComparison.Ordinal);

	/// <inheritdoc />
	public override bool Equals(object? obj) => obj is CertificateHash other && Equals(other);

	/// <inheritdoc />
	public override int GetHashCode() => _hex is null ? 0 : _hex.GetHashCode(StringComparison.Ordinal);

	/// <summary>Value equality over the hash bytes.</summary>
	public static bool operator ==(CertificateHash left, CertificateHash right) => left.Equals(right);

	/// <summary>Value inequality over the hash bytes.</summary>
	public static bool operator !=(CertificateHash left, CertificateHash right) => !left.Equals(right);
}

/// <summary>Shared validation for the hex-transported hash value types.</summary>
internal static class HexValue
{
	/// <summary>
	/// Validate that <paramref name="hex"/> encodes exactly
	/// <paramref name="expectedBytes"/> bytes and lowercase it, so equality is
	/// byte equality regardless of the producer's casing.
	/// </summary>
	public static string Normalize(string hex, int expectedBytes)
	{
		if (hex.Length != expectedBytes * 2)
		{
			throw new KeetaException("HASH_LENGTH", $"expected {expectedBytes * 2} hex characters, got {hex.Length}");
		}

		// Round-tripping through bytes rejects non-hex characters up front.
		// (Convert.ToHexStringLower needs .NET 9. net8.0 is still targeted.)
		byte[] value = Convert.FromHexString(hex);
		return Convert.ToHexString(value).ToLowerInvariant();
	}
}
