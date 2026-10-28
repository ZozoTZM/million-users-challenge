using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;

namespace Feed;

internal enum RequestKind : byte
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

internal struct Conn
{
    public int Gen;
    public bool Busy;
    public bool CloseAfterSend;
    public bool PeerDone;
    public byte[]? In;
    public int InLength;
    public byte[]? Out;
    public int OutStart;
    public int OutEnd;
}

internal sealed class Loop
{
    private const int MaxRequest = 64 * 1024;
    private const int MaxAccepts = 256;
    private const ulong ListenerTag = 1UL << 40;
    private const ulong PgTag = 2UL << 40;

    public static Conn[] Conns = [];
    public static byte[] JwtKey = [];
    public static long StartedAt;
    public static bool GzipEnabled;
    public static int GzipMin;

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

    private readonly int _index;
    private readonly int _epfd;
    private readonly Socket[] _listeners;
    private readonly PgConnection?[] _pgs;
    private readonly PgConnection?[] _readers;
    private readonly PgConnection? _writer;
    private readonly Queue<PgRequest> _pendingReads = new();
    private readonly Queue<PgRequest> _pendingWrites = new();

    private readonly byte[] _receive = new byte[MaxRequest];
    private readonly byte[] _response = new byte[1 << 20];
    private int _nextReader;

