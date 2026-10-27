// Feed API (challenge SPEC.md) on the standard ASP.NET Core stack: Kestrel, Npgsql, System.Text.Json.
//
// This is deliberately the normal stack (no hand-written HTTP, no unsafe code, no custom Postgres
// protocol). The changes are deployment/runtime shape and allocation discipline:
//   * several Kestrel processes with SO_REUSEPORT, each with its own bounded GC heap and DB pool
//   * responses written straight to the response pipe with Utf8JsonWriter (no Results.Json)
//   * gzip only for the feed (the one response that is large); small JSON stays plain
//   * a small AuthenticationHandler for HS256 instead of the JwtBearer/IdentityModel claims pipeline
//   * separate read and write Npgsql pools so a commit never queues reads
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Runtime;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

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

// ---- process shape -------------------------------------------------------------------------
// The benchmark scores the whole app, not a process, so a few Kestrel processes are allowed and
// useful: each has its own heap and GC, and SO_REUSEPORT spreads new connections over them. Eight
// workers is enough to isolate GC pauses without multiplying runtime/thread-pool overhead 16x.
var cores = Environment.ProcessorCount;
var workers = int.TryParse(Environment.GetEnvironmentVariable("WORKERS"), out var requestedWorkers) && requestedWorkers > 0
    ? Math.Min(requestedWorkers, cores)
    : Math.Min(cores, 8);
if (!OperatingSystem.IsLinux())
    workers = 1; // SO_REUSEPORT listener setup below is Linux-specific; tests on Windows stay single-process
var workerIndex = int.TryParse(Environment.GetEnvironmentVariable("WORKER_INDEX"), out var wi) ? wi : 0;

// Total app-wide Postgres connections. The example went 128 -> 256 (665k -> 700k users), so 256 is
// the default here too. DB_POOL is the total across workers, not per process.
var totalPool = int.TryParse(Environment.GetEnvironmentVariable("DB_POOL"), out var dbPool) && dbPool > 0
    ? Math.Min(dbPool, 512)
    : Math.Min(256, 16 * cores);
var perWorkerPool = Math.Max(2, totalPool / workers);
var readPool = int.TryParse(Environment.GetEnvironmentVariable("DB_POOL_READ"), out var rp) && rp > 0
    ? rp
    : Math.Max(1, perWorkerPool * 3 / 4);
var writePool = int.TryParse(Environment.GetEnvironmentVariable("DB_POOL_WRITE"), out var wp) && wp > 0
    ? wp
    : Math.Max(1, perWorkerPool - readPool);

