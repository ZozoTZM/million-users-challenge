using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using GenHTTP.Api.Infrastructure;
using GenHTTP.Api.Protocol;
using GenHTTP.Engine.Ioxide;
using GenHTTP.Modules.Compression;
using GenHTTP.Modules.IO;
using Microsoft.Extensions.Logging.Abstractions;
using FeedApi;

// GenHTTP 11 on the ioxide io_uring engine. GenHTTP does the HTTP work (routing is a custom
// IHandler, responses use GenHTTP content); Postgres uses ioxide.pg's per-reactor pool so a query
// does not hop to the thread pool. Linux 6.1+ only.
var hostName = Env("HOST", "0.0.0.0");
var port = int.Parse(Env("PORT", "3000"), CultureInfo.InvariantCulture);
var extraPort = int.TryParse(Env("EXTRA_PORT", ""), out var extra) ? extra : (int?)null;
var reactors = int.TryParse(Env("IOXIDE_REACTORS", ""), out var reactorCount) ? reactorCount : Environment.ProcessorCount;
var maxDbConnections = int.TryParse(Env("DATABASE_MAX_CONN", ""), out var dbConnections) ? dbConnections : 256;
var jwtSecret = Env("JWT_SECRET");

App.Configure(jwtSecret);
Postgres.Configure(reactors, maxDbConnections);
var effectiveDbConnections = Math.Min(maxDbConnections, reactors * 8);
Console.WriteLine($"feed-api genhttp-ioxide reactors={reactors} ports={port},{extraPort?.ToString() ?? "-"} db_pool_total={effectiveDbConnections}");

var address = IPAddress.TryParse(hostName, out var parsed) ? parsed : IPAddress.Any;

var server = Host.Create(
        onReactorStart: Postgres.Enabled ? Postgres.Start : null,
        options: new EngineOptions
        {
            Reactor = new ReactorOptions
            {
                ReactorCount = reactors,
            },
        })
    .Logging(NullLoggerFactory.Instance, false)
    .Handler(Handler.From(App.HandleAsync));

// GenHTTP's normal compression concern. Clients that do not send Accept-Encoding get plain JSON.
server = CompressedContent.Compression(server);

server.Bind(address, (ushort)port, HttpProtocols.Http1);
if (extraPort is int second)
    server.Bind(address, (ushort)second, HttpProtocols.Http1);

await server.RunAsync();

static string Env(string name, string? fallback = null) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : fallback ?? throw new InvalidOperationException($"missing required environment variable {name}");
