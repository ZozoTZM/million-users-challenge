// Feed API (challenge SPEC.md) on the conventional ASP.NET Core stack:
// Kestrel, minimal APIs, JwtBearer, source-generated System.Text.Json, Npgsql.
//
// The only deliberate performance changes over examples/csharp are:
//   * source-generated JSON metadata instead of reflection
//   * a 256-connection Npgsql pool (the example found 128 -> 256 held more users)
//   * an 80% GC heap hard limit instead of 70%
//   * 16 KB Kestrel pipe buffers instead of the 64 KB / 1 MB defaults
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateSlimBuilder(args);

static string Env(IConfiguration configuration, string name, string? fallback = null) =>
    configuration[name] is { Length: > 0 } value
        ? value
        : fallback ?? throw new InvalidOperationException($"missing required environment variable {name}");

// The benchmark contract is DATABASE_URL (postgres://...). Aspire's WithReference injects the
// standard ConnectionStrings__feed value, so local dev can use the same binary.
var databaseUrl = builder.Configuration["DATABASE_URL"] is { Length: > 0 } configuredDatabase
    ? configuredDatabase
    : builder.Configuration.GetConnectionString("feed")
      ?? throw new InvalidOperationException("missing DATABASE_URL or ConnectionStrings:feed");
var jwtSecret = Env(builder.Configuration, "JWT_SECRET");
var host = Env(builder.Configuration, "HOST", "127.0.0.1");
var port = int.Parse(Env(builder.Configuration, "PORT", "3000"), CultureInfo.InvariantCulture);
int? extraPort = builder.Configuration["EXTRA_PORT"] is { Length: > 0 } ep
    ? int.Parse(ep, CultureInfo.InvariantCulture)
    : null;

var cores = Environment.ProcessorCount;
var poolSize = int.TryParse(Environment.GetEnvironmentVariable("DB_POOL"), out var dbPool) && dbPool > 0
    ? Math.Min(dbPool, 512)
    : Math.Min(256, 16 * cores);

// ---- Kestrel --------------------------------------------------------------------------------
builder.WebHost.UseSockets(o =>
{
    o.Backlog = 65535; // 1M clients connecting; kernel caps at net.core.somaxconn
    o.NoDelay = true;
    o.WaitForDataBeforeAllocatingBuffer = true; // no read buffer while a keep-alive connection is idle
    o.MaxReadBufferSize = 16 * 1024;
    o.MaxWriteBufferSize = 16 * 1024;
});

builder.WebHost.ConfigureKestrel(o =>
{
    o.AddServerHeader = false;
    o.Limits.MaxConcurrentConnections = null;
    o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(120); // clients idle up to ~40 s; spec needs >= 75 s
    o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
    o.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
    o.Limits.MaxRequestLineSize = 4 * 1024;
    o.Limits.MaxRequestBufferSize = 16 * 1024;
    o.Limits.MaxResponseBufferSize = 16 * 1024;

    void Bind(int p)
    {
        if (host is "0.0.0.0" or "*" or "")
            o.ListenAnyIP(p);
        else if (host == "localhost")
            o.ListenLocalhost(p);
        else
            o.Listen(IPAddress.Parse(host), p);
    }

    Bind(port);
    if (extraPort is int x)
        Bind(x);
});

// ---- JSON: source-generated metadata --------------------------------------------------------
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, FeedJsonContext.Default));

// ---- gzip for application/json (in the default MIME list) ----------------------------------
builder.Services.AddResponseCompression(o => o.Providers.Add<GzipCompressionProvider>());

// ---- auth: JwtBearer, with the short-key signature workaround -------------------------------
var jwtKey = Encoding.UTF8.GetBytes(jwtSecret);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false; // keep "sub" and "username" as-is
        o.TokenValidationParameters = new TokenValidationParameters
        {
            // IdentityModel refuses HMAC keys under 256 bits (the 23-byte challenge secret is
            // 184 bits), so the signature check is the documented SignatureValidator hook.
            SignatureValidator = (token, _) => Auth.VerifyHs256(token, jwtKey),
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = ctx =>
            {
                if (Auth.GetUser(ctx.Principal) is null) ctx.Fail(Auth.InvalidPayload);
                return Task.CompletedTask;
            },
            OnChallenge = async ctx =>
            {
                ctx.HandleResponse(); // SPEC bodies instead of the default empty 401
                var message = ctx.AuthenticateFailure switch
                {
                    null => "missing bearer token",
                    { Message: Auth.InvalidPayload } => Auth.InvalidPayload,
                    _ => "invalid or expired token",
                };
                await Results.Json(new ErrorResponse(message), FeedJsonContext.Default.ErrorResponse, statusCode: 401)
                    .ExecuteAsync(ctx.HttpContext);
            },
        };
    });
builder.Services.AddAuthorization();

// ---- Postgres -------------------------------------------------------------------------------
builder.Services.AddNpgsqlDataSource(Db.ToConnectionString(databaseUrl, poolSize));

