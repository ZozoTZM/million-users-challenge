using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

readonly record struct AuthUser(long Id, string Username);

static class Auth
{
    public const string InvalidPayload = "invalid token payload";

    // sub must be a positive integer string and username a string.
    public static AuthUser? GetUser(ClaimsPrincipal? principal)
    {
        var sub = principal?.FindFirst("sub");
        var username = principal?.FindFirst("username");
        if (sub is null || sub.ValueType != ClaimValueTypes.String) return null;
        if (!long.TryParse(sub.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0) return null;
        if (username is null || username.ValueType != ClaimValueTypes.String) return null;
        return new AuthUser(id, username.Value);
    }

    // IdentityModel refuses HMAC keys under 256 bits; the challenge secret is 184 bits. This is the
    // documented SignatureValidator hook: accept HS256 only, verify with the BCL HMAC, constant time.
    public static SecurityToken VerifyHs256(string token, byte[] key)
    {
        var jwt = new JsonWebToken(token); // malformed -> 401 invalid or expired token
        if (jwt.Alg != SecurityAlgorithms.HmacSha256)
            throw new SecurityTokenInvalidAlgorithmException($"alg {jwt.Alg} not allowed");

        var lastDot = token.LastIndexOf('.');
        Span<byte> expected = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(token, 0, lastDot), expected);

        byte[] actual;
        try
        {
            actual = Base64UrlEncoder.DecodeBytes(token[(lastDot + 1)..]);
        }
        catch (FormatException)
        {
            throw new SecurityTokenInvalidSignatureException("bad signature encoding");
        }

        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new SecurityTokenInvalidSignatureException("bad signature");
        return jwt;
    }
}