    public Loop(int index, PgSettings db, int readers, IPAddress host, int[] ports)
    {
        _index = index;
        _epfd = Native.epoll_create1(0);
        if (_epfd < 0)
        {
            throw new InvalidOperationException("epoll_create1 failed");
        }

        _listeners = new Socket[ports.Length];
        for (int i = 0; i < ports.Length; i++)
        {
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.SetRawSocketOption(Native.SolSocket, Native.SoReusePort, BitConverter.GetBytes(1));
            listener.Bind(new IPEndPoint(host, ports[i]));
            listener.Listen(65_535);
            Native.SetTcpDeferAccept((int)listener.Handle);
            listener.Blocking = false;
            _listeners[i] = listener;
            Watch((int)listener.Handle, Native.EpollIn, ListenerTag | (uint)i);
        }

        int total = readers + 1;
        _pgs = new PgConnection?[total];
        _readers = new PgConnection?[readers];
        for (int i = 0; i < total; i++)
        {
            try
            {
                var pg = new PgConnection(this, i, db);
                _pgs[i] = pg;
                if (i < readers)
                {
                    _readers[i] = pg;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[loop {_index}] postgres connection {i} failed: {ex.Message}");
            }
        }

        _writer = _pgs[readers];
    }

    public unsafe void Run()
    {
        const int MaxEvents = 1024;
        EpollEvent* events = stackalloc EpollEvent[MaxEvents];

        while (true)
        {
            int count = Native.epoll_wait(_epfd, events, MaxEvents, -1);
            if (count < 0)
            {
                int error = Native.LastError;
                if (error == Native.Eintr)
                {
                    continue;
                }
                throw new InvalidOperationException($"epoll_wait failed: {error}");
            }

            for (int i = 0; i < count; i++)
            {
                ulong data = events[i].Data;
                uint ready = events[i].Events;
                if (data >= PgTag)
                {
                    OnPg((int)(data - PgTag), ready);
                }
                else if (data >= ListenerTag)
                {
                    OnListener((int)(data - ListenerTag));
                }
                else
                {
                    OnClient((int)data, ready);
                }
            }
        }
    }

    public void WatchPg(PgConnection pg, bool wantWrite)
    {
        uint events = Native.EpollIn | (wantWrite ? Native.EpollOut : 0);
        Watch(pg.Fd, events, PgTag | (uint)pg.Index);
    }

    private void Watch(int fd, uint events, ulong tag)
    {
        var ev = new EpollEvent { Events = events, Data = tag };
        int op = Native.EpollCtlAdd;
        if (Native.epoll_ctl(_epfd, op, fd, ref ev) != 0)
        {
            // A second Watch for the same fd must modify, not add.
            op = Native.EpollCtlMod;
            Native.epoll_ctl(_epfd, op, fd, ref ev);
        }
    }

    private void OnListener(int index)
    {
        int listener = (int)_listeners[index].Handle;
        for (int i = 0; i < MaxAccepts; i++)
        {
            int fd = Native.accept4(listener, IntPtr.Zero, IntPtr.Zero, 0x800 | 0x80000);
            if (fd < 0)
            {
                return;
            }
            if (fd >= Conns.Length)
            {
                Native.close(fd);
                continue;
            }

            Native.SetTcpNoDelay(fd);
            ref Conn c = ref Conns[fd];
            c.Gen++;
            c.Busy = false;
            c.CloseAfterSend = false;
            c.PeerDone = false;
            c.InLength = 0;
            c.OutStart = 0;
            c.OutEnd = 0;
            Watch(fd, Native.EpollIn | Native.EpollRdhup, (ulong)fd);
        }
    }

    private void OnClient(int fd, uint events)
    {
        if ((uint)fd >= (uint)Conns.Length)
        {
            return;
        }

        ref Conn c = ref Conns[fd];

        if ((events & Native.EpollErr) != 0)
        {
            Close(fd);
            return;
        }

        if ((events & Native.EpollOut) != 0 && c.OutEnd > 0)
        {
            if (!SendPending(fd))
            {
                return;
            }
        }

        if ((events & (Native.EpollIn | Native.EpollRdhup | Native.EpollHup)) == 0)
        {
            return;
        }

        if (c.Busy || c.OutEnd > 0)
        {
            return;
        }

        int held = c.InLength;
        if (held >= MaxRequest)
        {
            BadRequest(fd);
            return;
        }

        if (held > 0)
        {
            c.In.AsSpan(0, held).CopyTo(_receive);
        }

        int received = Native.Recv(fd, _receive, held, MaxRequest - held);
        if (received == 0)
        {
            c.PeerDone = true;
            if (held > 0)
            {
                Serve(fd, _receive, held);
            }
            else if (!c.Busy && c.OutEnd == 0)
            {
                Close(fd);
            }
            return;
        }

        if (received < 0)
        {
            int error = Native.LastError;
            if (Native.WouldBlock(error))
            {
                return;
            }
            Close(fd);
            return;
        }

        held += received;
        Serve(fd, _receive, held);
    }

    private void Serve(int fd, byte[] buffer, int length)
    {
        int at = 0;
        int gen = Conns[fd].Gen;

        while (at < length)
        {
            int consumed = ServeOne(fd, buffer.AsSpan(at, length - at));
            if (consumed == 0)
            {
                break;
            }
            if (consumed < 0 || Conns[fd].Gen != gen)
            {
                return;
            }
            at += consumed;
            if (Conns[fd].Busy || Conns[fd].OutEnd > 0)
            {
                break;
            }
        }

        ref Conn c = ref Conns[fd];
        int remaining = length - at;
        if (remaining > 0)
        {
            c.In ??= ArrayPool<byte>.Shared.Rent(MaxRequest);
            buffer.AsSpan(at, remaining).CopyTo(c.In);
            c.InLength = remaining;
        }
        else
        {
            c.InLength = 0;
            if (c.In is not null)
            {
                ArrayPool<byte>.Shared.Return(c.In);
                c.In = null;
            }
        }

        if (c.PeerDone && !c.Busy && c.OutEnd == 0)
        {
            Close(fd);
        }
        else if (!c.Busy && c.OutEnd == 0)
        {
            Arm(fd);
        }
    }

    private int ServeOne(int fd, ReadOnlySpan<byte> bytes)
    {
        Parsed parsed = Parse(bytes);
        if (parsed.Status == ParseStatus.Incomplete)
        {
            return bytes.Length >= MaxRequest ? BadRequest(fd) : 0;
        }
        if (parsed.Status == ParseStatus.Malformed)
        {
            return BadRequest(fd);
        }

        ref Conn c = ref Conns[fd];
        c.CloseAfterSend = parsed.Close;

        switch (parsed.Kind)
        {
            case RequestKind.Health:
            case RequestKind.Feed:
            case RequestKind.GetPost:
            case RequestKind.CreatePost:
            case RequestKind.Like:
                if (!QueueDb(fd, parsed))
                {
                    return parsed.Consumed;
                }
                c.Busy = true;
                return parsed.Consumed;

            case RequestKind.MissingAuth:
                Respond(fd, 401, EMissingAuth, parsed.Close);
                return parsed.Consumed;
            case RequestKind.InvalidToken:
                Respond(fd, 401, EInvalidToken, parsed.Close);
                return parsed.Consumed;
            case RequestKind.InvalidPayload:
                Respond(fd, 401, EInvalidPayload, parsed.Close);
                return parsed.Consumed;
            case RequestKind.BadId:
                Respond(fd, 400, EInvalidId, parsed.Close);
                return parsed.Consumed;
            case RequestKind.MalformedJson:
                Respond(fd, 400, EMalformedJson, parsed.Close);
                return parsed.Consumed;
            case RequestKind.BodyRequired:
                Respond(fd, 400, EBodyRequired, parsed.Close);
                return parsed.Consumed;
            case RequestKind.BodyTooLong:
                Respond(fd, 400, EBodyTooLong, parsed.Close);
                return parsed.Consumed;
            default:
                Respond(fd, 404, ENotFound, parsed.Close);
                return parsed.Consumed;
        }
    }

    private int BadRequest(int fd)
    {
        Conns[fd].CloseAfterSend = true;
        Respond(fd, 400, EBadRequest, close: true);
        return -1;
    }

    private bool QueueDb(int fd, Parsed parsed)
    {
        PgRequest? request = PgRequest.Create(fd, Conns[fd].Gen, parsed);
        if (request is null)
        {
            if (parsed.Kind == RequestKind.Health)
            {
                Respond(fd, 503, Degraded("database unavailable"), parsed.Close);
            }
            else
            {
                Respond(fd, 500, EInternal, parsed.Close);
            }
            return false;
        }

        if (parsed.Kind is RequestKind.Feed or RequestKind.GetPost or RequestKind.Health)
        {
            QueueRead(request);
        }
        else
        {
            QueueWrite(request);
        }
        return true;
    }

    private void QueueRead(PgRequest request)
    {
        PgConnection? pg = null;
        for (int i = 0; i < _readers.Length; i++)
        {
            PgConnection? candidate = _readers[(_nextReader + i) % _readers.Length];
            if (candidate is { Broken: false, Current: null })
            {
                pg = candidate;
                _nextReader = (_nextReader + i + 1) % _readers.Length;
                break;
            }
        }

        if (pg is not null)
        {
            pg.Send(request);
            return;
        }

        if (AnyLiveReader())
        {
            _pendingReads.Enqueue(request);
        }
        else
        {
            FailRequest(request);
        }
    }

    private void QueueWrite(PgRequest request)
    {
        PgConnection? writer = _writer;
        if (writer is { Broken: false, Current: null })
        {
            writer.Send(request);
        }
        else if (writer is { Broken: false })
        {
            _pendingWrites.Enqueue(request);
        }
        else
        {
            FailRequest(request);
        }
    }

    private bool AnyLiveReader()
    {
        foreach (PgConnection? reader in _readers)
        {
            if (reader is { Broken: false })
            {
                return true;
            }
        }
        return false;
    }

    private void OnPg(int index, uint events)
    {
        if (index < 0 || index >= _pgs.Length)
        {
            return;
        }

        PgConnection? pg = _pgs[index];
        if (pg is null || pg.Broken)
        {
            return;
        }

        if ((events & (Native.EpollErr | Native.EpollHup)) != 0)
        {
            pg.Break();
            return;
        }

        if ((events & Native.EpollIn) != 0)
        {
            pg.OnReadable();
        }

        if ((events & Native.EpollOut) != 0)
        {
            pg.OnWritable();
        }
    }

    internal void OnPgReady(PgConnection pg, PgRequest request)
    {
        CompleteDb(request);

        if (pg == _writer)
        {
            StartNextWrite();
        }
        else
        {
            StartNextRead();
        }
    }

    internal void OnPgBroken(PgConnection pg, PgRequest? request)
    {
        if (request is not null)
        {
            FailRequest(request);
        }

        if (pg == _writer)
        {
            while (_pendingWrites.Count > 0)
            {
                FailRequest(_pendingWrites.Dequeue());
            }
        }
        else
        {
            StartNextRead();
        }
    }

    private void StartNextRead()
    {
        while (_pendingReads.Count > 0)
        {
            PgConnection? pg = null;
            for (int i = 0; i < _readers.Length; i++)
            {
                PgConnection? candidate = _readers[(_nextReader + i) % _readers.Length];
                if (candidate is { Broken: false, Current: null })
                {
                    pg = candidate;
                    _nextReader = (_nextReader + i + 1) % _readers.Length;
                    break;
                }
            }

            if (pg is null)
            {
                if (!AnyLiveReader())
                {
                    while (_pendingReads.Count > 0)
                    {
                        FailRequest(_pendingReads.Dequeue());
                    }
                }
                return;
            }

            pg.Send(_pendingReads.Dequeue());
        }
    }

    private void StartNextWrite()
    {
        if (_writer is not { Broken: false, Current: null })
        {
            return;
        }

        if (_pendingWrites.Count > 0)
        {
            _writer.Send(_pendingWrites.Dequeue());
        }
    }

    private void CompleteDb(PgRequest request)
    {
        if ((uint)request.Fd >= (uint)Conns.Length || Conns[request.Fd].Gen != request.Gen)
        {
            request.Json.Dispose();
            return;
        }

        ref Conn c = ref Conns[request.Fd];
        c.Busy = false;
        c.CloseAfterSend = request.Close;

        bool responded = false;
        if (request.Failed)
        {
            if (request.Kind == RequestKind.Like && request.ErrorState == "23503")
            {
                Respond(request.Fd, 404, EPostNotFound, request.Close);
                responded = true;
            }
            else if (request.Kind == RequestKind.Health)
            {
                Respond(request.Fd, 503, Degraded(request.ErrorMessage), request.Close);
                responded = true;
            }
            else
            {
                Respond(request.Fd, 500, EInternal, request.Close);
                responded = true;
            }
        }
        else
        {
            switch (request.Kind)
            {
                case RequestKind.Health:
                    request.Json.Append("{\"status\":\"ok\",\"db\":\"ok\",\"uptime_s\":"u8);
                    request.Json.AppendLong((Environment.TickCount64 - StartedAt) / 1000);
                    request.Json.Append((byte)'}');
                    Respond(request.Fd, 200, request.Json.Span, request.Close, request.Gzip);
                    responded = true;
                    break;

                case RequestKind.Feed:
                    request.Json.Append("]}"u8);
                    Respond(request.Fd, 200, request.Json.Span, request.Close, request.Gzip);
                    responded = true;
                    break;

                case RequestKind.GetPost:
                    if (request.Found)
                    {
                        request.Json.Append((byte)'}');
                        Respond(request.Fd, 200, request.Json.Span, request.Close, request.Gzip);
                    }
                    else
                    {
                        Respond(request.Fd, 404, EPostNotFound, request.Close);
                    }
                    responded = true;
                    break;

                case RequestKind.CreatePost:
                    request.Json.Append("{\"post\":{\"id\":"u8);
                    request.Json.AppendLong(request.NewId);
                    request.Json.Append(",\"body\":"u8);
                    request.Json.AppendJsonString(request.Body ?? "");
                    request.Json.Append(",\"created_at\":"u8);
                    request.Json.AppendJsonString(FormatTimestamp(request.CreatedMicros));
                    request.Json.Append(",\"author\":"u8);
                    request.Json.AppendJsonString(request.User.Username);
                    request.Json.Append(",\"like_count\":0}}"u8);
                    Respond(request.Fd, 201, request.Json.Span, request.Close, request.Gzip);
                    responded = true;
                    break;

                case RequestKind.Like:
                    request.Json.Append("{\"liked\":true,\"already_liked\":"u8);
                    request.Json.Append(request.Inserted > 0 ? "false"u8 : "true"u8);
                    request.Json.Append(",\"post_id\":"u8);
                    request.Json.AppendLong(request.Id);
                    request.Json.Append((byte)'}');
                    Respond(request.Fd, request.Inserted > 0 ? 201 : 200, request.Json.Span, request.Close, request.Gzip);
                    responded = true;
                    break;
            }
        }

        request.Json.Dispose();

        if (!responded)
        {
            Respond(request.Fd, 500, EInternal, request.Close);
            return;
        }

        if (Conns[request.Fd].Gen != request.Gen)
        {
            return;
        }

        if (c.OutEnd > 0)
        {
            return;
        }

        if (c.PeerDone || c.CloseAfterSend)
        {
            Close(request.Fd);
        }
        else
        {
            Resume(request.Fd);
        }
    }

    private void Resume(int fd)
    {
        ref Conn c = ref Conns[fd];
        if (c.Busy || c.OutEnd > 0)
        {
            return;
        }

        if (c.InLength > 0)
        {
            Serve(fd, c.In!, c.InLength);
        }
        else if (c.PeerDone)
        {
            Close(fd);
        }
        else
        {
            Arm(fd);
        }
    }

    private void FailRequest(PgRequest request)
    {
        if ((uint)request.Fd >= (uint)Conns.Length || Conns[request.Fd].Gen != request.Gen)
        {
            request.Json.Dispose();
            return;
        }

        ref Conn c = ref Conns[request.Fd];
        c.Busy = false;
        if (request.Kind == RequestKind.Health)
        {
            Respond(request.Fd, 503, Degraded("database connection lost"), request.Close);
        }
        else
        {
            Respond(request.Fd, 500, EInternal, request.Close);
        }
        request.Json.Dispose();

        if (Conns[request.Fd].Gen != request.Gen)
        {
            return;
        }

        if (c.PeerDone || c.CloseAfterSend)
        {
            Close(request.Fd);
        }
        else if (c.OutEnd == 0)
        {
            Resume(request.Fd);
        }
    }

    private void Arm(int fd)
    {
        ref Conn c = ref Conns[fd];
        uint events = 0;
        if (c.OutEnd > 0)
        {
            events |= Native.EpollOut;
        }
        else if (!c.Busy && !c.PeerDone)
        {
            events |= Native.EpollIn | Native.EpollRdhup;
        }

        if (events != 0)
        {
            Watch(fd, events, (ulong)fd);
        }
    }

    private bool SendPending(int fd)
    {
        ref Conn c = ref Conns[fd];
        if (c.OutEnd == 0)
        {
            return true;
        }

        int sent = Native.Send(fd, c.Out.AsSpan(c.OutStart, c.OutEnd - c.OutStart));
        if (sent < 0)
        {
            int error = Native.LastError;
            if (!Native.WouldBlock(error))
            {
                Close(fd);
                return false;
            }
            return true;
        }

        c.OutStart += sent;
        if (c.OutStart < c.OutEnd)
        {
            return true;
        }

        if (c.Out is not null)
        {
            ArrayPool<byte>.Shared.Return(c.Out);
        }
        c.Out = null;
        c.OutStart = 0;
        c.OutEnd = 0;

        if (c.CloseAfterSend)
        {
            Close(fd);
            return false;
        }

        Arm(fd);
        Resume(fd);
        return true;
    }

    private void Respond(int fd, int status, ReadOnlySpan<byte> body, bool close) =>
        Respond(fd, status, body, close, false);

    private void Respond(int fd, int status, ReadOnlySpan<byte> body, bool close, bool gzip)
    {
        byte[]? compressed = null;
        if (gzip && GzipEnabled && body.Length >= GzipMin)
        {
            compressed = Compress(body);
            body = compressed;
        }

        int at = 0;
        Append(_response, ref at, StatusLine(status));
        Append(_response, ref at, "Content-Type: application/json\r\n"u8);
        if (compressed is not null)
        {
            Append(_response, ref at, "Content-Encoding: gzip\r\nVary: Accept-Encoding\r\n"u8);
        }
        Append(_response, ref at, "Content-Length: "u8);
        Span<byte> length = stackalloc byte[20];
        Utf8Formatter.TryFormat(body.Length, length, out int lengthBytes);
        Append(_response, ref at, length[..lengthBytes]);
        if (close)
        {
            Append(_response, ref at, "\r\nConnection: close"u8);
        }
        Append(_response, ref at, "\r\n\r\n"u8);
        body.CopyTo(_response.AsSpan(at));
        at += body.Length;

        int sent = Native.Send(fd, _response.AsSpan(0, at));
        if (sent < 0)
        {
            int error = Native.LastError;
            if (!Native.WouldBlock(error))
            {
                Close(fd);
                return;
            }
            sent = 0;
        }

        if (sent < at)
        {
            ref Conn c = ref Conns[fd];
            c.Out = ArrayPool<byte>.Shared.Rent(at - sent);
            _response.AsSpan(sent, at - sent).CopyTo(c.Out);
            c.OutStart = 0;
            c.OutEnd = at - sent;
            c.CloseAfterSend = close;
            Arm(fd);
            return;
        }

        if (close)
        {
            Close(fd);
        }
        else
        {
            Arm(fd);
        }
    }

    private static byte[] Compress(ReadOnlySpan<byte> body)
    {
        using var output = new MemoryStream(body.Length / 2 + 32);
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(body);
        }
        return output.ToArray();
    }

