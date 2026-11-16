using System.Buffers.Binary;
using System.Text;

namespace NkiConverter.IO;

/// <summary>
/// Sequential little-endian (by default) reader over a byte array segment.
/// Throws <see cref="InvalidDataException"/> when reading past the end so that
/// malformed files fail with a clear message instead of garbage values.
/// </summary>
internal sealed class ByteReader
{
    private readonly byte[] _data;
    private readonly int _end;

    public ByteReader(byte[] data, bool bigEndian = false)
        : this(data, 0, data.Length, bigEndian)
    {
    }

    public ByteReader(byte[] data, int offset, int length, bool bigEndian = false)
    {
        if (offset < 0 || length < 0 || offset + length > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _data = data;
        Position = offset;
        _end = offset + length;
        BigEndian = bigEndian;
    }

    public bool BigEndian { get; }

    public int Position { get; set; }

    public int Remaining => _end - Position;

    public bool HasMore => Position < _end;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new InvalidDataException($"Unexpected end of data (wanted {count} bytes, {Remaining} left).");
        }

        var span = new ReadOnlySpan<byte>(_data, Position, count);
        Position += count;
        return span;
    }

    public byte U8() => Take(1)[0];

    public ushort U16() => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(Take(2)) : BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public short I16() => unchecked((short)U16());

    public uint U32() => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(Take(4)) : BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public int I32() => unchecked((int)U32());

    public ulong U64() => BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(Take(8)) : BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public float F32() => BitConverter.Int32BitsToSingle(I32());

    public double F64() => BitConverter.Int64BitsToDouble(unchecked((long)U64()));

    public byte[] Bytes(int count) => Take(count).ToArray();

    public byte[] RestBytes() => Bytes(Remaining);

    public void Skip(int count) => Take(count);

    public string Ascii(int count) => Encoding.ASCII.GetString(Take(count));

    public string Latin1(int count) => Encoding.Latin1.GetString(Take(count));

    /// <summary>UTF-16LE string prefixed with a 32-bit character count, no terminator.</summary>
    public string Utf16WithLength()
    {
        var chars = U32();
        if (chars > int.MaxValue / 2)
        {
            throw new InvalidDataException("String length out of range.");
        }

        return Encoding.Unicode.GetString(Take((int)chars * 2));
    }

    /// <summary>Reads a block which is prefixed with its total size as a 64-bit number (the size includes the 8 size bytes).</summary>
    public ByteReader Block64()
    {
        var size = U64();
        if (size < 8 || size - 8 > (ulong)Remaining)
        {
            throw new InvalidDataException("Invalid block size in NI container.");
        }

        var length = (int)(size - 8);
        var reader = new ByteReader(_data, Position, length, BigEndian);
        Position += length;
        return reader;
    }

    public ByteReader Sub(int length)
    {
        if (length < 0 || length > Remaining)
        {
            throw new InvalidDataException("Invalid sub-block size.");
        }

        var reader = new ByteReader(_data, Position, length, BigEndian);
        Position += length;
        return reader;
    }

    // ---- Random access helpers (relative to the start of the underlying array) ----

    public static ushort U16At(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

    public static uint U32At(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    public static float F32At(byte[] data, int offset) => BitConverter.Int32BitsToSingle(unchecked((int)U32At(data, offset)));

    /// <summary>Reads a 32-bit length prefixed printable ASCII string, or null if the bytes at the offset are not such a string.</summary>
    public static string? AsciiWithLengthAt(byte[] data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length)
        {
            return null;
        }

        var length = U32At(data, offset);
        if (length > (uint)(data.Length - offset - 4))
        {
            return null;
        }

        var sb = new StringBuilder((int)length);
        for (var i = 0; i < length; i++)
        {
            var c = data[offset + 4 + i];
            if (c < 0x20 || c > 0x7E)
            {
                return null;
            }

            sb.Append((char)c);
        }

        return sb.ToString();
    }
}
