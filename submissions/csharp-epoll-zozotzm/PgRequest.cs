using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Feed;

internal sealed class PgRequest
{
    public readonly int Fd;
    public readonly int Gen;
    public readonly RequestKind Kind;
    public readonly long Id;
    public readonly string? Body;
    public readonly AuthUser User;
    public readonly bool Close;
    public readonly bool Gzip;
    public readonly string StatementName;
    public readonly PgParam[] Params;
    public readonly JsonBuffer Json;

    public bool Found;
    public bool Failed;
    public string ErrorState = "";
    public string ErrorMessage = "";
    public long NewId;
    public long CreatedMicros;
    public int Inserted;

    private PgRequest(int fd, int gen, RequestKind kind, long id, string? body,
        AuthUser user, bool close, bool gzip, string statementName, PgParam[] parameters)
    {
        Fd = fd;
        Gen = gen;
        Kind = kind;
        Id = id;
        Body = body;
        User = user;
        Close = close;
        Gzip = gzip;
        StatementName = statementName;
        Params = parameters;
        Json = new JsonBuffer(kind is RequestKind.Feed ? 8192 : 512);

        if (kind is RequestKind.Feed)
        {
            Json.Append("{\"posts\":["u8);
        }
        else if (kind is RequestKind.GetPost)
        {
            Json.Append("{\"post\":"u8);
        }
    }

    public static PgRequest? Create(int fd, int gen, Loop.Parsed parsed)
    {
        return parsed.Kind switch
        {
            RequestKind.Health => new PgRequest(fd, gen, RequestKind.Health, 0, null,
                default, parsed.Close, parsed.Gzip, "health", []),
            RequestKind.Feed => new PgRequest(fd, gen, RequestKind.Feed, 0, null,
                default, parsed.Close, parsed.Gzip, "feed", []),
            RequestKind.GetPost => new PgRequest(fd, gen, RequestKind.GetPost, parsed.Id, null,
                default, parsed.Close, parsed.Gzip, "post", [PgParam.Int(parsed.Id)]),
            RequestKind.CreatePost => new PgRequest(fd, gen, RequestKind.CreatePost, 0, parsed.Body,
                parsed.User, parsed.Close, parsed.Gzip, "create",
                [PgParam.Int(parsed.User.Id), PgParam.Text(parsed.Body ?? "")]),
            RequestKind.Like => new PgRequest(fd, gen, RequestKind.Like, parsed.Id, null,
                parsed.User, parsed.Close, parsed.Gzip, "like",
                [PgParam.Int(parsed.User.Id), PgParam.Int(parsed.Id)]),
            _ => null,
        };
    }

    public void OnRow(ReadOnlySpan<byte> payload)
    {
        int at = 0;
        int columns = ReadInt16(payload, ref at);
        if (columns <= 0)
        {
            return;
        }

        switch (Kind)
        {
            case RequestKind.Feed:
            case RequestKind.GetPost:
            {
                ReadOnlySpan<byte> idField = ReadField(payload, ref at);
                ReadOnlySpan<byte> body = ReadField(payload, ref at);
                ReadOnlySpan<byte> createdField = ReadField(payload, ref at);
                ReadOnlySpan<byte> author = ReadField(payload, ref at);
                ReadOnlySpan<byte> likeField = ReadField(payload, ref at);
                long id = BinaryPrimitives.ReadInt64BigEndian(idField);
                long createdMicros = BinaryPrimitives.ReadInt64BigEndian(createdField);
                int likeCount = BinaryPrimitives.ReadInt32BigEndian(likeField);

                if (Kind == RequestKind.Feed && Found)
                {
                    Json.Append((byte)',');
                }
                Found = true;

                Json.Append("{\"id\":"u8);
                Json.AppendLong(id);
                Json.Append(",\"body\":"u8);
                Json.AppendJsonString(body);
                Json.Append(",\"created_at\":"u8);
                Json.AppendJsonString(Loop.FormatTimestamp(createdMicros));
                Json.Append(",\"author\":"u8);
                Json.AppendJsonString(author);
                Json.Append(",\"like_count\":"u8);
                Json.AppendLong(likeCount);
                Json.Append((byte)'}');
                break;
            }

            case RequestKind.CreatePost:
            {
                ReadOnlySpan<byte> createdIdField = ReadField(payload, ref at);
                ReadOnlySpan<byte> createdField = ReadField(payload, ref at);
                NewId = BinaryPrimitives.ReadInt64BigEndian(createdIdField);
                CreatedMicros = BinaryPrimitives.ReadInt64BigEndian(createdField);
                break;
            }
        }
    }

    public void OnCommand(string commandTag)
    {
        if (Kind == RequestKind.Like)
        {
            Inserted = commandTag.EndsWith(" 1", StringComparison.Ordinal) ? 1 : 0;
        }
    }

    public void OnError(ReadOnlySpan<byte> payload)
    {
        Failed = true;
        for (int i = 0; i < payload.Length;)
        {
            byte field = payload[i++];
            int end = payload[i..].IndexOf((byte)0);
            if (end < 0)
            {
                break;
            }
            ReadOnlySpan<byte> value = payload.Slice(i, end);
            i += end + 1;
            if (field == (byte)'C')
            {
                ErrorState = Encoding.ASCII.GetString(value);
            }
            else if (field == (byte)'M')
            {
                ErrorMessage = Encoding.UTF8.GetString(value);
            }
        }
    }

    private static ReadOnlySpan<byte> ReadField(ReadOnlySpan<byte> payload, ref int at)
    {
        int length = ReadInt32(payload, ref at);
        if (length < 0)
        {
            return default;
        }
        ReadOnlySpan<byte> value = payload.Slice(at, length);
        at += length;
        return value;
    }

    private static int ReadInt16(ReadOnlySpan<byte> payload, ref int at)
    {
        int value = BinaryPrimitives.ReadInt16BigEndian(payload.Slice(at, 2));
        at += 2;
        return value;
    }

    private static int ReadInt32(ReadOnlySpan<byte> payload, ref int at)
    {
        int value = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(at, 4));
        at += 4;
        return value;
    }

    private static long ReadInt64(ReadOnlySpan<byte> payload, ref int at)
    {
        long value = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(at, 8));
        at += 8;
        return value;
    }
}