    private static void Append(byte[] destination, ref int at, ReadOnlySpan<byte> value)
    {
        value.CopyTo(destination.AsSpan(at));
        at += value.Length;
    }

    private static ReadOnlySpan<byte> StatusLine(int status) => status switch
    {
        200 => "HTTP/1.1 200 OK\r\n"u8,
        201 => "HTTP/1.1 201 Created\r\n"u8,
        400 => "HTTP/1.1 400 Bad Request\r\n"u8,
        401 => "HTTP/1.1 401 Unauthorized\r\n"u8,
        404 => "HTTP/1.1 404 Not Found\r\n"u8,
        503 => "HTTP/1.1 503 Service Unavailable\r\n"u8,
        _ => "HTTP/1.1 500 Internal Server Error\r\n"u8,
    };

    private static byte[] Degraded(string message)
    {
        using var buffer = new JsonBuffer(256);
        buffer.Append("{\"status\":\"degraded\",\"db\":\"unreachable\",\"error\":"u8);
        buffer.AppendJsonString(message);
        buffer.Append((byte)'}');
        return buffer.Span.ToArray();
    }

    private void Close(int fd)
    {
        if ((uint)fd >= (uint)Conns.Length)
        {
            return;
        }

        ref Conn c = ref Conns[fd];
        c.Gen++;
        c.Busy = false;
        c.CloseAfterSend = false;
        c.PeerDone = false;
        if (c.In is not null)
        {
            ArrayPool<byte>.Shared.Return(c.In);
            c.In = null;
        }
        if (c.Out is not null)
        {
            ArrayPool<byte>.Shared.Return(c.Out);
            c.Out = null;
        }
        c.InLength = 0;
        c.OutStart = 0;
        c.OutEnd = 0;
        Native.close(fd);
    }