// ---- Kestrel --------------------------------------------------------------------------------
builder.WebHost.UseSockets(o =>
{
    o.Backlog = 65535; // 1M clients connecting; kernel caps at net.core.somaxconn (65535)
    o.NoDelay = true;
    o.WaitForDataBeforeAllocatingBuffer = true; // no read buffer while a keep-alive connection is idle
    o.MaxReadBufferSize = 16 * 1024;            // a request here is < 1 KB; don't let one connection pin MBs
    o.MaxWriteBufferSize = 16 * 1024;           // responses are ~2-12 KB
    if (workers > 1)
    {
        // One or two socket queues per process, not one per core in every process.
        o.IOQueueCount = Math.Max(1, cores / workers);
        o.CreateBoundListenSocket = ListenSocket.Create;
    }
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

// ---- auth -----------------------------------------------------------------------------------
// Standard ASP.NET Core authentication pipeline, but with a small handler that does exactly what
// the spec asks: HS256 only, signature and exp on every request, then sub/username validation.
builder.Services.AddSingleton(new JwtKey(Encoding.UTF8.GetBytes(jwtSecret)));
builder.Services.AddAuthentication(JwtAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, JwtAuthHandler>(JwtAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

// ---- Postgres -------------------------------------------------------------------------------
builder.Services.AddKeyedSingleton<NpgsqlDataSource>("read", (_, _) =>
    NpgsqlDataSource.Create(Db.BuildConnectionString(databaseUrl, readPool)));
builder.Services.AddKeyedSingleton<NpgsqlDataSource>("write", (_, _) =>
    NpgsqlDataSource.Create(Db.BuildConnectionString(databaseUrl, writePool)));

var app = builder.Build();

var readDb = app.Services.GetRequiredKeyedService<NpgsqlDataSource>("read");
var writeDb = app.Services.GetRequiredKeyedService<NpgsqlDataSource>("write");

// Async request handlers are short; a small floor avoids thread-pool hill climbing during bursts.
ThreadPool.GetMinThreads(out var minWorkerThreads, out var minIoThreads);
var minThreads = Math.Max(8, cores * 2 / Math.Max(1, workers));
ThreadPool.SetMinThreads(Math.Max(minWorkerThreads, minThreads), Math.Max(minIoThreads, minThreads));

var started = Stopwatch.StartNew();

// ---- errors ---------------------------------------------------------------------------------
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
    {
        // client went away; nothing to answer
    }
    catch (Exception ex) when (!ctx.Response.HasStarted)
    {
        ctx.Response.Clear();
        await AppJson.WriteError(ctx, 500, "internal server error");
        app.Logger.LogError(ex, "request failed");
    }
});

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// ---- endpoints ------------------------------------------------------------------------------
app.MapGet("/health", async (HttpContext ctx) =>
{
    try
    {
        await using var cmd = readDb.CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync(ctx.RequestAborted);
        await AppJson.WriteHealth(ctx, (long)started.Elapsed.TotalSeconds);
    }
    catch (Exception ex)
    {
        if (ctx.Response.HasStarted) throw;
        ctx.Response.Clear();
        await AppJson.WriteDegraded(ctx, ex.Message);
    }
});

app.MapGet("/feed", async (HttpContext ctx) =>
{
    var gzip = ctx.Request.Headers.AcceptEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase);
    using var buffer = new PooledBufferWriter(8 * 1024);
    using (var writer = new Utf8JsonWriter(buffer, AppJson.Options))
    {
        writer.WriteStartObject();
        writer.WriteStartArray("posts");

        await using (var cmd = readDb.CreateCommand(Db.PostSelect + " ORDER BY p.created_at DESC, p.id DESC LIMIT $1"))
        {
            cmd.Parameters.AddWithValue(Db.FeedSize);
            await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SingleResult, ctx.RequestAborted);
            while (await reader.ReadAsync(ctx.RequestAborted))
                WritePost(writer, reader);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
    await AppJson.Finish(ctx, 200, buffer, gzip);
});

app.MapGet("/posts/{id}", async (HttpContext ctx) =>
{
    if (!Db.TryParseId(ctx.Request.RouteValues["id"] as string, out var id))
    {
        await AppJson.WriteError(ctx, 400, "invalid post id");
        return;
    }

    await using var cmd = readDb.CreateCommand(Db.PostSelect + " WHERE p.id = $1");
    cmd.Parameters.AddWithValue(id);
    await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SingleResult, ctx.RequestAborted);
    if (!await reader.ReadAsync(ctx.RequestAborted))
    {
        await AppJson.WriteError(ctx, 404, "post not found");
        return;
    }

    using var buffer = new PooledBufferWriter(512);
    using (var writer = new Utf8JsonWriter(buffer, AppJson.Options))
    {
        writer.WriteStartObject();
        writer.WritePropertyName("post");
        WritePost(writer, reader);
        writer.WriteEndObject();
    }
    await AppJson.Finish(ctx, 200, buffer, compress: false);
});

app.MapPost("/posts", async (HttpContext ctx) =>
{
    var user = (AuthUser)ctx.Items["user"]!;

    var (status, text) = await BodyReader.ReadAsync(ctx.Request);
    if (status == BodyStatus.Malformed)
    {
        await AppJson.WriteError(ctx, 400, "malformed JSON body");
        return;
    }
    if (text.Length == 0)
    {
        await AppJson.WriteError(ctx, 400, "body is required");
        return;
    }
    // The spec says characters, i.e. code points, not UTF-16 code units. The fast check is exact
    // for every string of at most 500 units; only longer strings need the rune count.
    if (text.Length > 500 && CountRunes(text) > 500)
    {
        await AppJson.WriteError(ctx, 400, "body must be at most 500 characters");
        return;
    }

    await using var cmd = writeDb.CreateCommand("INSERT INTO posts (user_id, body) VALUES ($1, $2) RETURNING id, created_at");
    cmd.Parameters.AddWithValue(user.Id);
    cmd.Parameters.AddWithValue(text);
    await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SingleResult, ctx.RequestAborted);
    await reader.ReadAsync(ctx.RequestAborted);

    using var buffer = new PooledBufferWriter(512);
    using (var writer = new Utf8JsonWriter(buffer, AppJson.Options))
    {
        writer.WriteStartObject();
        writer.WriteStartObject("post");
        writer.WriteNumber("id", reader.GetInt64(0));
        writer.WriteString("body", text);
        AppJson.WriteTimestamp(writer, "created_at", reader.GetDateTime(1));
        writer.WriteString("author", user.Username);
        writer.WriteNumber("like_count", 0);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
    await AppJson.Finish(ctx, 201, buffer, compress: false);
}).RequireAuthorization();

app.MapPost("/posts/{id}/like", async (HttpContext ctx) =>
{
    var user = (AuthUser)ctx.Items["user"]!;
    if (!Db.TryParseId(ctx.Request.RouteValues["id"] as string, out var id))
    {
        await AppJson.WriteError(ctx, 400, "invalid post id");
        return;
    }

    await using var cmd = writeDb.CreateCommand(
        "INSERT INTO likes (user_id, post_id) VALUES ($1, $2) ON CONFLICT (user_id, post_id) DO NOTHING");
    cmd.Parameters.AddWithValue(user.Id);
    cmd.Parameters.AddWithValue(id);

    int inserted;
    try
    {
        inserted = await cmd.ExecuteNonQueryAsync(ctx.RequestAborted);
    }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
    {
        await AppJson.WriteError(ctx, 404, "post not found");
        return;
    }

    using var buffer = new PooledBufferWriter(128);
    using (var writer = new Utf8JsonWriter(buffer, AppJson.Options))
    {
        writer.WriteStartObject();
        writer.WriteBoolean("liked", true);
        writer.WriteBoolean("already_liked", inserted == 0);
        writer.WriteNumber("post_id", id);
        writer.WriteEndObject();
    }
    await AppJson.Finish(ctx, inserted > 0 ? 201 : 200, buffer, compress: false);
}).RequireAuthorization();

app.MapFallback(async (HttpContext ctx) => await AppJson.WriteError(ctx, 404, "not found"));

Console.WriteLine(
    $"feed-api workers={workers}/{cores} index={workerIndex} ports={port},{extraPort?.ToString() ?? "-"} " +
    $"pools=read:{readPool},write:{writePool},total:{workers * (readPool + writePool)} " +
    $"server_gc={GCSettings.IsServerGC} heap_limit_mb={GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >> 20}");

await app.RunAsync();

// ---- helpers --------------------------------------------------------------------------------
static void WritePost(Utf8JsonWriter writer, NpgsqlDataReader reader)
{
    writer.WriteStartObject();
    writer.WriteNumber("id", reader.GetInt64(0));
    writer.WriteString("body", reader.GetString(1));
    AppJson.WriteTimestamp(writer, "created_at", reader.GetDateTime(2));
    writer.WriteString("author", reader.GetString(3));
    writer.WriteNumber("like_count", reader.GetInt32(4));
    writer.WriteEndObject();
}

static int CountRunes(string text)
{
    var count = 0;
    foreach (var _ in text.EnumerateRunes())
        count++;
    return count;
}