var app = builder.Build();

app.UseResponseCompression();
app.UseExceptionHandler(errorApp => errorApp.Run(ctx =>
    Results.Json(new ErrorResponse("internal server error"), FeedJsonContext.Default.ErrorResponse, statusCode: 500)
        .ExecuteAsync(ctx)));

var started = Stopwatch.StartNew();

// ---- routes ---------------------------------------------------------------------------------
app.MapGet("/health", async (NpgsqlDataSource db) =>
{
    try
    {
        await using var cmd = db.CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync();
        return Results.Json(new HealthResponse("ok", "ok", (long)started.Elapsed.TotalSeconds),
            FeedJsonContext.Default.HealthResponse);
    }
    catch (Exception ex)
    {
        return Results.Json(new DegradedResponse("degraded", "unreachable", ex.Message),
            FeedJsonContext.Default.DegradedResponse, statusCode: 503);
    }
});

app.MapGet("/feed", async (NpgsqlDataSource db) =>
{
    var posts = new List<PostDto>(Db.FeedSize);
    await using var cmd = db.CreateCommand(Db.PostSelect + " ORDER BY p.created_at DESC, p.id DESC LIMIT $1");
    cmd.Parameters.AddWithValue(Db.FeedSize);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        posts.Add(Db.ReadPost(reader));
    return Results.Json(new FeedResponse(posts), FeedJsonContext.Default.FeedResponse);
});

app.MapGet("/posts/{id}", async (string id, NpgsqlDataSource db) =>
{
    if (!Db.TryParseId(id, out var postId))
        return Error(400, "invalid post id");

    await using var cmd = db.CreateCommand(Db.PostSelect + " WHERE p.id = $1");
    cmd.Parameters.AddWithValue(postId);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return Error(404, "post not found");

    return Results.Json(new PostResponse(Db.ReadPost(reader)), FeedJsonContext.Default.PostResponse);
});

app.MapPost("/posts", async (HttpRequest request, ClaimsPrincipal principal, NpgsqlDataSource db) =>
{
    var user = Auth.GetUser(principal)!.Value;
    string body;
    try
    {
        using var json = await JsonDocument.ParseAsync(request.Body);
        body = json.RootElement.ValueKind == JsonValueKind.Object
               && json.RootElement.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
            ? b.GetString()!.Trim()
            : "";
    }
    catch (JsonException)
    {
        return Error(400, "malformed JSON body");
    }

    if (body.Length == 0)
        return Error(400, "body is required");
    // The spec says characters (code points), not UTF-16 code units. The fast check is exact for
    // strings of at most 500 units; only longer strings need the rune count.
    if (body.Length > 500 && CountRunes(body) > 500)
        return Error(400, "body must be at most 500 characters");

    await using var cmd = db.CreateCommand("INSERT INTO posts (user_id, body) VALUES ($1, $2) RETURNING id, created_at");
    cmd.Parameters.AddWithValue(user.Id);
    cmd.Parameters.AddWithValue(body);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    var post = new PostDto(reader.GetInt64(0), body, Db.FormatTimestamp(reader.GetDateTime(1)),
        user.Username, 0);
    return Results.Json(new PostResponse(post), FeedJsonContext.Default.PostResponse, statusCode: 201);
}).RequireAuthorization();

app.MapPost("/posts/{id}/like", async (string id, ClaimsPrincipal principal, NpgsqlDataSource db) =>
{
    var user = Auth.GetUser(principal)!.Value;
    if (!Db.TryParseId(id, out var postId))
        return Error(400, "invalid post id");

    await using var cmd = db.CreateCommand(
        "INSERT INTO likes (user_id, post_id) VALUES ($1, $2) ON CONFLICT (user_id, post_id) DO NOTHING");
    cmd.Parameters.AddWithValue(user.Id);
    cmd.Parameters.AddWithValue(postId);

    int inserted;
    try
    {
        inserted = await cmd.ExecuteNonQueryAsync();
    }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
    {
        return Error(404, "post not found");
    }

    return Results.Json(new LikeResponse(true, inserted == 0, postId), FeedJsonContext.Default.LikeResponse,
        statusCode: inserted > 0 ? 201 : 200);
}).RequireAuthorization();

app.MapFallback(() => Error(404, "not found"));

Console.WriteLine(
    $"feed-api cores={cores} pool={poolSize} ports={port},{extraPort?.ToString() ?? "-"} " +
    $"server_gc={System.Runtime.GCSettings.IsServerGC} " +
    $"heap_limit_mb={GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >> 20}");

app.Run();

// ---- helpers --------------------------------------------------------------------------------
static IResult Error(int status, string message) =>
    Results.Json(new ErrorResponse(message), FeedJsonContext.Default.ErrorResponse, statusCode: status);

static int CountRunes(string text)
{
    var count = 0;
    foreach (var _ in text.EnumerateRunes())
        count++;
    return count;
}