    private Parsed Parse(ReadOnlySpan<byte> bytes)
    {
        int headEnd = bytes.IndexOf("\r\n\r\n"u8);
        if (headEnd < 0)
        {
            return Parsed.Incomplete();
        }

        ReadOnlySpan<byte> head = bytes[..headEnd];
        int lineEnd = head.IndexOf("\r\n"u8);
        if (lineEnd < 0)
        {
            return Parsed.Malformed();
        }

        ReadOnlySpan<byte> line = head[..lineEnd];
        int firstSpace = line.IndexOf((byte)' ');
        int lastSpace = line.LastIndexOf((byte)' ');
        if (firstSpace <= 0 || lastSpace <= firstSpace)
        {
            return Parsed.Malformed();
        }

        ReadOnlySpan<byte> method = line[..firstSpace];
        ReadOnlySpan<byte> target = line[(firstSpace + 1)..lastSpace];
        int queryMark = target.IndexOf((byte)'?');
        ReadOnlySpan<byte> path = queryMark < 0 ? target : target[..queryMark];

        int contentLength = 0;
        bool close = !line[(lastSpace + 1)..].SequenceEqual("HTTP/1.1"u8);
        bool chunked = false;
        bool gzip = false;
        string? authorization = null;

        ReadOnlySpan<byte> headers = lineEnd < 0 ? default : head[(lineEnd + 2)..];
        while (!headers.IsEmpty)
        {
            int end = headers.IndexOf("\r\n"u8);
            ReadOnlySpan<byte> header = end < 0 ? headers : headers[..end];
            headers = end < 0 ? default : headers[(end + 2)..];
            int colon = header.IndexOf((byte)':');
            if (colon <= 0)
            {
                continue;
            }

            ReadOnlySpan<byte> name = header[..colon];
            ReadOnlySpan<byte> value = header[(colon + 1)..].Trim((byte)' ');
            if (Ascii.EqualsIgnoreCase(name, "content-length"u8))
            {
                if (!Utf8Parser.TryParse(value, out contentLength, out int used) || used != value.Length || contentLength < 0)
                {
                    return Parsed.Malformed();
                }
            }
            else if (Ascii.EqualsIgnoreCase(name, "transfer-encoding"u8))
            {
                chunked = true;
            }
            else if (Ascii.EqualsIgnoreCase(name, "connection"u8))
            {
                if (Ascii.EqualsIgnoreCase(value, "close"u8))
                {
                    close = true;
                }
                else if (Ascii.EqualsIgnoreCase(value, "keep-alive"u8))
                {
                    close = false;
                }
            }
            else if (Ascii.EqualsIgnoreCase(name, "authorization"u8))
            {
                authorization = Encoding.UTF8.GetString(value);
            }
            else if (Ascii.EqualsIgnoreCase(name, "accept-encoding"u8))
            {
                gzip = ContainsIgnoreCase(value, "gzip"u8);
            }
        }

        if (chunked || contentLength > MaxRequest)
        {
            return Parsed.Malformed();
        }

        int total = headEnd + 4 + contentLength;
        if (total > MaxRequest || bytes.Length < total)
        {
            return bytes.Length >= MaxRequest ? Parsed.Malformed() : Parsed.Incomplete();
        }

        ReadOnlySpan<byte> body = bytes.Slice(headEnd + 4, contentLength);
        bool get = method.SequenceEqual("GET"u8);
        bool post = method.SequenceEqual("POST"u8);

        if (get)
        {
            if (path.SequenceEqual("/health"u8))
            {
                return Parsed.Complete(total, RequestKind.Health, close, gzip: gzip);
            }
            if (path.SequenceEqual("/feed"u8))
            {
                return Parsed.Complete(total, RequestKind.Feed, close, gzip: gzip);
            }
            if (path.StartsWith("/posts/"u8))
            {
                ReadOnlySpan<byte> idText = path[7..];
                if (idText.IsEmpty || !TryParseId(idText, out long id))
                {
                    return Parsed.Complete(total, RequestKind.BadId, close);
                }
                return Parsed.Complete(total, RequestKind.GetPost, close, id, gzip: gzip);
            }
            return Parsed.Complete(total, RequestKind.NotFound, close);
        }

        if (post)
        {
            if (path.SequenceEqual("/posts"u8))
            {
                if (!TryAuthenticate(authorization, out AuthUser user, out RequestKind authError))
                {
                    return Parsed.Complete(total, authError, close);
                }
                if (!TryParseBody(body, out string? text, out RequestKind bodyError))
                {
                    return Parsed.Complete(total, bodyError, close);
                }
                return Parsed.Complete(total, RequestKind.CreatePost, close, body: text, user: user, gzip: gzip);
            }

            if (path.StartsWith("/posts/"u8) && path.EndsWith("/like"u8))
            {
                if (!TryAuthenticate(authorization, out AuthUser user, out RequestKind authError))
                {
                    return Parsed.Complete(total, authError, close);
                }
                ReadOnlySpan<byte> idText = path[7..^5];
                if (!TryParseId(idText, out long id))
                {
                    return Parsed.Complete(total, RequestKind.BadId, close);
                }
                return Parsed.Complete(total, RequestKind.Like, close, id, user: user, gzip: gzip);
            }
        }

        return Parsed.Complete(total, RequestKind.NotFound, close);
    }

