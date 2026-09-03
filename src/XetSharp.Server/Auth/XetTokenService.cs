using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using XetSharp.Hub;
using XetSharp.Server.Storage;

namespace XetSharp.Server.Auth;

/// <summary>What a Xet token says about its bearer.</summary>
public sealed record XetTokenClaims(RepositoryId Repository, string Revision, XetTokenScope Scope, DateTimeOffset ExpiresAt)
{
    public bool Allows(XetTokenScope scope) => Scope == XetTokenScope.Write || scope == XetTokenScope.Read;
}

/// <summary>
/// Mints and checks the bearer tokens the CAS API runs on. A token is its claims, base64url'd,
/// followed by an HMAC over them: nothing is stored server-side, so any instance holding the key
/// can check a token any other instance minted, which is what a Lambda deployment needs.
/// </summary>
internal sealed class XetTokenService(byte[] key, TimeProvider timeProvider)
{
    private const string Prefix = "xet_";

    public string Issue(XetTokenClaims claims)
    {
        var payload = Encoding.UTF8.GetBytes(string.Join(
            '\n',
            claims.Repository.Type,
            claims.Repository.Namespace,
            claims.Repository.Name,
            claims.Revision,
            claims.Scope == XetTokenScope.Write ? "write" : "read",
            claims.ExpiresAt.ToUnixTimeSeconds().ToString()));

        return Prefix + Base64Url.EncodeToString(payload) + "." + Base64Url.EncodeToString(HMACSHA256.HashData(key, payload));
    }

    /// <summary>Whether the token was minted by this key and has not expired.</summary>
    public bool TryValidate(string? token, out XetTokenClaims claims)
    {
        claims = null!;
        if (token is null || !token.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var separator = token.LastIndexOf('.');
        if (separator < 0)
        {
            return false;
        }

        byte[] payload;
        byte[] signature;
        try
        {
            payload = Base64Url.DecodeFromChars(token.AsSpan(Prefix.Length, separator - Prefix.Length));
            signature = Base64Url.DecodeFromChars(token.AsSpan(separator + 1));
        }
        catch (FormatException)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(key, payload)))
        {
            return false;
        }

        var fields = Encoding.UTF8.GetString(payload).Split('\n');
        if (fields.Length != 6 || !long.TryParse(fields[5], out var expiresAt))
        {
            return false;
        }

        var expiry = DateTimeOffset.FromUnixTimeSeconds(expiresAt);
        if (expiry <= timeProvider.GetUtcNow())
        {
            return false;
        }

        claims = new XetTokenClaims(
            new RepositoryId(fields[0], fields[1], fields[2]),
            fields[3],
            fields[4] == "write" ? XetTokenScope.Write : XetTokenScope.Read,
            expiry);
        return true;
    }
}
