using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

using ioxide;
using ioxide.pg;

namespace LeanApi;

internal static class Db
{
    public const string PostSelect = """
        SELECT p.id, p.body, p.created_at, u.username,
               (SELECT count(*) FROM likes l WHERE l.post_id = p.id)::int AS like_count
          FROM posts p
          JOIN users u ON u.id = p.user_id
        """;

    public const string FeedSql = PostSelect + " ORDER BY p.created_at DESC, p.id DESC LIMIT $1";
    public const string GetPostSql = PostSelect + " WHERE p.id = $1";
    public const string CreatePostSql =
        "INSERT INTO posts (user_id, body) VALUES ($1, $2) RETURNING id, created_at";
    public const string LikeSql =
        "INSERT INTO likes (user_id, post_id) VALUES ($1, $2) ON CONFLICT (user_id, post_id) DO NOTHING";

    private static PgOptions? _options;
    private static int _effectiveConnections;

    public static int EffectiveConnections => _effectiveConnections;

    public static void Configure(int reactors)
    {
        string? url = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        var uri = new Uri(url);
        string[] userInfo = uri.UserInfo.Split(':', 2);

        int maxConnections = Int("DATABASE_MAX_CONN", 256);
        int perReactorMax = Int("DATABASE_POOL_PER_REACTOR_MAX", 32);
        int poolSize = Math.Clamp(maxConnections / Math.Max(reactors, 1), 1, perReactorMax);

        _options = new PgOptions
        {
            Host = ResolveIPv4(uri.Host),
            Port = (ushort)(uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port),
            User = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            PoolSize = poolSize,
            CommandTimeoutMs = 5000,
        };
        _effectiveConnections = poolSize * reactors;
    }

    // Runs on each reactor's own thread, so the pool's connections belong to that ring.
    public static void Start(Reactor reactor)
    {
        if (_options is null)
        {
            return;
        }

        try
        {
            PgPool.Start(reactor, _options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{Thread.CurrentThread.Name}] pg pool start failed: {ex.Message}");
        }
    }

    public static PgPool? TryPool(Reactor reactor)
    {
        if (_options is null)
        {
            return null;
        }

        try
        {
            return reactor.GetService<PgPool>();
        }
        catch
        {
            return null;
        }
    }

    public static long ParseLong(ReadOnlySpan<byte> value) =>
        Utf8Parser.TryParse(value, out long result, out _) ? result : 0;

    public static int ParseInt(ReadOnlySpan<byte> value) =>
        Utf8Parser.TryParse(value, out int result, out _) ? result : 0;

    /// <summary>
    /// ioxide.pg returns Postgres text. ISO timestamptz text is
    /// "2025-12-31 23:59:19.409+00"; the API requires UTC with exactly three fractional
    /// digits and a Z suffix, so parse the offset and truncate (not round) the microseconds.
    /// </summary>
    public static string FormatTimestamp(ReadOnlySpan<byte> value)
    {
        string text = Encoding.UTF8.GetString(value);
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        return text;
    }

    private static int Int(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None,
            CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;

    private static string ResolveIPv4(string host)
    {
        if (IPAddress.TryParse(host, out _))
        {
            return host;
        }

        foreach (IPAddress address in Dns.GetHostAddresses(host))
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                return address.ToString();
            }
        }

        return "127.0.0.1";
    }
}