    private static bool TryAuthenticate(string? header, out AuthUser user, out RequestKind error)
    {
        user = default;
        error = RequestKind.MissingAuth;
        if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            user = Jwt.Validate(header.AsSpan(7), JwtKey, DateTimeOffset.UtcNow);
            return true;
        }
        catch (JwtException ex)
        {
            error = ex.Message == "invalid token payload"
                ? RequestKind.InvalidPayload
                : RequestKind.InvalidToken;
            return false;
        }
    }

    private static bool TryParseBody(ReadOnlySpan<byte> body, out string? text, out RequestKind error)
    {
        text = null;
        error = RequestKind.MalformedJson;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body.ToArray());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("body", out JsonElement value) ||
                value.ValueKind != JsonValueKind.String)
            {
                error = RequestKind.BodyRequired;
                return false;
            }

            text = (value.GetString() ?? "").Trim();
            if (text.Length == 0)
            {
                error = RequestKind.BodyRequired;
                return false;
            }

            if (CountRunes(text) > 500)
            {
                error = RequestKind.BodyTooLong;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = RequestKind.MalformedJson;
            return false;
        }
    }

    private static bool ContainsIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (Ascii.EqualsIgnoreCase(haystack.Slice(i, needle.Length), needle))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryParseId(ReadOnlySpan<byte> text, out long id)
    {
        if (!Utf8Parser.TryParse(text, out id, out int consumed) || consumed != text.Length || id <= 0)
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

    internal static string FormatTimestamp(long microseconds)
    {
        long millis = microseconds / 1000;
        DateTime utc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(millis * 10_000);
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    internal readonly struct Parsed
    {
        public readonly ParseStatus Status;
        public readonly RequestKind Kind;
        public readonly int Consumed;
        public readonly long Id;
        public readonly string? Body;
        public readonly AuthUser User;
        public readonly bool Close;
        public readonly bool Gzip;

        private Parsed(ParseStatus status, RequestKind kind, int consumed, long id,
            string? body, AuthUser user, bool close, bool gzip)
        {
            Status = status;
            Kind = kind;
            Consumed = consumed;
            Id = id;
            Body = body;
            User = user;
            Close = close;
            Gzip = gzip;
        }

        public static Parsed Incomplete() => new(ParseStatus.Incomplete, RequestKind.NotFound, 0, 0, null, default, false, false);
        public static Parsed Malformed() => new(ParseStatus.Malformed, RequestKind.NotFound, 0, 0, null, default, true, false);

        public static Parsed Complete(int consumed, RequestKind kind, bool close, long id = 0,
            string? body = null, AuthUser user = default, bool gzip = false) =>
            new(ParseStatus.Complete, kind, consumed, id, body, user, close, gzip);
    }
}

internal enum ParseStatus : byte
{
    Incomplete,
    Complete,
    Malformed,
}


