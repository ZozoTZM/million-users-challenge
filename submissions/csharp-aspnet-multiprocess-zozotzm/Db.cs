using System.Globalization;
using Npgsql;

static class Db
{
    public const int FeedSize = 20;

    public const string PostSelect = """
        SELECT p.id, p.body, p.created_at, u.username,
               (SELECT count(*) FROM likes l WHERE l.post_id = p.id)::int AS like_count
          FROM posts p
          JOIN users u ON u.id = p.user_id
        """;

    public static bool TryParseId(string? raw, out long id) =>
        long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;

    // The benchmark supplies a postgres:// URL. Aspire / local dev supplies the keyword form.
    // Both become one Npgsql connection string with the pool and prepare settings this app wants.
    public static string BuildConnectionString(string value, int maxPoolSize)
    {
        NpgsqlConnectionStringBuilder builder;
        if (value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(value);
            var userInfo = uri.UserInfo.Split(':', 2);
            builder = new NpgsqlConnectionStringBuilder
            {
                Host = uri.Host,
                Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
                Username = Uri.UnescapeDataString(userInfo[0]),
                Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
                Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            };
        }
        else
        {
            builder = new NpgsqlConnectionStringBuilder(value);
        }

        builder.MaxPoolSize = maxPoolSize;
        builder.Timeout = 5;          // connect timeout and wait for a pooled connection
        builder.CommandTimeout = 5;   // a stuck statement fails instead of holding a request for 30 s
        builder.MaxAutoPrepare = 10;  // Npgsql's opt-in statement preparation; the other drivers do this by default
        builder.AutoPrepareMinUsages = 2;
        builder.NoResetOnClose = true; // the app never changes session state; saves a reset statement per request
        builder.ApplicationName = "feed-api";
        return builder.ConnectionString;
    }
}
