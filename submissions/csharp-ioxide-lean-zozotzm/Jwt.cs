using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LeanApi;

internal sealed class JwtException(string message) : Exception(message);

internal static class Jwt
{
    private const string Invalid = "invalid or expired token";
    private const string InvalidPayload = "invalid token payload";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    };

    public static AuthUser Validate(ReadOnlySpan<char> token, byte[] key, DateTimeOffset now)
    {
        try
        {
            var first = token.IndexOf('.');
            var last = token.LastIndexOf('.');
            if (first <= 0 || last <= first || last == token.Length - 1)
                throw new JwtException(Invalid);

            byte[] headerBytes;
            byte[] payloadBytes;
            byte[] signature;
            try
            {
                headerBytes = Base64Url.DecodeFromChars(token[..first]);
                payloadBytes = Base64Url.DecodeFromChars(token[(first + 1)..last]);
                signature = Base64Url.DecodeFromChars(token[(last + 1)..]);
            }
            catch (FormatException)
            {
                throw new JwtException(Invalid);
            }

            if (signature.Length != 32)
                throw new JwtException(Invalid);

            using (var header = JsonDocument.Parse(headerBytes, DocumentOptions))
            {
                if (header.RootElement.ValueKind != JsonValueKind.Object ||
                    !header.RootElement.TryGetProperty("alg", out var alg) ||
                    alg.ValueKind != JsonValueKind.String ||
                    !string.Equals(alg.GetString(), "HS256", StringComparison.Ordinal))
                {
                    throw new JwtException(Invalid);
                }
            }

            byte[]? rented = null;
            Span<byte> signingInput = last <= 1024
                ? stackalloc byte[last]
                : (rented = ArrayPool<byte>.Shared.Rent(last));
            try
            {
                var length = Encoding.ASCII.GetBytes(token[..last], signingInput);
                Span<byte> expected = stackalloc byte[32];
                HMACSHA256.HashData(key, signingInput[..length], expected);
                if (!CryptographicOperations.FixedTimeEquals(expected, signature))
                    throw new JwtException(Invalid);
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<byte>.Shared.Return(rented);
            }

            using var payload = JsonDocument.Parse(payloadBytes, DocumentOptions);
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JwtException(Invalid);

            if (!root.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var expiresAt))
                throw new JwtException(Invalid);

            var nowSeconds = now.ToUnixTimeSeconds();
            if (expiresAt <= nowSeconds)
                throw new JwtException(Invalid);

            if (root.TryGetProperty("nbf", out var notBefore) &&
                (!notBefore.TryGetInt64(out var notBeforeSeconds) || notBeforeSeconds > nowSeconds))
            {
                throw new JwtException(Invalid);
            }

            if (!root.TryGetProperty("sub", out var sub) || sub.ValueKind != JsonValueKind.String ||
                !long.TryParse(sub.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
                id <= 0)
            {
                throw new JwtException(InvalidPayload);
            }

            if (!root.TryGetProperty("username", out var username) || username.ValueKind != JsonValueKind.String)
                throw new JwtException(InvalidPayload);

            return new AuthUser(id, username.GetString()!);
        }
        catch (JwtException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            throw new JwtException(Invalid);
        }
    }
}


internal readonly record struct AuthUser(long Id, string Username);
