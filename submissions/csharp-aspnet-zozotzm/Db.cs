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

    public static PostDto ReadPost(NpgsqlDataReader reader) =>
        new(reader.GetInt64(0), reader.GetString(1), FormatTimestamp(reader.GetDateTime(2)),
            reader.GetString(3), reader.GetInt32(4));

    public static string FormatTimestamp(DateTime utc) =>
        utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static bool TryParseId(string? raw, out long id) =>
        long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;

    // The benchmark supplies a postgres:// URL. Aspire/local dev supplies the keyword form.
    public static string ToConnectionString(string value, int maxPoolSize)
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
        builder.Timeout = 5;
        builder.CommandTimeout = 5;
        builder.MaxAutoPrepare = 10;
        builder.AutoPrepareMinUsages = 2;
        builder.NoResetOnClose = true;
        builder.ApplicationName = "feed-api";
        return builder.ConnectionString;
    }
}
