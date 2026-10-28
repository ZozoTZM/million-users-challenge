using System.Threading;
using System;
using System.Globalization;
using System.Net;
using System.Text;

namespace Feed;

internal static class Program
{
    private static int Main()
    {
        string databaseUrl = Required("DATABASE_URL");
        string jwtSecret = Required("JWT_SECRET");
        PgSettings db = PgSettings.Parse(databaseUrl);

        int loops = Integer("LOOPS", Integer("IOXIDE_REACTORS", Math.Min(Environment.ProcessorCount, 64)));
        loops = Math.Clamp(loops, 1, 64);
        int pool = Math.Clamp(Integer("DB_POOL", 64), loops, 512);
        int readers = Math.Max(1, Math.Min((pool + loops - 1) / loops, 512 / loops - 1));
        if (loops * (readers + 1) > 512)
        {
            throw new InvalidOperationException("too many loops for the 512-connection limit");
        }

        IPAddress host = IPAddress.TryParse(Environment.GetEnvironmentVariable("HOST") ?? "0.0.0.0", out IPAddress? parsed)
            ? parsed
            : IPAddress.Any;

        int port = Integer("PORT", 3000);
        int extra = Integer("EXTRA_PORT", 3001);
        int[] ports = extra == 0 || extra == port ? [port] : [port, extra];

        Loop.Conns = new Conn[Native.MaxFiles()];
        Loop.JwtKey = Encoding.UTF8.GetBytes(jwtSecret);
        Loop.StartedAt = Environment.TickCount64;
        Loop.GzipEnabled = Integer("GZIP", 1) != 0;
        Loop.GzipMin = Math.Max(0, Integer("GZIP_MIN", 1024));

        Console.Error.WriteLine(
            $"csharp-epoll loops={loops} readers_per_loop={readers} writer_per_loop=1 " +
            $"ports={string.Join(',', ports)} db={db.Host}:{db.Port}/{db.Database}");

        var threads = new Thread[loops];
        for (int i = 0; i < loops; i++)
        {
            var loop = new Loop(i, db, readers, host, ports);
            threads[i] = new Thread(loop.Run) { Name = $"loop-{i}", IsBackground = false };
            threads[i].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        return 0;
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"missing required environment variable {name}");

    private static int Integer(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None,
            CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
}
