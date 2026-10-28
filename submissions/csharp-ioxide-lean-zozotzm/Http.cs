using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;

using ioxide;
using ioxide.pg;
using ioxide.utils;

namespace LeanApi;

/// <summary>
/// HTTP/1.1 on the raw ioxide recv/send API. The hot path deliberately owns no response buffer:
/// rows are appended to a pooled <see cref="JsonBuffer"/>, written into the connection's write
/// slab, and the buffer is returned. A connection waiting for its next request keeps only the
/// small unconsumed remainder of its last recv.
/// </summary>
internal static class Http
{
    private const int MaxHeadBytes = 16 * 1024;
    private const int MaxBodyBytes = 1 * 1024 * 1024;

    private static readonly Stopwatch Uptime = Stopwatch.StartNew();
    private static byte[] _jwtKey = [];
    private static bool _gzipEnabled = true;

    private static readonly byte[] ENotFound = "{\"error\":\"not found\"}"u8.ToArray();
    private static readonly byte[] EInvalidId = "{\"error\":\"invalid post id\"}"u8.ToArray();
    private static readonly byte[] EPostNotFound = "{\"error\":\"post not found\"}"u8.ToArray();
    private static readonly byte[] EMissingAuth = "{\"error\":\"missing bearer token\"}"u8.ToArray();
    private static readonly byte[] EInvalidToken = "{\"error\":\"invalid or expired token\"}"u8.ToArray();
    private static readonly byte[] EInvalidPayload = "{\"error\":\"invalid token payload\"}"u8.ToArray();
    private static readonly byte[] EMalformedJson = "{\"error\":\"malformed JSON body\"}"u8.ToArray();
    private static readonly byte[] EBodyRequired = "{\"error\":\"body is required\"}"u8.ToArray();
    private static readonly byte[] EBodyTooLong = "{\"error\":\"body must be at most 500 characters\"}"u8.ToArray();
    private static readonly byte[] EInternal = "{\"error\":\"internal server error\"}"u8.ToArray();
    private static readonly byte[] EBadRequest = "{\"error\":\"bad request\"}"u8.ToArray();

    public static void ConfigureJwt(string secret, bool gzip)
    {
        _jwtKey = Encoding.UTF8.GetBytes(secret);
        _gzipEnabled = gzip;
    }

