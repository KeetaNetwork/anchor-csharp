namespace KeetaNet.Anchor.Crypto;

/// <summary>Project optional wrapper sequences to their core-module handle arrays.</summary>
internal static class Handles
{
	public static int[] Of(IEnumerable<Account>? accounts)
	{
		IEnumerable<Account> source = accounts ?? Enumerable.Empty<Account>();
		return source.Select(account => account.Handle).ToArray();
	}

	public static int[] Of(IEnumerable<Certificate>? certificates)
	{
		IEnumerable<Certificate> source = certificates ?? Enumerable.Empty<Certificate>();
		return source.Select(certificate => certificate.Handle).ToArray();
	}
}
