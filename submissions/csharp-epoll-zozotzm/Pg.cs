using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace Feed;

internal enum PgParamKind : byte
{
    Null,
    Int,
    Text,
}

internal readonly struct PgParam
{
    public readonly PgParamKind Kind;
    public readonly long IntValue;
    public readonly string? TextValue;

    private PgParam(PgParamKind kind, long intValue, string? textValue)
    {
        Kind = kind;
        IntValue = intValue;
        TextValue = textValue;
    }

    public static PgParam Null => new(PgParamKind.Null, 0, null);
    public static PgParam Int(long value) => new(PgParamKind.Int, value, null);
    public static PgParam Text(string value) => new(PgParamKind.Text, 0, value);
}

internal sealed class PgSettings
{
    public readonly string Host;
    public readonly int Port;
    public readonly string User;
    public readonly string Password;
    public readonly string Database;

    private PgSettings(string host, int port, string user, string password, string database)
    {
        Host = host;
        Port = port;
        User = user;
        Password = password;
        Database = database;
    }

    public static PgSettings Parse(string url)
    {
        var uri = new Uri(url);
        string[] info = uri.UserInfo.Split(':', 2);
        return new PgSettings(
            uri.Host,
            uri.Port > 0 ? uri.Port : 5432,
            Uri.UnescapeDataString(info[0]),
            info.Length > 1 ? Uri.UnescapeDataString(info[1]) : "",
            Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')));
    }
}

/// <summary>
/// One Postgres connection on the binary v3 protocol. Login and Parse run blocking before the
/// loop starts; Bind/Execute/Sync then run non-blocking on the loop's epoll set.
/// </summary>
internal sealed class PgConnection : IDisposable
{
    private const string HealthSql = "SELECT 1";
    private const string FeedSql = """
        SELECT p.id, p.body, p.created_at, u.username,
               (SELECT count(*) FROM likes l WHERE l.post_id = p.id)::int AS like_count
          FROM posts p JOIN users u ON u.id = p.user_id
         ORDER BY p.created_at DESC, p.id DESC LIMIT 20
        """;
    private const string PostSql = """
        SELECT p.id, p.body, p.created_at, u.username,
               (SELECT count(*) FROM likes l WHERE l.post_id = p.id)::int AS like_count
          FROM posts p JOIN users u ON u.id = p.user_id
         WHERE p.id = $1
        """;
    private const string CreateSql = "INSERT INTO posts (user_id, body) VALUES ($1, $2) RETURNING id, created_at";
    private const string LikeSql = "INSERT INTO likes (user_id, post_id) VALUES ($1, $2) ON CONFLICT (user_id, post_id) DO NOTHING";

    private static readonly (string Name, string Sql)[] Statements =
    [
        ("health", HealthSql),
        ("feed", FeedSql),
        ("post", PostSql),
        ("create", CreateSql),
        ("like", LikeSql),
    ];

    private readonly Loop _loop;
    private readonly PgSettings _settings;
    private readonly Socket _socket;
    private readonly byte[] _out = new byte[64 * 1024];
    private readonly byte[] _in = new byte[128 * 1024];

    private int _outLength;
    private int _outSent;
    private int _inLength;

    public readonly int Index;
    public readonly int Fd;
    public bool Broken;
    public PgRequest? Current;

    public PgConnection(Loop loop, int index, PgSettings settings)
    {
        _loop = loop;
        _settings = settings;
        Index = index;

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        socket.Connect(settings.Host, settings.Port);
        _socket = socket;
        Fd = checked((int)socket.Handle);

        try
        {
            Startup();
            Prepare();
            socket.Blocking = false;
            _loop.WatchPg(this, wantWrite: false);
        }
        catch
        {
            Close();
            throw;
        }
    }

    public bool WantsWrite => _outSent < _outLength;

    public void Send(PgRequest request)
    {
        Current = request;
        _outLength = 0;
        _outSent = 0;
        BuildQuery(request);
        Flush();
    }

    public void OnWritable()
    {
        Flush();
        if (!WantsWrite)
        {
            _loop.WatchPg(this, wantWrite: false);
        }
    }

    public void OnReadable()
    {
        while (!Broken)
        {
            if (_inLength == _in.Length)
            {
                Fail();
                return;
            }

            int received;
            try
            {
                received = Native.Recv(Fd, _in, _inLength, _in.Length - _inLength);
            }
            catch
            {
                Fail();
                return;
            }

            if (received > 0)
            {
                _inLength += received;
                if (!Parse())
                {
                    return;
                }

                if (_inLength == _in.Length)
                {
                    Fail();
                    return;
                }
            }
            else if (received == 0)
            {
                Fail();
                return;
            }
            else
            {
                int error = Native.LastError;
                if (Native.WouldBlock(error))
                {
                    return;
                }

                Fail();
                return;
            }
        }
    }

