using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GenHTTP.Api.Protocol;
using ioxide.pg;

namespace FeedApi;

internal static class App
{
    private static readonly JsonWriterOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = true,
    };

    private static byte[] _jwtKey = [];
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();

    public static void Configure(string jwtSecret) => _jwtKey = Encoding.UTF8.GetBytes(jwtSecret);

    public static async ValueTask<IResponse?> HandleAsync(IRequest request)
    {
        try
        {
            var method = request.Header.Method;
            var path = request.Header.Path.ToString();

            if (method == RequestMethod.Get)
            {
                if (path == "/health")
                    return await Health(request);
                if (path == "/feed")
                    return await Feed(request);
                if (path.StartsWith("/posts/", StringComparison.Ordinal))
                {
                    var raw = path.AsSpan(7);
                    if (raw.Length == 0 || raw.IndexOf('/') >= 0)
                        return Error(request, ResponseStatus.NotFound, "not found");
                    return await GetPost(request, raw.ToString());
                }
                return Error(request, ResponseStatus.NotFound, "not found");
            }

            if (method == RequestMethod.Post)
            {
                if (path == "/posts")
                {
                    var auth = Authenticate(request);
                    if (auth.Error is not null)
                        return Error(request, ResponseStatus.Unauthorized, auth.Error);
                    return await CreatePost(request, auth.User!.Value);
                }

                if (TryLikePath(path, out var idText))
                {
                    // Auth is checked before the id, as required by the spec.
                    var auth = Authenticate(request);
                    if (auth.Error is not null)
                        return Error(request, ResponseStatus.Unauthorized, auth.Error);
                    if (!TryParseId(idText, out var postId))
                        return Error(request, ResponseStatus.BadRequest, "invalid post id");
                    return await Like(request, auth.User!.Value, postId);
                }
            }

            return Error(request, ResponseStatus.NotFound, "not found");
        }
        catch
        {
            return Error(request, ResponseStatus.InternalServerError, "internal server error");
        }
    }

    private static async ValueTask<IResponse> Health(IRequest request)
    {
        try
        {
            var pool = Postgres.TryPool();
            if (pool is null)
                return Json(request, ResponseStatus.ServiceUnavailable,
                    Degraded("database unavailable"));
            await pool.QueryAsync("SELECT 1");
            return Json(request, ResponseStatus.Ok,
                Serialize(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("status", "ok");
                    w.WriteString("db", "ok");
                    w.WriteNumber("uptime_s", (long)Uptime.Elapsed.TotalSeconds);
                    w.WriteEndObject();
                }));
        }
        catch (Exception ex)
        {
            return Json(request, ResponseStatus.ServiceUnavailable, Degraded(ex.Message));
        }
    }

    private static async ValueTask<IResponse> Feed(IRequest request)
    {
        var pool = Postgres.TryPool();
        if (pool is null)
            return Error(request, ResponseStatus.InternalServerError, "internal server error");

        var posts = new List<Post>(20);
        PgParam[] args = [PgParam.Int(20)];
        await pool.QueryAsync(
            Db.PostSelect + " ORDER BY p.created_at DESC, p.id DESC LIMIT $1",
            args,
            row => posts.Add(Db.ReadPost(row)));

        return Json(request, ResponseStatus.Ok, Serialize(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("posts");
            foreach (var post in posts)
                WritePost(w, post);
            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    private static async ValueTask<IResponse> GetPost(IRequest request, string idText)
    {
        if (!TryParseId(idText, out var id))
            return Error(request, ResponseStatus.BadRequest, "invalid post id");

        var pool = Postgres.TryPool();
        if (pool is null)
            return Error(request, ResponseStatus.InternalServerError, "internal server error");

        Post? found = null;
        PgParam[] args = [PgParam.Int(id)];
        await pool.QueryAsync(Db.PostSelect + " WHERE p.id = $1", args,
            row => found = Db.ReadPost(row));

        if (found is not { } post)
            return Error(request, ResponseStatus.NotFound, "post not found");

        return Json(request, ResponseStatus.Ok, Serialize(w =>
        {
            w.WriteStartObject();
            w.WritePropertyName("post");
            WritePost(w, post);
            w.WriteEndObject();
        }));
    }

    private static async ValueTask<IResponse> CreatePost(IRequest request, AuthUser user)
    {
        string body;
        try
        {
            var content = request.GetBody(HeaderAccess.Retain);
            if (content is null)
                return Error(request, ResponseStatus.BadRequest, "malformed JSON body");
            using var document = JsonDocument.Parse(await content.AsMemoryAsync());
            body = document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("body", out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()!.Trim()
                : "";
        }
        catch (JsonException)
        {
            return Error(request, ResponseStatus.BadRequest, "malformed JSON body");
        }

        if (body.Length == 0)
            return Error(request, ResponseStatus.BadRequest, "body is required");
        if (body.Length > 500 && CountRunes(body) > 500)
            return Error(request, ResponseStatus.BadRequest, "body must be at most 500 characters");

        var pool = Postgres.TryPool();
        if (pool is null)
            return Error(request, ResponseStatus.InternalServerError, "internal server error");

        long id = 0;
        string createdAt = "";
        PgParam[] args = [PgParam.Int(user.Id), PgParam.Text(body)];
        await pool.QueryAsync(
            "INSERT INTO posts (user_id, body) VALUES ($1, $2) RETURNING id, created_at",
            args,
            row =>
            {
                id = Db.ParseLong(row.Field(0));
                createdAt = Db.FormatTimestamp(row.Field(1));
            });

        var post = new Post(id, body, createdAt, user.Username, 0);
        return Json(request, ResponseStatus.Created, Serialize(w =>
        {
            w.WriteStartObject();
            w.WritePropertyName("post");
            WritePost(w, post);
            w.WriteEndObject();
        }));
    }

    private static async ValueTask<IResponse> Like(IRequest request, AuthUser user, long postId)
    {
        var pool = Postgres.TryPool();
        if (pool is null)
            return Error(request, ResponseStatus.InternalServerError, "internal server error");

        PgParam[] args = [PgParam.Int(user.Id), PgParam.Int(postId)];
        PgResult result;
        try
        {
            result = await pool.QueryAsync(
                "INSERT INTO likes (user_id, post_id) VALUES ($1, $2) ON CONFLICT (user_id, post_id) DO NOTHING",
                args);
        }
        catch (PgException ex) when (ex.SqlState == "23503")
        {
            return Error(request, ResponseStatus.NotFound, "post not found");
        }

        var inserted = AffectedRows(result) > 0;
        return Json(request, inserted ? ResponseStatus.Created : ResponseStatus.Ok,
            Serialize(w =>
            {
                w.WriteStartObject();
                w.WriteBoolean("liked", true);
                w.WriteBoolean("already_liked", !inserted);
                w.WriteNumber("post_id", postId);
                w.WriteEndObject();
            }));
    }

    private static AuthResult Authenticate(IRequest request)
    {
        var header = GetHeader(request.Header.Headers, "authorization");
        if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal))
            return new AuthResult(null, "missing bearer token");

        try
        {
            var user = Jwt.Validate(header.AsSpan(7), _jwtKey, DateTimeOffset.UtcNow);
            return new AuthResult(user, null);
        }
        catch (JwtException ex)
        {
            return new AuthResult(null, ex.Message);
        }
    }

    private static string? GetHeader(IRequestHeaders headers, string name)
    {
        var wanted = Encoding.ASCII.GetBytes(name);
        for (var i = 0; i < headers.Count; i++)
        {
            var entry = headers.GetMemoryEntry(i);
            if (System.Text.Ascii.EqualsIgnoreCase(entry.Key.Span, wanted))
                return Encoding.UTF8.GetString(entry.Value.Span);
        }
        return null;
    }

    private static bool TryLikePath(string path, out string id)
    {
        id = "";
        if (!path.StartsWith("/posts/", StringComparison.Ordinal) ||
            !path.EndsWith("/like", StringComparison.Ordinal))
            return false;

        var raw = path.AsSpan(7, path.Length - 7 - 5);
        if (raw.Length == 0 || raw.IndexOf('/') >= 0)
            return false;
        id = raw.ToString();
        return true;
    }

    internal static bool TryParseId(string raw, out long id) =>
        long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;

    // INSERT ... ON CONFLICT DO NOTHING has no RETURNING, so PgResult.Rows is 0. The command
    // tag is "INSERT 0 1" or "INSERT 0 0"; its last token is the affected row count.
    private static int AffectedRows(PgResult result)
    {
        var tag = result.CommandTag;
        var lastSpace = tag.LastIndexOf(' ');
        if (lastSpace >= 0 && int.TryParse(tag.AsSpan(lastSpace + 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out var affected))
        {
            return affected;
        }
        return result.Rows;
    }

    private static string Serialize(Action<Utf8JsonWriter> write)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, JsonOptions))
            write(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WritePost(Utf8JsonWriter writer, Post post)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", post.Id);
        writer.WriteString("body", post.Body);
        writer.WriteString("created_at", post.CreatedAt);
        writer.WriteString("author", post.Author);
        writer.WriteNumber("like_count", post.LikeCount);
        writer.WriteEndObject();
    }

    private static string Degraded(string error) => Serialize(w =>
    {
        w.WriteStartObject();
        w.WriteString("status", "degraded");
        w.WriteString("db", "unreachable");
        w.WriteString("error", error);
        w.WriteEndObject();
    });

    private static IResponse Json(IRequest request, ResponseStatus status, string payload) =>
        request.Respond()
            .Status(status)
            .Content(new GenHTTP.Modules.IO.Strings.StringContent(payload, ContentType.ApplicationJson))
            .Build();

    private static IResponse Error(IRequest request, ResponseStatus status, string message) =>
        Json(request, status, Serialize(w =>
        {
            w.WriteStartObject();
            w.WriteString("error", message);
            w.WriteEndObject();
        }));

    private static int CountRunes(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
            count++;
        return count;
    }
}

internal readonly record struct AuthUser(long Id, string Username);

internal readonly record struct AuthResult(AuthUser? User, string? Error);

internal readonly record struct Post(long Id, string Body, string CreatedAt, string Author, int LikeCount);
