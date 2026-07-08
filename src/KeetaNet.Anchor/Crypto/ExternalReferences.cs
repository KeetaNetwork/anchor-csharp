using System.Net;
using System.Text;
using System.Text.Json;

namespace KeetaNet.Anchor.Crypto;

/// <summary>
/// Networked convenience for sharable-attribute external references. The core
/// ingests external blobs but never performs I/O. This fetches each discovered
/// reference's stored bytes, decoding <c>data:</c> URLs inline.
/// </summary>
public static class ExternalReferences
{
	/// <summary>
	/// Fetch every reference's blob, keyed by the reference's digest id: a
	/// <c>data:</c> URL decodes inline, an http(s) URL is fetched with
	/// <paramref name="httpClient"/>, and a JSON response in the
	/// storage-service <c>{data, mimeType}</c> convention is unwrapped.
	/// </summary>
	/// <exception cref="KeetaException">
	/// Code <c>REFERENCE_FETCH</c> when a URL cannot be decoded or the server
	/// does not answer 200.
	/// </exception>
	public static async Task<IReadOnlyDictionary<string, byte[]>> FetchBlobs(
		HttpClient httpClient,
		IEnumerable<AttributeReference> references,
		CancellationToken cancellationToken = default)
	{
		var blobs = new Dictionary<string, byte[]>();
		foreach (AttributeReference reference in references)
		{
			blobs[reference.Id] = await FetchReference(httpClient, reference, cancellationToken).ConfigureAwait(false);
		}

		return blobs;
	}

	/// <summary>Fetch one reference's raw stored bytes from its URL.</summary>
	private static async Task<byte[]> FetchReference(
		HttpClient httpClient,
		AttributeReference reference,
		CancellationToken cancellationToken)
	{
		string url = reference.Url;
		if (url.StartsWith("data:", StringComparison.Ordinal))
		{
			return DecodeDataUrl(url);
		}

		using HttpResponseMessage response = await Get(httpClient, url, cancellationToken).ConfigureAwait(false);
		if (response.StatusCode != HttpStatusCode.OK)
		{
			throw new KeetaException(
				"REFERENCE_FETCH",
				$"the server answered {(int)response.StatusCode} for `{url}`");
		}

		byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
		return UnwrapContainerPayload(url, body);
	}

	/// <summary>Issue the GET, surfacing a transport failure as the stable typed error.</summary>
	private static async Task<HttpResponseMessage> Get(
		HttpClient httpClient,
		string url,
		CancellationToken cancellationToken)
	{
		try
		{
			return await httpClient.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
		}
		catch (HttpRequestException error)
		{
			throw new KeetaException("REFERENCE_FETCH", $"the request to `{url}` failed", error);
		}
	}

	/// <summary>Decode a <c>data:&lt;media type&gt;[;base64],&lt;data&gt;</c> URL body.</summary>
	private static byte[] DecodeDataUrl(string url)
	{
		string rest = url["data:".Length..];
		int separator = rest.IndexOf(',', StringComparison.Ordinal);
		if (separator < 0)
		{
			throw new KeetaException("REFERENCE_FETCH", $"the data URL `{url}` has no payload");
		}

		string header = rest[..separator];
		string data = rest[(separator + 1)..];
		if (!header.EndsWith(";base64", StringComparison.Ordinal))
		{
			return Encoding.UTF8.GetBytes(data);
		}

		return DecodeBase64(data, $"the data URL `{url}`");
	}

	/// <summary>
	/// Unwrap the storage-service container-payload convention: a JSON body of
	/// exactly <c>{data, mimeType}</c> (both strings) carries the base64 stored
	/// bytes. Anything else is the stored bytes themselves.
	/// </summary>
	/// <exception cref="KeetaException">
	/// Code <c>REFERENCE_FETCH</c> when the wrapper shape is detected but its
	/// base64 payload does not decode, so a corrupted wrapper surfaces here
	/// instead of as a later decryption failure.
	/// </exception>
	private static byte[] UnwrapContainerPayload(string url, byte[] body)
	{
		string? data = WrappedPayload(body);
		if (data is null)
		{
			return body;
		}

		return DecodeBase64(data, $"the wrapped payload from `{url}`");
	}

	/// <summary>Decode base64 <paramref name="data"/>, naming <paramref name="source"/> on failure.</summary>
	private static byte[] DecodeBase64(string data, string source)
	{
		try
		{
			return Convert.FromBase64String(data);
		}
		catch (FormatException error)
		{
			throw new KeetaException("REFERENCE_FETCH", $"{source} is not valid base64", error);
		}
	}

	/// <summary>
	/// The base64 payload of an exact <c>{data, mimeType}</c> wrapper, or
	/// <c>null</c> when the body is anything else.
	/// </summary>
	private static string? WrappedPayload(byte[] body)
	{
		try
		{
			using JsonDocument parsed = JsonDocument.Parse(body);
			JsonElement root = parsed.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				return null;
			}

			string? data = null;
			bool hasMimeType = false;
			int properties = 0;
			using JsonElement.ObjectEnumerator enumerated = root.EnumerateObject();
			foreach (JsonProperty property in enumerated)
			{
				properties++;
				if (property.Name == "data" && property.Value.ValueKind == JsonValueKind.String)
				{
					data = property.Value.GetString();
				}
				else if (property.Name == "mimeType" && property.Value.ValueKind == JsonValueKind.String)
				{
					hasMimeType = true;
				}
			}

			if (properties != 2 || !hasMimeType)
			{
				return null;
			}

			return data;
		}
		catch (JsonException)
		{
			// A non-JSON body is the stored bytes themselves (e.g. raw DER).
			return null;
		}
	}
}