    private bool Parse()
    {
        int at = 0;
        while (_inLength - at >= 5)
        {
            byte type = _in[at];
            int length = BinaryPrimitives.ReadInt32BigEndian(_in.AsSpan(at + 1, 4));
            if (length < 4 || at + 1 + length > _inLength)
            {
                break;
            }

            ReadOnlySpan<byte> payload = _in.AsSpan(at + 5, length - 4);
            at += 1 + length;

            switch (type)
            {
                case (byte)'D':
                    Current?.OnRow(payload);
                    break;
                case (byte)'C':
                    Current?.OnCommand(CString(payload));
                    break;
                case (byte)'E':
                    Current?.OnError(payload);
                    break;
                case (byte)'Z':
                    PgRequest? request = Current;
                    Current = null;
                    if (request is not null)
                    {
                        _loop.OnPgReady(this, request);
                    }
                    if (Broken)
                    {
                        return false;
                    }
                    break;
            }
        }

        if (at > 0)
        {
            int remaining = _inLength - at;
            if (remaining > 0)
            {
                Buffer.BlockCopy(_in, at, _in, 0, remaining);
            }
            _inLength = remaining;
        }

        return true;
    }

    private void BuildQuery(PgRequest request)
    {
        int bind = BeginMessage((byte)'B');
        WriteByte(0);
        WriteCString(request.StatementName);
        if (request.Params.Length == 0)
        {
            WriteInt16(0);
        }
        else
        {
            WriteInt16(1);
            WriteInt16(1);
        }

        WriteInt16((short)request.Params.Length);
        foreach (PgParam parameter in request.Params)
        {
            switch (parameter.Kind)
            {
                case PgParamKind.Null:
                    WriteInt32(-1);
                    break;
                case PgParamKind.Int:
                    WriteInt32(8);
                    WriteInt64(parameter.IntValue);
                    break;
                case PgParamKind.Text:
                    byte[] text = Encoding.UTF8.GetBytes(parameter.TextValue ?? "");
                    WriteInt32(text.Length);
                    WriteBytes(text);
                    break;
            }
        }

        WriteInt16(1);
        WriteInt16(1);
        EndMessage(bind);

        int execute = BeginMessage((byte)'E');
        WriteByte(0);
        WriteInt32(0);
        EndMessage(execute);

        WriteByte((byte)'S');
        WriteInt32(4);
    }

    private void Flush()
    {
        while (_outSent < _outLength)
        {
            int sent;
            try
            {
                sent = Native.Send(Fd, _out.AsSpan(_outSent, _outLength - _outSent));
            }
            catch
            {
                Fail();
                return;
            }

            if (sent > 0)
            {
                _outSent += sent;
                continue;
            }

            if (sent < 0 && Native.WouldBlock(Native.LastError))
            {
                _loop.WatchPg(this, wantWrite: true);
                return;
            }

            Fail();
            return;
        }

        _outLength = 0;
        _outSent = 0;
    }

    private void Startup()
    {
        var startup = new List<byte>(128);
        startup.AddRange(new byte[4]);
        WriteInt32(startup, 196608);
        WriteCString(startup, "user");
        WriteCString(startup, _settings.User);
        WriteCString(startup, "database");
        WriteCString(startup, _settings.Database);
        WriteCString(startup, "client_encoding");
        WriteCString(startup, "UTF8");
        startup.Add(0);
        byte[] startupBytes = startup.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(startupBytes, startupBytes.Length);
        SendBlocking(startupBytes);

        Scram? scram = null;
        while (true)
        {
            byte type = ReadMessageBlocking(out byte[] body);
            switch (type)
            {
                case (byte)'E':
                    throw new InvalidOperationException("Postgres: " + ErrorText(body));
                case (byte)'Z':
                    return;
                case (byte)'R':
                    int code = BinaryPrimitives.ReadInt32BigEndian(body);
                    switch (code)
                    {
                        case 0:
                            break;
                        case 3:
                            SendPassword(_settings.Password);
                            break;
                        case 10:
                            if (!ContainsMechanism(body, "SCRAM-SHA-256"))
                            {
                                throw new InvalidOperationException("Postgres does not offer SCRAM-SHA-256");
                            }
                            scram = new Scram(_settings.Password);
                            SendSaslInitial(scram.ClientFirst());
                            break;
                        case 11:
                            SendSaslResponse(scram!.ClientFinal(Encoding.UTF8.GetString(body.AsSpan(4))));
                            break;
                        case 12:
                            scram!.VerifyServerFinal(Encoding.UTF8.GetString(body.AsSpan(4)));
                            break;
                        default:
                            throw new InvalidOperationException($"Postgres authentication method {code} is not supported");
                    }
                    break;
            }
        }
    }

    private void Prepare()
    {
        _outLength = 0;
        _outSent = 0;
        foreach ((string name, string sql) in Statements)
        {
            int start = BeginMessage((byte)'P');
            WriteCString(name);
            WriteCString(sql);
            WriteInt16(0);
            EndMessage(start);
        }
        WriteByte((byte)'S');
        WriteInt32(4);
        SendBlocking(_out.AsSpan(0, _outLength));
        _outLength = 0;
        _outSent = 0;

        while (true)
        {
            byte type = ReadMessageBlocking(out byte[] body);
            if (type == (byte)'E')
            {
                throw new InvalidOperationException("Postgres prepare: " + ErrorText(body));
            }
            if (type == (byte)'Z')
            {
                break;
            }
        }
    }

