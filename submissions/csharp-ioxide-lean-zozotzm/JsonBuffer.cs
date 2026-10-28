using System.Buffers;
using System.Buffers.Text;
using System.Text;

namespace LeanApi;

/// <summary>
/// A small growable UTF-8 buffer backed by <see cref="ArrayPool{T}"/>. One is rented per response
/// that needs assembly and returned immediately after the connection's write slab has copied it;
/// an idle keep-alive connection therefore does not own a response buffer.
/// </summary>
internal sealed class JsonBuffer : IDisposable
{
    private byte[] _buffer;
    private int _length;

    public JsonBuffer(int initialCapacity = 4096)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    public int Length => _length;

    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, _length);

    public void Append(ReadOnlySpan<byte> value)
    {
        Ensure(_length + value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    public void Append(byte value)
    {
        Ensure(_length + 1);
        _buffer[_length++] = value;
    }

    public void AppendLong(long value)
    {
        Span<byte> digits = stackalloc byte[24];
        Utf8Formatter.TryFormat(value, digits, out int written);
        Append(digits[..written]);
    }

    public void AppendInt(int value)
    {
        Span<byte> digits = stackalloc byte[16];
        Utf8Formatter.TryFormat(value, digits, out int written);
        Append(digits[..written]);
    }

    /// <summary>
    /// Append a JSON string literal. The bytes are already UTF-8; only the characters JSON must
    /// escape are touched, so non-ASCII text (including the test's café) round-trips unchanged.
    /// </summary>
    public void AppendJsonString(ReadOnlySpan<byte> utf8)
    {
        Append((byte)'"');

        foreach (byte b in utf8)
        {
            switch (b)
            {
                case (byte)'"':
                    Append((byte)'\\');
                    Append((byte)'"');
                    break;
                case (byte)'\\':
                    Append((byte)'\\');
                    Append((byte)'\\');
                    break;
                case (byte)'\b':
                    Append("\\b"u8);
                    break;
                case (byte)'\f':
                    Append("\\f"u8);
                    break;
                case (byte)'\n':
                    Append("\\n"u8);
                    break;
                case (byte)'\r':
                    Append("\\r"u8);
                    break;
                case (byte)'\t':
                    Append("\\t"u8);
                    break;
                default:
                    if (b < 0x20)
                    {
                        Append("\\u00"u8);
                        Append(Hex(b >> 4));
                        Append(Hex(b & 0xF));
                    }
                    else
                    {
                        Append(b);
                    }
                    break;
            }
        }

        Append((byte)'"');
    }

    public void AppendJsonString(string value) =>
        AppendJsonString(Encoding.UTF8.GetBytes(value));

    public void Reset() => _length = 0;

    private void Ensure(int size)
    {
        if (_buffer.Length >= size)
        {
            return;
        }

        int next = Math.Max(size, _buffer.Length * 2);
        byte[] replacement = ArrayPool<byte>.Shared.Rent(next);
        _buffer.AsSpan(0, _length).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = replacement;
    }

    private static byte Hex(int value) => (byte)(value < 10 ? '0' + value : 'a' + (value - 10));

    public void Dispose()
    {
        byte[] buffer = _buffer;
        _buffer = [];
        _length = 0;
        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
