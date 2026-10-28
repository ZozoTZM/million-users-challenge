using System.Globalization;
using System.Runtime.InteropServices;

using ioxide;

namespace LeanApi;

/// <summary>
/// A deliberately lean C# HTTP/1.1 server on the raw ioxide ring API.
///
/// There is no HTTP framework here: the parser, keep-alive, routing and response framing are in
/// <see cref="Http"/>. One reactor thread owns one ring and one SO_REUSEPORT pair of listeners,
/// and ioxide.pg is opened on the same thread. The only per-connection state is the small carry
/// needed for a partial request and a reference to the reactor's Postgres pool.
/// </summary>
internal static class Program
{
    private static PosixSignalRegistration? _sigTerm;
    private static PosixSignalRegistration? _sigInt;

    private static int Main()
    {
        InstallSignalHandlers();

        ushort port = Port("PORT", 3000);
        ushort extraPort = Port("EXTRA_PORT", 0);
        int reactors = Int("IOXIDE_REACTORS", Math.Min(Environment.ProcessorCount, 64));

        int writeSlabKb = Int("IOXIDE_WRITE_SLAB_KB", 4);
        bool gzip = Int("IOXIDE_LEAN_GZIP", 1) != 0;

        Http.ConfigureJwt(Env("JWT_SECRET"), gzip);
        Db.Configure(reactors);

        ushort[] extras = extraPort == 0 ? [] : [extraPort];

        var config = new ServerConfig
        {
            ReactorCount = reactors,
            // One shared receive ring per reactor, not a buffer per connection. 256 x 16 KB is
            // enough in-flight receive memory for the load profile and stays small on a 16-core box.
            RecvBufferSize = 16 * 1024,
            RecvSlots = 256,
            Tcp = new TcpOptions
            {
                Port = port,
                ExtraPorts = extras,
                ListenBacklog = 65_535,
                // A 4 KB primary slab keeps the idle cost down. Responses bigger than that spill
                // into pooled slabs and leave with one vectored send (Segmented, not Grow), so a
                // 20-post feed does not permanently grow every connection's slab.
                WriteSlabSize = writeSlabKb * 1024,
                WriteOverflow = WriteOverflowStrategy.Segmented,
                // NULL-users holds idle keep-alive connections for ~40 s between requests; do not
                // reap them early.
                ReadTimeoutMs = 0,
                SendTimeoutMs = 0,
            },
        };

        Console.WriteLine(
            $"feed-api csharp-ioxide-lean reactors={reactors} ports={port},{extraPort} " +
            $"db_pool_total={Db.EffectiveConnections} write_slab={writeSlabKb}k segmented gzip={gzip}");

        var threads = new Thread[reactors];
        for (int i = 0; i < threads.Length; i++)
        {
            var reactor = new Reactor(i, config);

            reactor.OnStart = r => Db.Start(r);
            reactor.TcpHandle = Http.HandleAsync;

            threads[i] = new Thread(reactor.Run) { Name = $"reactor-{i}" };
            threads[i].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        return 0;
    }

    private static void InstallSignalHandlers()
    {
        _sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            Environment.Exit(0);
        });
        _sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
        {
            ctx.Cancel = true;
            Environment.Exit(0);
        });
    }

    private static string Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"missing required environment variable {name}");

    private static ushort Port(string name, ushort fallback) =>
        ushort.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None,
            CultureInfo.InvariantCulture, out ushort value)
            ? value
            : fallback;

    private static int Int(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None,
            CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
}