    private void SendPassword(string password)
    {
        byte[] body = [.. Encoding.UTF8.GetBytes(password), 0];
        SendMessage((byte)'p', body);
    }

    private void SendSaslInitial(byte[] first)
    {
        byte[] mechanism = [.. "SCRAM-SHA-256"u8, 0];
        byte[] body = new byte[mechanism.Length + 4 + first.Length];
        mechanism.CopyTo(body, 0);
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(mechanism.Length, 4), first.Length);
        first.CopyTo(body, mechanism.Length + 4);
        SendMessage((byte)'p', body);
    }

    private void SendSaslResponse(byte[] response) => SendMessage((byte)'p', response);

    private void SendMessage(byte type, byte[] body)
    {
        byte[] message = new byte[5 + body.Length];
        message[0] = type;
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(1), 4 + body.Length);
        body.CopyTo(message, 5);
        SendBlocking(message);
    }

    private void SendBlocking(ReadOnlySpan<byte> data)
    {
        int at = 0;
        while (at < data.Length)
        {
            int sent = _socket.Send(data[at..]);
            if (sent <= 0)
            {
                throw new InvalidOperationException("Postgres send failed");
            }
            at += sent;
        }
    }

    private byte ReadMessageBlocking(out byte[] body)
    {
        byte[] header = new byte[5];
        ReadExact(header);
        byte type = header[0];
        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (length < 4)
        {
            throw new InvalidOperationException("Postgres sent a bad message length");
        }
        body = new byte[length - 4];
        ReadExact(body);
        return type;
    }

    private void ReadExact(byte[] target)
    {
        int at = 0;
        while (at < target.Length)
        {
            int read = _socket.Receive(target, at, target.Length - at, SocketFlags.None);
            if (read <= 0)
            {
                throw new InvalidOperationException("Postgres closed during login");
            }
            at += read;
        }
    }

    public void Break() => Fail();

    private void Fail()
    {
        if (Broken)
        {
            return;
        }

        Broken = true;
        PgRequest? request = Current;
        Current = null;
        Close();
        _loop.OnPgBroken(this, request);
    }

    private void Close()
    {
        try
        {
            _socket.Dispose();
        }
        catch
        {
            // already gone
        }
    }

    public void Dispose() => Close();

    private static bool ContainsMechanism(ReadOnlySpan<byte> body, string mechanism)
    {
        ReadOnlySpan<byte> rest = body[4..];
        byte[] wanted = Encoding.ASCII.GetBytes(mechanism);
        while (!rest.IsEmpty)
        {
            int end = rest.IndexOf((byte)0);
            if (end < 0)
            {
                return false;
            }
            if (rest[..end].SequenceEqual(wanted))
            {
                return true;
            }
            rest = rest[(end + 1)..];
        }
        return false;
    }

    private static string ErrorText(ReadOnlySpan<byte> body)
    {
        for (int i = 0; i + 1 < body.Length; i++)
        {
            if (body[i] == (byte)'M')
            {
                int end = body[(i + 1)..].IndexOf((byte)0);
                if (end >= 0)
                {
                    return Encoding.UTF8.GetString(body.Slice(i + 1, end));
                }
            }
        }
        return "unknown error";
    }

    private int BeginMessage(byte type)
    {
        WriteByte(type);
        int lengthPosition = _outLength;
        WriteInt32(0);
        return lengthPosition;
    }

    private void EndMessage(int lengthPosition)
    {
        int length = _outLength - lengthPosition;
        BinaryPrimitives.WriteInt32BigEndian(_out.AsSpan(lengthPosition, 4), length);
    }

    private static string CString(ReadOnlySpan<byte> payload)
    {
        int end = payload.IndexOf((byte)0);
        if (end < 0)
        {
            end = payload.Length;
        }
        return Encoding.UTF8.GetString(payload[..end]);
    }

    private void WriteByte(byte value)
    {
        _out[_outLength++] = value;
    }

    private void WriteInt16(short value)
    {
        BinaryPrimitives.WriteInt16BigEndian(_out.AsSpan(_outLength), value);
        _outLength += 2;
    }

    private void WriteInt32(int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(_out.AsSpan(_outLength), value);
        _outLength += 4;
    }

    private void WriteInt64(long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(_out.AsSpan(_outLength), value);
        _outLength += 8;
    }

    private void WriteCString(string value)
    {
        int written = Encoding.UTF8.GetBytes(value, _out.AsSpan(_outLength));
        _outLength += written;
        _out[_outLength++] = 0;
    }

    private void WriteBytes(ReadOnlySpan<byte> value)
    {
        value.CopyTo(_out.AsSpan(_outLength));
        _outLength += value.Length;
    }

    private static void WriteInt32(List<byte> target, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        foreach (byte b in bytes)
        {
            target.Add(b);
        }
    }

    private static void WriteCString(List<byte> target, string value)
    {
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            target.Add(b);
        }
        target.Add(0);
    }
}