    public static async Task HandleAsync(Reactor reactor, TcpConnection conn)
    {
        var session = new Session(conn, Db.TryPool(reactor));

        try
        {
            while (!session.Close)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();
                session.Feed(conn, in snapshot);

                if (await session.PumpAsync())
                {
                    await conn.FlushAsync();
                }

                if (snapshot.IsClosed)
                {
                    return;
                }

                conn.ResetRead();
            }

            conn.ShutdownWrite();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{Thread.CurrentThread.Name}] http handler crash: {ex.Message}");
        }
        finally
        {
            conn.DecRef();
        }
    }

    private sealed class Session
    {
        private const int NoConsumed = 0;

        private readonly TcpConnection _conn;
        private readonly PgPool? _pool;

        private byte[] _carry = [];
        private int _carryLength;
        private bool _wrote;

        // The buffer currently being assembled for a query. PgRowHandler runs inline on this
        // reactor, and a connection has at most one query in flight, so one field is enough.
        private JsonBuffer? _out;
        private bool _commaSeparatedRows;
        private bool _firstRow;
        private int _rowCount;
        private long _createdId;
        private string _createdAt = "";

        public bool Close { get; private set; }

        public Session(TcpConnection conn, PgPool? pool)
        {
            _conn = conn;
            _pool = pool;
        }

        public void Feed(TcpConnection conn, in RecvSnapshot snapshot)
        {
            while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
            {
                if (!item.HasBuffer)
                {
                    continue;
                }

                AppendCarry(item.AsSpan());
                conn.ReturnBuffer(in item);
            }
        }

        public async ValueTask<bool> PumpAsync()
        {
            while (!Close)
            {
                ParsedRequest request = Parse();
                if (request.Status == ParseStatus.Incomplete)
                {
                    break;
                }

                if (request.Status == ParseStatus.Malformed)
                {
                    Consume(_carryLength);
                    WriteError(EBadRequest, 400, close: true);
                    Close = true;
                    break;
                }

                Consume(request.Consumed);
                Close = request.Close;

                await DispatchAsync(request);

                if (Close)
                {
                    break;
                }
            }

            bool wrote = _wrote;
            _wrote = false;
            return wrote;
        }

        private async ValueTask DispatchAsync(ParsedRequest request)
        {
            switch (request.Route)
            {
                case Route.Health:
                    await HealthAsync(request.Close, request.Gzip);
                    break;
                case Route.Feed:
                    await FeedAsync(request.Close, request.Gzip);
                    break;
                case Route.GetPost:
                    await GetPostAsync(request.Id, request.Close, request.Gzip);
                    break;
                case Route.CreatePost:
                    await CreatePostAsync(request.User!.Value, request.Body!, request.Close, request.Gzip);
                    break;
                case Route.Like:
                    await LikeAsync(request.User!.Value, request.Id, request.Close, request.Gzip);
                    break;
                case Route.MissingAuth:
                    WriteError(EMissingAuth, 401, request.Close);
                    break;
                case Route.InvalidToken:
                    WriteError(EInvalidToken, 401, request.Close);
                    break;
                case Route.InvalidPayload:
                    WriteError(EInvalidPayload, 401, request.Close);
                    break;
                case Route.BadId:
                    WriteError(EInvalidId, 400, request.Close);
                    break;
                case Route.MalformedJson:
                    WriteError(EMalformedJson, 400, request.Close);
                    break;
                case Route.BodyRequired:
                    WriteError(EBodyRequired, 400, request.Close);
                    break;
                case Route.BodyTooLong:
                    WriteError(EBodyTooLong, 400, request.Close);
                    break;
                case Route.NotFound:
                default:
                    WriteError(ENotFound, 404, request.Close);
                    break;
            }
        }

        // ── endpoints ─────────────────────────────────────────────────────────────────────────

        private async ValueTask HealthAsync(bool close, bool gzip)
        {
            if (_pool is null)
            {
                WriteDegraded("database unavailable", close);
                return;
            }

            try
            {
                await _pool.QueryAsync("SELECT 1");

                using var buffer = new JsonBuffer(128);
                buffer.Append("{\"status\":\"ok\",\"db\":\"ok\",\"uptime_s\":"u8);
                buffer.AppendLong((long)Uptime.Elapsed.TotalSeconds);
                buffer.Append((byte)'}');
                WriteJson(buffer, 200, close, gzip);
            }
            catch (Exception ex)
            {
                WriteDegraded(ex.Message, close);
            }
        }

        private async ValueTask FeedAsync(bool close, bool gzip)
        {
            if (_pool is null)
            {
                WriteError(EInternal, 500, close);
                return;
            }

            using var buffer = new JsonBuffer(8192);
            _out = buffer;
            _commaSeparatedRows = true;
            _firstRow = true;
            _rowCount = 0;

            try
            {
                buffer.Append("{\"posts\":["u8);
                PgParam[] args = [PgParam.Int(20)];
                await _pool.QueryAsync(Db.FeedSql, args, AppendPostRow);
                buffer.Append("]}"u8);
                WriteJson(buffer, 200, close, gzip);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{Thread.CurrentThread.Name}] feed failed: {ex.Message}");
                WriteError(EInternal, 500, close);
            }
            finally
            {
                _out = null;
            }
        }

        private async ValueTask GetPostAsync(long id, bool close, bool gzip)
        {
            if (_pool is null)
            {
                WriteError(EInternal, 500, close);
                return;
            }

            using var buffer = new JsonBuffer(1024);
            _out = buffer;
            _commaSeparatedRows = false;
            _firstRow = true;
            _rowCount = 0;

            try
            {
                buffer.Append("{\"post\":"u8);
                PgParam[] args = [PgParam.Int(id)];
                await _pool.QueryAsync(Db.GetPostSql, args, AppendPostRow);

                if (_rowCount == 0)
                {
                    WriteError(EPostNotFound, 404, close);
                    return;
                }

                buffer.Append((byte)'}');
                WriteJson(buffer, 200, close, gzip);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{Thread.CurrentThread.Name}] get post failed: {ex.Message}");
                WriteError(EInternal, 500, close);
            }
            finally
            {
                _out = null;
            }
        }

        private async ValueTask CreatePostAsync(AuthUser user, string body, bool close, bool gzip)
        {
            if (_pool is null)
            {
                WriteError(EInternal, 500, close);
                return;
            }

            _createdId = 0;
            _createdAt = "";

            try
            {
                PgParam[] args = [PgParam.Int(user.Id), PgParam.Text(body)];
                await _pool.QueryAsync(Db.CreatePostSql, args, CaptureCreated);

                using var buffer = new JsonBuffer(512);
                buffer.Append("{\"post\":{\"id\":"u8);
                buffer.AppendLong(_createdId);
                buffer.Append(",\"body\":"u8);
                buffer.AppendJsonString(body);
                buffer.Append(",\"created_at\":"u8);
                buffer.AppendJsonString(_createdAt);
                buffer.Append(",\"author\":"u8);
                buffer.AppendJsonString(user.Username);
                buffer.Append(",\"like_count\":0}}"u8);
                WriteJson(buffer, 201, close, gzip);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{Thread.CurrentThread.Name}] create post failed: {ex.Message}");
                WriteError(EInternal, 500, close);
            }
        }

        private async ValueTask LikeAsync(AuthUser user, long postId, bool close, bool gzip)
        {
            if (_pool is null)
            {
                WriteError(EInternal, 500, close);
                return;
            }

            try
            {
                PgParam[] args = [PgParam.Int(user.Id), PgParam.Int(postId)];
                PgResult result = await _pool.QueryAsync(Db.LikeSql, args);

                bool inserted = AffectedRows(result) > 0;

                using var buffer = new JsonBuffer(128);
                buffer.Append("{\"liked\":true,\"already_liked\":"u8);
                buffer.Append(inserted ? "false"u8 : "true"u8);
                buffer.Append(",\"post_id\":"u8);
                buffer.AppendLong(postId);
                buffer.Append((byte)'}');
                WriteJson(buffer, inserted ? 201 : 200, close, gzip);
            }
            catch (PgException ex) when (ex.SqlState == "23503")
            {
                WriteError(EPostNotFound, 404, close);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{Thread.CurrentThread.Name}] like failed: {ex.Message}");
                WriteError(EInternal, 500, close);
            }
        }

        private void CaptureCreated(PgRow row)
        {
            _createdId = Db.ParseLong(row.Field(0));
            _createdAt = Db.FormatTimestamp(row.Field(1));
        }

        private void AppendPostRow(PgRow row)
        {
            JsonBuffer? buffer = _out;
            if (buffer is null)
            {
                return;
            }

            if (_commaSeparatedRows)
            {
                if (!_firstRow)
                {
                    buffer.Append((byte)',');
                }

                _firstRow = false;
            }

            _rowCount++;

            buffer.Append("{\"id\":"u8);
            buffer.AppendLong(Db.ParseLong(row.Field(0)));
            buffer.Append(",\"body\":"u8);
            buffer.AppendJsonString(row.Field(1));
            buffer.Append(",\"created_at\":"u8);
            buffer.AppendJsonString(Db.FormatTimestamp(row.Field(2)));
            buffer.Append(",\"author\":"u8);
            buffer.AppendJsonString(row.Field(3));
            buffer.Append(",\"like_count\":"u8);
            buffer.AppendLong(Db.ParseInt(row.Field(4)));
            buffer.Append((byte)'}');
        }

        private static int AffectedRows(PgResult result)
        {
            string tag = result.CommandTag;
            int lastSpace = tag.LastIndexOf(' ');
            if (lastSpace >= 0 &&
                int.TryParse(tag.AsSpan(lastSpace + 1), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int affected))
            {
                return affected;
            }

            return result.Rows;
        }

        // ── response writing ──────────────────────────────────────────────────────────────────

        private void WriteJson(JsonBuffer buffer, int status, bool close, bool gzip)
        {
            if (_gzipEnabled && gzip && buffer.Length >= 512)
            {
                byte[] compressed = Gzip(buffer.Span);
                WriteResponse(compressed, status, close, gzip: true);
                return;
            }

            WriteResponse(buffer.Span, status, close, gzip: false);
        }

        private void WriteDegraded(string error, bool close)
        {
            using var buffer = new JsonBuffer(256);
            buffer.Append("{\"status\":\"degraded\",\"db\":\"unreachable\",\"error\":"u8);
            buffer.AppendJsonString(error);
            buffer.Append((byte)'}');
            WriteResponse(buffer.Span, 503, close, gzip: false);
        }

        private void WriteError(ReadOnlySpan<byte> body, int status, bool close) =>
            WriteResponse(body, status, close, gzip: false);

        private void WriteResponse(ReadOnlySpan<byte> body, int status, bool close, bool gzip)
        {
            ReadOnlySpan<byte> statusLine = status switch
            {
                200 => "HTTP/1.1 200 OK\r\n"u8,
                201 => "HTTP/1.1 201 Created\r\n"u8,
                400 => "HTTP/1.1 400 Bad Request\r\n"u8,
                401 => "HTTP/1.1 401 Unauthorized\r\n"u8,
                404 => "HTTP/1.1 404 Not Found\r\n"u8,
                500 => "HTTP/1.1 500 Internal Server Error\r\n"u8,
                503 => "HTTP/1.1 503 Service Unavailable\r\n"u8,
                _ => "HTTP/1.1 500 Internal Server Error\r\n"u8,
            };

            Span<byte> head = stackalloc byte[256];
            int at = 0;
            Append(head, ref at, statusLine);
            Append(head, ref at, "Content-Type: application/json\r\n"u8);

            if (gzip)
            {
                Append(head, ref at, "Content-Encoding: gzip\r\nVary: Accept-Encoding\r\n"u8);
            }

            Append(head, ref at, "Content-Length: "u8);
            Span<byte> length = stackalloc byte[20];
            Utf8Formatter.TryFormat(body.Length, length, out int lengthWritten);
            Append(head, ref at, length[..lengthWritten]);

            if (close)
            {
                Append(head, ref at, "\r\nConnection: close"u8);
            }

            Append(head, ref at, "\r\n\r\n"u8);

            _conn.Write(head[..at]);
            _conn.Write(body);
            _wrote = true;
        }

        private static void Append(Span<byte> destination, ref int at, ReadOnlySpan<byte> value)
        {
            value.CopyTo(destination[at..]);
            at += value.Length;
        }

        private static byte[] Gzip(ReadOnlySpan<byte> body)
        {
            using var output = new MemoryStream(body.Length / 2 + 32);
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(body);
            }

            return output.ToArray();
        }

        // ── parser ───────────────────────────────────────────────────────────────────────────

        private ParsedRequest Parse()
        {
            ReadOnlySpan<byte> input = _carry.AsSpan(0, _carryLength);
            int headEnd = input.IndexOf("\r\n\r\n"u8);
            if (headEnd < 0)
            {
                return input.Length > MaxHeadBytes
                    ? ParsedRequest.Malformed()
                    : ParsedRequest.Incomplete();
            }

            ReadOnlySpan<byte> head = input[..headEnd];
            int requestLineEnd = head.IndexOf("\r\n"u8);
            if (requestLineEnd < 0)
            {
                return ParsedRequest.Malformed();
            }

            ReadOnlySpan<byte> requestLine = head[..requestLineEnd];
            int firstSpace = requestLine.IndexOf((byte)' ');
            if (firstSpace <= 0)
            {
                return ParsedRequest.Malformed();
            }

            ReadOnlySpan<byte> method = requestLine[..firstSpace];
            ReadOnlySpan<byte> afterMethod = requestLine[(firstSpace + 1)..];
            int secondSpace = afterMethod.IndexOf((byte)' ');
            if (secondSpace <= 0)
            {
                return ParsedRequest.Malformed();
            }

            ReadOnlySpan<byte> target = afterMethod[..secondSpace];
            int queryMark = target.IndexOf((byte)'?');
            ReadOnlySpan<byte> path = queryMark < 0 ? target : target[..queryMark];

            int contentLength = 0;
            bool chunked = false;
            bool close = false;
            bool gzip = false;
            string? authorization = null;

            ReadOnlySpan<byte> headers = head[(requestLineEnd + 2)..];
            while (!headers.IsEmpty)
            {
                int lineEnd = headers.IndexOf("\r\n"u8);
                ReadOnlySpan<byte> line = lineEnd < 0 ? headers : headers[..lineEnd];
                headers = lineEnd < 0 ? default : headers[(lineEnd + 2)..];

                int colon = line.IndexOf((byte)':');
                if (colon < 0)
                {
                    continue;
                }

                ReadOnlySpan<byte> name = line[..colon];
                ReadOnlySpan<byte> value = line[(colon + 1)..].Trim((byte)' ');

                if (Ascii.EqualsIgnoreCase(name, "content-length"u8))
                {
                    Utf8Parser.TryParse(value, out contentLength, out _);
                }
                else if (Ascii.EqualsIgnoreCase(name, "transfer-encoding"u8))
                {
                    chunked = CiContains(value, "chunked"u8);
                }
                else if (Ascii.EqualsIgnoreCase(name, "connection"u8))
                {
                    close = CiContains(value, "close"u8);
                }
                else if (Ascii.EqualsIgnoreCase(name, "accept-encoding"u8))
                {
                    gzip = CiContains(value, "gzip"u8);
                }
                else if (Ascii.EqualsIgnoreCase(name, "authorization"u8))
                {
                    authorization = Encoding.UTF8.GetString(value);
                }
            }

            int bodyStart = headEnd + 4;
            ReadOnlySpan<byte> body;
            int wireLength;

            if (chunked)
            {
                if (!TryDecodeChunked(input[bodyStart..], out byte[]? decoded, out wireLength))
                {
                    return input.Length - bodyStart > MaxBodyBytes
                        ? ParsedRequest.Malformed()
                        : ParsedRequest.Incomplete();
                }

                if (decoded is null || decoded.Length > MaxBodyBytes)
                {
                    return ParsedRequest.Malformed();
                }

                body = decoded;
            }
            else
            {
                if ((uint)contentLength > MaxBodyBytes)
                {
                    return ParsedRequest.Malformed();
                }

                if (input.Length - bodyStart < contentLength)
                {
                    return ParsedRequest.Incomplete();
                }

                body = input.Slice(bodyStart, contentLength);
                wireLength = contentLength;
            }

            int consumed = bodyStart + wireLength;
            bool isGet = method.SequenceEqual("GET"u8);
            bool isPost = method.SequenceEqual("POST"u8);

            if (isGet)
            {
                if (path.SequenceEqual("/health"u8))
                {
                    return ParsedRequest.Complete(consumed, Route.Health, close, gzip);
                }

                if (path.SequenceEqual("/feed"u8))
                {
                    return ParsedRequest.Complete(consumed, Route.Feed, close, gzip);
                }

                if (path.StartsWith("/posts/"u8))
                {
                    ReadOnlySpan<byte> idText = path[7..];
                    if (!TryParseId(idText, out long id))
                    {
                        return ParsedRequest.Complete(consumed, Route.BadId, close, gzip);
                    }

                    return ParsedRequest.Complete(consumed, Route.GetPost, close, gzip, id);
                }

                return ParsedRequest.Complete(consumed, Route.NotFound, close, gzip);
            }

            if (isPost)
            {
                if (path.SequenceEqual("/posts"u8))
                {
                    if (!TryAuthenticate(authorization, out AuthUser? user, out Route authError))
                    {
                        return ParsedRequest.Complete(consumed, authError, close, gzip);
                    }

                    if (!TryParseBody(body, out string? text, out Route bodyError))
                    {
                        return ParsedRequest.Complete(consumed, bodyError, close, gzip);
                    }

                    return ParsedRequest.Complete(consumed, Route.CreatePost, close, gzip, body: text, user: user);
                }

                if (path.StartsWith("/posts/"u8) && path.EndsWith("/like"u8))
                {
                    // Auth is checked before the id, as the API requires.
                    if (!TryAuthenticate(authorization, out AuthUser? user, out Route authError))
                    {
                        return ParsedRequest.Complete(consumed, authError, close, gzip);
                    }

                    ReadOnlySpan<byte> idText = path[7..^5];
                    if (!TryParseId(idText, out long id))
                    {
                        return ParsedRequest.Complete(consumed, Route.BadId, close, gzip);
                    }

                    return ParsedRequest.Complete(consumed, Route.Like, close, gzip, id, user: user);
                }
            }

            return ParsedRequest.Complete(consumed, Route.NotFound, close, gzip);
        }

        private static bool TryAuthenticate(string? header, out AuthUser? user, out Route error)
        {
            user = null;
            error = Route.MissingAuth;

            if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                user = Jwt.Validate(header.AsSpan(7), _jwtKey, DateTimeOffset.UtcNow);
                return true;
            }
            catch (JwtException ex)
            {
                error = ex.Message == "invalid token payload"
                    ? Route.InvalidPayload
                    : Route.InvalidToken;
                return false;
            }
        }

        private static bool TryParseBody(ReadOnlySpan<byte> body, out string? text, out Route error)
        {
            text = null;
            error = Route.MalformedJson;

            try
            {
                using JsonDocument document = JsonDocument.Parse(body.ToArray());
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("body", out JsonElement value) ||
                    value.ValueKind != JsonValueKind.String)
                {
                    error = Route.BodyRequired;
                    return false;
                }

                text = (value.GetString() ?? "").Trim();
                if (text.Length == 0)
                {
                    error = Route.BodyRequired;
                    return false;
                }

                if (CountRunes(text) > 500)
                {
                    error = Route.BodyTooLong;
                    return false;
                }

                return true;
            }
            catch (JsonException)
            {
                error = Route.MalformedJson;
                return false;
            }
        }

        private static bool TryParseId(ReadOnlySpan<byte> text, out long id)
        {
            if (!Utf8Parser.TryParse(text, out id, out int consumed) ||
                consumed != text.Length ||
                id <= 0)
            {
                id = 0;
                return false;
            }

            return true;
        }

        private static int CountRunes(string text)
        {
            int count = 0;
            foreach (Rune _ in text.EnumerateRunes())
            {
                count++;
            }

            return count;
        }

        private static bool TryDecodeChunked(ReadOnlySpan<byte> data, out byte[]? body, out int wireLength)
        {
            body = null;
            wireLength = 0;

            using var decoded = new MemoryStream();
            int at = 0;

            while (true)
            {
                int lineEnd = data[at..].IndexOf("\r\n"u8);
                if (lineEnd < 0)
                {
                    return false;
                }

                if (!Utf8Parser.TryParse(data.Slice(at, lineEnd), out int size, out _, 'x') ||
                    size < 0 ||
                    size > MaxBodyBytes)
                {
                    return false;
                }

                at += lineEnd + 2;

                if (size == 0)
                {
                    int trailer = data[at..].IndexOf("\r\n"u8);
                    if (trailer < 0)
                    {
                        return false;
                    }

                    wireLength = at + trailer + 2;
                    body = decoded.ToArray();
                    return true;
                }

                if (data.Length - at < size + 2)
                {
                    return false;
                }

                decoded.Write(data.Slice(at, size));
                at += size + 2;
            }
        }

        // ── carry ────────────────────────────────────────────────────────────────────────────

        private void AppendCarry(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
            {
                return;
            }

            int needed = _carryLength + data.Length;
            if (_carry.Length < needed)
            {
                Array.Resize(ref _carry, Math.Max(needed, Math.Max(256, _carry.Length * 2)));
            }

            data.CopyTo(_carry.AsSpan(_carryLength));
            _carryLength = needed;
        }

        private void Consume(int count)
        {
            if (count <= 0)
            {
                return;
            }

            if (count >= _carryLength)
            {
                _carryLength = 0;
                return;
            }

            Buffer.BlockCopy(_carry, count, _carry, 0, _carryLength - count);
            _carryLength -= count;
        }
    }

    // ── parsed request ────────────────────────────────────────────────────────────────────────

    private enum ParseStatus
    {
        Incomplete,
        Complete,
        Malformed,
    }

    private enum Route
    {
        NotFound,
        Health,
        Feed,
        GetPost,
        CreatePost,
        Like,
        MissingAuth,
        InvalidToken,
        InvalidPayload,
        BadId,
        MalformedJson,
        BodyRequired,
        BodyTooLong,
    }

    private readonly struct ParsedRequest
    {
        public readonly ParseStatus Status;
        public readonly Route Route;
        public readonly int Consumed;
        public readonly long Id;
        public readonly string? Body;
        public readonly AuthUser? User;
        public readonly bool Close;
        public readonly bool Gzip;

        private ParsedRequest(ParseStatus status, Route route, int consumed, long id,
            string? body, AuthUser? user, bool close, bool gzip)
        {
            Status = status;
            Route = route;
            Consumed = consumed;
            Id = id;
            Body = body;
            User = user;
            Close = close;
            Gzip = gzip;
        }

        public static ParsedRequest Incomplete() =>
            new(ParseStatus.Incomplete, Route.NotFound, 0, 0, null, null, false, false);

        public static ParsedRequest Malformed() =>
            new(ParseStatus.Malformed, Route.NotFound, 0, 0, null, null, true, false);

        public static ParsedRequest Complete(int consumed, Route route, bool close, bool gzip,
            long id = 0, string? body = null, AuthUser? user = null) =>
            new(ParseStatus.Complete, route, consumed, id, body, user, close, gzip);
    }

    // ── byte helpers ─────────────────────────────────────────────────────────────────────────

    private static bool CiContains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty || haystack.Length < needle.Length)
        {
            return false;
        }

        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (Ascii.EqualsIgnoreCase(haystack.Slice(i, needle.Length), needle))
            {
                return true;
            }
        }

        return false;
    }
}
