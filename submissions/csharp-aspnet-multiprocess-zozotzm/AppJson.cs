using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;

static class AppJson
{
    public const string ContentType = "application/json; charset=utf-8";

    // The load-test corpus is ASCII. Relaxed escaping keeps non-ASCII and <>&/ literal, which the
    // spec explicitly allows, and avoids the default encoder's extra work on every string.
    public static readonly JsonWriterOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = true,
        Indented = false,
    };

    public static async Task WriteError(HttpContext ctx, int status, string message)
    {
        using var buffer = new PooledBufferWriter(128);
        using (var writer = new Utf8JsonWriter(buffer, Options))
        {
            writer.WriteStartObject();
            writer.WriteString("error", message);
            writer.WriteEndObject();
        }
        await Finish(ctx, status, buffer, compress: false);
    }

    public static async Task WriteHealth(HttpContext ctx, long uptimeSeconds)
    {
        using var buffer = new PooledBufferWriter(128);
        using (var writer = new Utf8JsonWriter(buffer, Options))
        {
            writer.WriteStartObject();
            writer.WriteString("status", "ok");
            writer.WriteString("db", "ok");
            writer.WriteNumber("uptime_s", uptimeSeconds);
            writer.WriteEndObject();
        }
        await Finish(ctx, 200, buffer, compress: false);
    }

    public static async Task WriteDegraded(HttpContext ctx, string error)
    {
        using var buffer = new PooledBufferWriter(256);
        using (var writer = new Utf8JsonWriter(buffer, Options))
        {
            writer.WriteStartObject();
            writer.WriteString("status", "degraded");
            writer.WriteString("db", "unreachable");
            writer.WriteString("error", error);
            writer.WriteEndObject();
        }
        await Finish(ctx, 503, buffer, compress: false);
    }

    // One async write with a known Content-Length. No chunked encoding, no synchronous flush on
    // Kestrel's response stream, and gzip only for bodies worth compressing.
    public static async Task Finish(HttpContext ctx, int status, PooledBufferWriter buffer, bool compress)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = ContentType;
        var json = buffer.WrittenMemory;

        if (compress && json.Length >= 1024)
        {
            var rented = ArrayPool<byte>.Shared.Rent(json.Length + 128);
            try
            {
                using var compressed = new MemoryStream(rented, 0, rented.Length, writable: true, publiclyVisible: true);
                compressed.SetLength(0); // the rented array may contain old bytes; start the stream empty
                using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                    gzip.Write(json.Span);
                var length = (int)compressed.Length;
                ctx.Response.Headers.ContentEncoding = "gzip";
                ctx.Response.Headers.Vary = "Accept-Encoding";
                ctx.Response.ContentLength = length;
                await ctx.Response.Body.WriteAsync(rented.AsMemory(0, length), ctx.RequestAborted);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
            return;
        }

        ctx.Response.ContentLength = json.Length;
        await ctx.Response.Body.WriteAsync(json, ctx.RequestAborted);
    }

    // "fff" truncates the microseconds, exactly as the spec requires.
    public static void WriteTimestamp(Utf8JsonWriter writer, string name, DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
            value = value.ToUniversalTime();
        Span<char> buffer = stackalloc char[24];
        if (!value.TryFormat(buffer, out var written, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            throw new InvalidOperationException("timestamp format failed");
        writer.WriteString(name, buffer[..written]);
    }
}

// Minimal ArrayPool-backed IBufferWriter so Utf8JsonWriter never flushes to Kestrel synchronously.
sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer;
    private int _written;

    public PooledBufferWriter(int initialCapacity)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, initialCapacity));
    }

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);
    public int WrittenCount => _written;

    public void Advance(int count) => _written += count;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void Ensure(int sizeHint)
    {
        if (sizeHint < 1)
            sizeHint = 1;
        if (_buffer.Length - _written >= sizeHint)
            return;

        var newSize = Math.Max(_buffer.Length * 2, _written + sizeHint);
        var replacement = ArrayPool<byte>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _written).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = replacement;
    }

    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = Array.Empty<byte>();
        _written = 0;
        if (buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(buffer);
    }
}
