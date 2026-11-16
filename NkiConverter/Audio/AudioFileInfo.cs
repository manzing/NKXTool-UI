using System.Buffers.Binary;
using System.Text;

namespace NkiConverter.Audio;

/// <summary>Basic properties of an audio file, read from its header.</summary>
public sealed record AudioFileInfo(int SampleRate, int Channels, int BitsPerSample, int Frames)
{
    public static readonly string[] SupportedExtensions = [".wav", ".aif", ".aiff", ".flac", ".ncw"];

    /// <summary>Reads the header of a WAV, AIFF or FLAC file (or NCW).</summary>
    public static AudioFileInfo Read(string file)
    {
        using var stream = File.OpenRead(file);
        var header = new byte[12];
        if (stream.Read(header, 0, 12) < 12)
        {
            throw new InvalidDataException($"Audio file is too short: {file}");
        }

        var id = Encoding.ASCII.GetString(header, 0, 4);
        stream.Position = 0;
        return id switch
        {
            "RIFF" or "RF64" => ReadWav(stream),
            "FORM" => ReadAiff(stream),
            "fLaC" => ReadFlac(stream),
            _ when BinaryPrimitives.ReadUInt32LittleEndian(header) == 0xD69EA801 => ReadNcw(header, stream),
            _ => throw new InvalidDataException($"Unsupported audio file format: {file}")
        };
    }

    private static AudioFileInfo ReadNcw(byte[] header, Stream stream)
    {
        var buffer = new byte[24];
        stream.ReadExactly(buffer);
        return new AudioFileInfo(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12)),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(8)),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(10)),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16)));
    }

    private static AudioFileInfo ReadWav(Stream stream)
    {
        var reader = new BinaryReader(stream);
        var riff = Encoding.ASCII.GetString(reader.ReadBytes(4));
        reader.ReadUInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        int channels = 0, rate = 0, bits = 0, blockAlign = 0;
        long dataSize = -1;
        long ds64DataSize = -1;

        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
            long size = reader.ReadUInt32();
            var start = stream.Position;

            switch (chunkId)
            {
                case "ds64":
                    reader.ReadUInt64(); // RIFF size
                    ds64DataSize = (long)reader.ReadUInt64();
                    break;
                case "fmt ":
                    reader.ReadUInt16(); // format
                    channels = reader.ReadUInt16();
                    rate = (int)reader.ReadUInt32();
                    reader.ReadUInt32(); // byte rate
                    blockAlign = reader.ReadUInt16();
                    bits = reader.ReadUInt16();
                    break;
                case "data":
                    dataSize = riff == "RF64" && size == 0xFFFFFFFF && ds64DataSize >= 0 ? ds64DataSize : size;
                    break;
            }

            if (dataSize >= 0 && blockAlign > 0)
            {
                break;
            }

            stream.Position = start + size + (size & 1);
        }

        if (blockAlign <= 0 || dataSize < 0)
        {
            throw new InvalidDataException("WAV file without 'fmt ' or 'data' chunk.");
        }

        return new AudioFileInfo(rate, channels, bits, (int)Math.Min(int.MaxValue, dataSize / blockAlign));
    }

    private static AudioFileInfo ReadAiff(Stream stream)
    {
        var buffer = new byte[12];
        stream.ReadExactly(buffer);
        var form = Encoding.ASCII.GetString(buffer, 8, 4);
        if (form is not ("AIFF" or "AIFC"))
        {
            throw new InvalidDataException("Not an AIFF file.");
        }

        var chunkHeader = new byte[8];
        while (stream.Position + 8 <= stream.Length)
        {
            stream.ReadExactly(chunkHeader);
            var chunkId = Encoding.ASCII.GetString(chunkHeader, 0, 4);
            var size = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader.AsSpan(4));
            if (chunkId == "COMM")
            {
                var comm = new byte[18];
                stream.ReadExactly(comm);
                var channels = BinaryPrimitives.ReadInt16BigEndian(comm);
                var frames = (int)BinaryPrimitives.ReadUInt32BigEndian(comm.AsSpan(2));
                var bits = BinaryPrimitives.ReadInt16BigEndian(comm.AsSpan(6));
                var rate = ReadExtended(comm.AsSpan(8, 10));
                return new AudioFileInfo((int)Math.Round(rate), channels, bits, frames);
            }

            stream.Position += size + (size & 1);
        }

        throw new InvalidDataException("AIFF file without 'COMM' chunk.");
    }

    /// <summary>Converts an 80-bit IEEE 754 extended precision number (big-endian) to double.</summary>
    private static double ReadExtended(ReadOnlySpan<byte> bytes)
    {
        var exponent = ((bytes[0] & 0x7F) << 8) | bytes[1];
        var mantissa = BinaryPrimitives.ReadUInt64BigEndian(bytes[2..]);
        if (exponent == 0 && mantissa == 0)
        {
            return 0;
        }

        var value = mantissa * Math.Pow(2, exponent - 16383 - 63);
        return (bytes[0] & 0x80) != 0 ? -value : value;
    }

    private static AudioFileInfo ReadFlac(Stream stream)
    {
        var buffer = new byte[4 + 4 + 34];
        stream.ReadExactly(buffer);
        // STREAMINFO is always the first metadata block
        var info = buffer.AsSpan(8);
        var rate = (info[10] << 12) | (info[11] << 4) | (info[12] >> 4);
        var channels = ((info[12] >> 1) & 0x07) + 1;
        var bits = (((info[12] & 0x01) << 4) | (info[13] >> 4)) + 1;
        var totalSamples = ((long)(info[13] & 0x0F) << 32) | ((long)info[14] << 24) | ((long)info[15] << 16) | ((long)info[16] << 8) | info[17];
        return new AudioFileInfo(rate, channels, bits, (int)Math.Min(int.MaxValue, totalSamples));
    }
}
