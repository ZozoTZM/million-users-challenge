using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GenHTTP.Api.Ioxide;
using ioxide;
using ioxide.pg;

namespace FeedApi;

internal static class Db
{
    public const string PostSelect = """
        SELECT p.id, p.body, p.created_at, u.username,
               (SELECT count(*) FROM likes l WHERE l.post_id = p.id)::int AS like_count
          FROM posts p
          JOIN users u ON u.id = p.user_id
        """;

    public static Post ReadPost(PgRow row) => new(
        ParseLong(row.Field(0)),
        Encoding.UTF8.GetString(row.Field(1)),
        FormatTimestamp(row.Field(2)),
        Encoding.UTF8.GetString(row.Field(3)),
        ParseInt(row.Field(4)));

    public static long ParseLong(ReadOnlySpan<byte> value) =>
        Utf8Parser.TryParse(value, out long result, out _) ? result : 0;

    public static int ParseInt(ReadOnlySpan<byte> value) =>
        Utf8Parser.TryParse(value, out int result, out _) ? result : 0;

    // ioxide.pg uses text format. PostgreSQL's ISO timestamptz text is
    // "2025-12-31 23:59:19.409+00"; DateTimeOffset handles the offset and fff truncates
    // the microseconds, as the SPEC requires.
    public static string FormatTimestamp(ReadOnlySpan<byte> value)
    {
        var text = Encoding.UTF8.GetString(value);
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }
        return text;
    }
}

internal static class Postgres
{
    private static PgOptions? _options;

    public static bool Enabled => _options is not null;

    public static void Configure(int reactors, int maxConnections)
    {
        var url = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (string.IsNullOrEmpty(url))
            return;

        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);
        var perReactorMax = int.TryParse(Environment.GetEnvironmentVariable("DATABASE_POOL_PER_REACTOR_MAX"), out var max)
            ? Math.Clamp(max, 1, 128)
            : 32;

        _options = new PgOptions
        {
            Host = ResolveIPv4(uri.Host),
            Port = (ushort)(uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port),
            User = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            // The HttpArena reference caps a reactor's ring-native pool at 8. The challenge allows
            // more, and Rust's entry uses 16 read + 16 write per 4-core thread; DATABASE_POOL_PER_REACTOR_MAX
            // raises the cap for the A/B without changing the default.
            PoolSize = Math.Clamp(maxConnections / Math.Max(reactors, 1), 1, perReactorMax),
            CommandTimeoutMs = 5000,
        };
    }

    // Runs once per reactor, on that reactor's own thread.
    public static void Start(Reactor reactor)
    {
        if (_options is null)
            return;
        try
        {
            PgPool.Start(reactor, _options);
        }
        catch
        {
            // If Postgres is unavailable, /health reports 503 and the DB routes return 500.
        }
    }

    public static PgPool? TryPool()
    {
        try
        {
            return IoxideReactor.Current.GetService<PgPool>();
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveIPv4(string host)
    {
        if (IPAddress.TryParse(host, out _))
            return host;

        foreach (var address in Dns.GetHostAddresses(host))
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
                return address.ToString();
        }
        return host;
    }
}

