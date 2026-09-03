using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace XetSharp.Server.Auth;

/// <summary>
/// Signs the xorb URLs a reconstruction hands out, the way the Hub's CDN does: a URL authorizes
/// one exact <c>Range</c> header and nothing else, until it expires. The range travels in the
/// <c>X-Xet-Signed-Range</c> query parameter the spec's examples show, which a client echoes
/// verbatim.
/// </summary>
internal sealed class XorbUrlSigner(byte[] key, TimeProvider timeProvider)
{
    public const string PathPrefix = "/xorb/default/";

    public Uri Sign(Uri baseUrl, MerkleHash xorb, string rangeSpec, TimeSpan lifetime)
    {
        var expires = timeProvider.GetUtcNow().Add(lifetime).ToUnixTimeSeconds();
        var signature = Base64Url.EncodeToString(Digest(xorb, rangeSpec, expires));
        // Relative to the base rather than to the host, so a base with a path — a proxy prefix, an
        // API Gateway stage — keeps that path in the URL.
        return new Uri(
            baseUrl,
            $"{PathPrefix.TrimStart('/')}{xorb}?X-Xet-Signed-Range={Uri.EscapeDataString(rangeSpec)}&Expires={expires}&Signature={signature}");
    }

    /// <summary>Whether the signature was made by this key over this xorb and range, and is still current.</summary>
    public bool Verify(MerkleHash xorb, string? signedRange, string? expires, string? signature)
    {
        if (string.IsNullOrEmpty(signedRange) || string.IsNullOrEmpty(signature) || !long.TryParse(expires, out var expiresAt))
        {
            return false;
        }

        // Compared as Unix seconds rather than converted: the value is whatever the query string
        // says, and a timestamp outside the calendar is an invalid URL, not a server error.
        if (expiresAt <= timeProvider.GetUtcNow().ToUnixTimeSeconds())
        {
            return false;
        }

        byte[] presented;
        try
        {
            presented = Base64Url.DecodeFromChars(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(presented, Digest(xorb, signedRange, expiresAt));
    }

    private byte[] Digest(MerkleHash xorb, string rangeSpec, long expires) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{xorb}\n{rangeSpec}\n{expires}"));
}
