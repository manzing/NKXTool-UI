using System.IO.Compression;
using System.Text;

namespace NkiConverter.Tests;

/// <summary>Little-endian writer helpers used to build synthetic Kontakt / NCW / WAV files for the tests.</summary>
internal sealed class Bin
{
    private readonly MemoryStream _stream = new();

    public Bin U8(int v) { _stream.WriteByte((byte)v); return this; }
    public Bin U16(int v) { _stream.Write(BitConverter.GetBytes((ushort)v)); return this; }
    public Bin I16(int v) { _stream.Write(BitConverter.GetBytes((short)v)); return this; }
    public Bin U32(long v) { _stream.Write(BitConverter.GetBytes((uint)v)); return this; }
    public Bin I32(int v) { _stream.Write(BitConverter.GetBytes(v)); return this; }
    public Bin U64(ulong v) { _stream.Write(BitConverter.GetBytes(v)); return this; }
    public Bin F32(float v) { _stream.Write(BitConverter.GetBytes(v)); return this; }
    public Bin F64(double v) { _stream.Write(BitConverter.GetBytes(v)); return this; }
    public Bin Bytes(byte[] b) { _stream.Write(b); return this; }
    public Bin Ascii(string s) { _stream.Write(Encoding.ASCII.GetBytes(s)); return this; }
    public Bin AsciiWithLength(string s) => U32(s.Length).Ascii(s);
    public Bin Utf16(string s) { U32(s.Length); _stream.Write(Encoding.Unicode.GetBytes(s)); return this; }
    public Bin Zeros(int n) { _stream.Write(new byte[n]); return this; }
    public Bin Block64(byte[] content) => U64((ulong)content.Length + 8).Bytes(content);
    public byte[] ToArray() => _stream.ToArray();
    public int Length => (int)_stream.Length;
}

internal sealed record TestLoop(int Mode, int Start, int Length, bool Alternating = false, float Tuning = 1f, int Crossfade = 0);

internal sealed record TestZone(
    int Group, int FileId, int LowKey, int HighKey, int Root, int LowVel = 1, int HighVel = 127,
    int FadeLowKey = 0, int FadeHighKey = 0, int FadeLowVel = 0, int FadeHighVel = 0,
    int Start = 0, int EndOffset = 0, float Volume = 1f, float Pan = 0f, float Tune = 1f, int Frames = 0, TestLoop[]? Loops = null);

internal sealed record TestGroup(string Name, float Volume = 1f, float Tune = 1f, bool KeyTracking = true, bool ReleaseTrigger = false,
    bool Muted = false, int StartMode = 0, int CycleClass = 0, bool VolumeEnvelope = false, float VelocityToVolume = -1f, float PitchBend = -1f);

/// <summary>Builds Kontakt preset data the same way Kontakt 4.2+ stores it.</summary>
internal static class KontaktBuilder
{
    private const int Program = 0x28, GroupList = 0x33, ZoneList = 0x34, LoopArray = 0x39, FilenameListEx = 0x4B;
    private const int ParameterArray16 = 0x3B, ParameterArray32 = 0x3C, StartCriteriaList = 0x38, ParInternalMod = 0x0D, ParExternalMod = 0x0C;

    public static byte[] Chunk(int id, byte[] data) => new Bin().U16(id).U32(data.Length).Bytes(data).ToArray();

    public static byte[] Structure(int version, byte[] publicData, params byte[][] children)
    {
        var childData = children.SelectMany(c => c).ToArray();
        return new Bin().U8(1).U16(version).U32(0).U32(publicData.Length).Bytes(publicData).U32(childData.Length).Bytes(childData).ToArray();
    }

    public static byte[] PresetData(string name, TestGroup[] groups, TestZone[] zones, string[] files, float programVolume = 1f, string author = "Test Author")
    {
        var programData = new Bin()
            .Utf16(name).F64(0).U8(0).F32(programVolume).F32(0).F32(1)
            .U8(1).U8(127).U8(0).U8(127).U16(0).U32(0).U32(0).U32(0).U32(0).U8(0).U32(0)
            .Utf16("Some credits").Utf16(author).Utf16("https://example.com").U16(0).U16(0).U16(0)
            .ToArray();

        // Group list: array of structures without reference
        var groupList = new Bin().U32(groups.Length);
        foreach (var g in groups)
        {
            var groupData = new Bin().Utf16(g.Name).F32(g.Volume).F32(0).F32(g.Tune)
                .U8(g.KeyTracking ? 1 : 0).U8(0).U8(g.ReleaseTrigger ? 1 : 0).U8(0).I32(0).I16(-1).I32(-1).I32(6)
                .U8(g.Muted ? 1 : 0).U8(0).I32(0).ToArray();
            var children = new List<byte[]>();
            if (g.VolumeEnvelope)
            {
                children.Add(Chunk(ParameterArray16, FixedArray(ParInternalMod, VolumeEnvelopeModulator())));
            }

            var extMods = new List<byte[]>();
            if (g.VelocityToVolume >= 0)
            {
                extMods.Add(ExternalModulator("volume", g.VelocityToVolume, 0));
            }

            if (g.PitchBend >= 0)
            {
                extMods.Add(ExternalModulator("pitch", g.PitchBend, 1));
            }

            if (extMods.Count > 0)
            {
                children.Add(Chunk(ParameterArray32, FixedArray(ParExternalMod, extMods.ToArray())));
            }

            if (g.StartMode != 0)
            {
                var sc = new byte[1 + 34];
                sc[0] = 1;
                BitConverter.GetBytes((ushort)0x70).CopyTo(sc, 2);
                BitConverter.GetBytes((uint)g.StartMode).CopyTo(sc, 4);
                BitConverter.GetBytes((ushort)g.CycleClass).CopyTo(sc, 22);
                children.Add(Chunk(StartCriteriaList, sc));
            }

            groupList.Bytes(Structure(0x95, groupData, children.ToArray()));
        }

        // Zone list: array of (group index, structure)
        var zoneList = new Bin().U32(zones.Length);
        foreach (var z in zones)
        {
            var zoneData = new Bin().U32(z.Start).U32(z.EndOffset).U32(0)
                .U16(z.LowVel).U16(z.HighVel).U16(z.LowKey).U16(z.HighKey)
                .U16(z.FadeLowVel).U16(z.FadeHighVel).U16(z.FadeLowKey).U16(z.FadeHighKey).U16(z.Root)
                .F32(z.Volume).F32(z.Pan).F32(z.Tune)
                .U8(0).U8(0).U32(0)
                .I32(z.FileId).U32(2).U32(44100).U8(1).U32(z.Frames).U32(0).U32(z.Root).F32(1).U8(0).U32(0)
                .ToArray();

            var loops = z.Loops ?? [];
            var loopData = new Bin();
            if (loops.Length == 0)
            {
                loopData.U8(0);
            }
            else
            {
                loopData.U16((1 << loops.Length) - 1);
                for (var i = 0; i < loops.Length; i++)
                {
                    var l = loops[i];
                    loopData.U16(0x60).I32(l.Mode).U32(l.Start).U32(l.Length).U32(0).U8(l.Alternating ? 1 : 0).F32(l.Tuning).U32(l.Crossfade);
                    if (i + 1 < loops.Length)
                    {
                        loopData.U8(0);
                    }
                }
            }

            zoneList.I32(z.Group).Bytes(Structure(0x9A, zoneData, Chunk(LoopArray, loopData.ToArray())));
        }

        var program = Chunk(Program, Structure(0xA5, programData,
            Chunk(GroupList, groupList.ToArray()),
            Chunk(ZoneList, zoneList.ToArray())));

        return program.Concat(Chunk(FilenameListEx, FileListEx(files))).ToArray();
    }

    private static byte[] FixedArray(int childId, params byte[][] children)
    {
        var b = new Bin().U8(0).U16(0x12); // version < 0x13: fixed number of slots
        var slots = childId == ParInternalMod ? 16 : 32;
        for (var i = 0; i < slots; i++)
        {
            if (i < children.Length)
            {
                b.U8(1).U16(childId).U32(children[i].Length).Bytes(children[i]);
            }
            else
            {
                b.U8(0);
            }
        }

        return b.ToArray();
    }

    private static byte[] VolumeEnvelopeModulator()
    {
        var block1 = new Bin().U32(1)
            .AsciiWithLength("volume").F32(1f).U16(0xFFFF).U8(0x10).U16(0)
            .AsciiWithLength("ENV_AHDSR_VOLUME").U8(0) // no shaper
            .U8(1).U8(0).U8(1).Zeros(5) // trailer: open, bypass, retrigger + unknown
            .AsciiWithLength("ENV_AHDSR").U32(0) // source name + source index (envelope)
            .ToArray();

        var block2 = new byte[60];
        BitConverter.GetBytes(-0.5f).CopyTo(block2, 34); // curve
        BitConverter.GetBytes(12f).CopyTo(block2, 38); // attack ms
        BitConverter.GetBytes(250f).CopyTo(block2, 42); // decay ms
        BitConverter.GetBytes(5f).CopyTo(block2, 46); // hold ms
        BitConverter.GetBytes(400f).CopyTo(block2, 50); // release ms
        BitConverter.GetBytes(0.7f).CopyTo(block2, 54); // sustain

        return new Bin().U8(1).U16(0x80).U32(block1.Length).Bytes(block1).U32(0).U32(block2.Length).Bytes(block2).ToArray();
    }

    private static byte[] ExternalModulator(string target, float intensity, int sourceType)
    {
        var block = new Bin().U32(1)
            .AsciiWithLength(target).F32(intensity).U16(0xFFFF).U8(0x10).U16(0)
            .AsciiWithLength("desc").U8(0)
            .AsciiWithLength("source").U32(1).U32(sourceType)
            .ToArray();
        return new Bin().U8(0).U16(0x50).U32(block.Length).Bytes(block).ToArray();
    }

    private static byte[] FileListEx(string[] files)
    {
        var b = new Bin().U16(2).I32(0).I32(files.Length);
        foreach (var file in files)
        {
            var parts = file.Split('/');
            b.U32(parts.Length);
            foreach (var part in parts[..^1])
            {
                if (part == "..")
                {
                    b.U8(3);
                }
                else
                {
                    b.U8(2).Utf16(part);
                }
            }

            b.U8(4).Utf16(parts[^1]);
        }

        // timestamps etc. (not evaluated)
        for (var i = 0; i < files.Length; i++)
        {
            b.U32(0).U32(0);
        }

        return b.ToArray();
    }

    /// <summary>Wraps preset data into a Kontakt 5+ NI container ('hsin').</summary>
    public static byte[] Kontakt5Container(byte[] presetData, string soundInfoAuthor = "Vendor Author")
    {
        var presetChunkData = new Bin().U32(1).U32(0).U32(1).U32(presetData.Length).U32(0).Bytes(presetData).U32(0).U32(0x8565620D).ToArray();
        var soundInfo = new Bin().U32(1).U32(1).U32(0).U32(0)
            .Utf16("SoundInfo Name").Utf16(soundInfoAuthor).Utf16("Test Vendor").Utf16("A test description")
            .U32(0).U64(ulong.MaxValue).U64(0).U64(0).U32(1).U32(1)
            .U32(1).Utf16("Kontakt")
            .U32(2).Utf16("Piano").Utf16("Grand")
            .U32(0).U32(1).Utf16("library").Utf16("Test Library")
            .ToArray();

        // Chunk stack: soundinfo -> preset -> terminator (each chunk contains the next one before its own data)
        var terminator = new Bin().Ascii("hsin").U32(1).U32(1).ToArray();
        var presetChunk = new Bin().Ascii("hsin").U32(109).U32(1).Block64(terminator).Bytes(presetChunkData).ToArray();
        var soundInfoChunk = new Bin().Ascii("hsin").U32(108).U32(1).Block64(presetChunk).Bytes(soundInfo).ToArray();

        var item = new Bin().U32(1).Ascii("hsin").U32(1).U32(0).Zeros(16).Block64(soundInfoChunk).U32(1).U32(0).ToArray();
        return new Bin().Block64(item).ToArray();
    }

    /// <summary>Wraps preset data into a Kontakt 4.2 NKI (header + FastLZ data, here compressed with literal runs only).</summary>
    public static byte[] Kontakt42File(byte[] presetData)
    {
        var compressed = FastLzLiteral(presetData);
        var header = Kontakt2Header(compressed.Length, 0x110);
        return new Bin().Bytes(header).U32(0).U32(presetData.Length).Zeros(32).Bytes(compressed).ToArray();
    }

    /// <summary>Kontakt 2-4.1 NKI: header + zlib compressed XML.</summary>
    public static byte[] Kontakt2File(string xml)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(Encoding.UTF8.GetBytes(xml));
        }

        var compressed = ms.ToArray();
        return new Bin().Bytes(Kontakt2Header(compressed.Length, 0x100)).Bytes(compressed).ToArray();
    }

    private static byte[] Kontakt2Header(int compressedLength, int headerVersion)
    {
        var b = new Bin()
            .Bytes([0x12, 0x90, 0xA8, 0x7F]) // little-endian Kontakt 2 instrument magic
            .U32(compressedLength).U16(headerVersion).U32(headerVersion == 0x110 ? 0x1A6337EA : 0x722A013E).U16(0)
            .Bytes([0, 0, 2, 4]) // version 4.2.0.0 (stored reversed)
            .Ascii("Kon4").U32(0).U32(0).U16(1).U16(1).U16(1).U32(0).U32(0).U32(0).U32(0)
            .U32(0).Ascii("Hdr Auth").U8(0).U8(0).U8(0);
        var website = new byte[86];
        Encoding.ASCII.GetBytes("www.example.org").CopyTo(website, 0);
        b.Bytes(website).Zeros(3).U32(0);
        b.Zeros(headerVersion == 0x110 ? 16 : 4).U32(0);
        return b.ToArray();
    }

    /// <summary>Valid FastLZ level 1 stream using literal runs only.</summary>
    private static byte[] FastLzLiteral(byte[] data)
    {
        var b = new Bin();
        for (var pos = 0; pos < data.Length; pos += 32)
        {
            var len = Math.Min(32, data.Length - pos);
            b.U8(len - 1).Bytes(data.AsSpan(pos, len).ToArray());
        }

        return b.ToArray();
    }
}

internal static class AudioBuilder
{
    public static short[] Sine(int frames, double frequency, double amplitude = 0.5, int sampleRate = 44100) =>
        Enumerable.Range(0, frames).Select(i => (short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * amplitude * short.MaxValue)).ToArray();

    public static void WriteWav16(string file, short[][] channels, int sampleRate = 44100)
    {
        var frames = channels[0].Length;
        var b = new Bin().Ascii("RIFF").U32(36 + frames * channels.Length * 2).Ascii("WAVE")
            .Ascii("fmt ").U32(16).U16(1).U16(channels.Length).U32(sampleRate).U32(sampleRate * channels.Length * 2).U16(channels.Length * 2).U16(16)
            .Ascii("data").U32(frames * channels.Length * 2);
        for (var i = 0; i < frames; i++)
        {
            foreach (var channel in channels)
            {
                b.I16(channel[i]);
            }
        }

        File.WriteAllBytes(file, b.ToArray());
    }

    /// <summary>
    /// Encodes 16 bit audio as NCW. Block types alternate between delta coding, truncated (absolute)
    /// coding and raw blocks; stereo blocks of the delta type use mid/side.
    /// </summary>
    public static byte[] EncodeNcw(short[][] channels, int sampleRate = 44100)
    {
        const int n = 512;
        var frames = channels[0].Length;
        var numBlocks = (frames + n - 1) / n;
        var blockData = new List<byte[]>();

        for (var block = 0; block < numBlocks; block++)
        {
            var mode = block % 3; // 0 = delta (+mid/side), 1 = truncated, 2 = raw
            var midSide = mode == 0 && channels.Length == 2;
            var blockBin = new Bin();
            var values = new int[channels.Length][];
            for (var c = 0; c < channels.Length; c++)
            {
                values[c] = new int[n];
                for (var i = 0; i < n; i++)
                {
                    var idx = block * n + i;
                    values[c][i] = idx < frames ? channels[c][idx] : 0;
                }
            }

            if (midSide)
            {
                // mid + side = left, mid - side = right  =>  mid = (l + r) / 2, side = (l - r) / 2 (needs l + r even)
                for (var i = 0; i < n; i++)
                {
                    var l = values[0][i];
                    var r = values[1][i];
                    if (((l + r) & 1) != 0)
                    {
                        r += 1;
                        values[1][i] = r;
                        var idx = block * n + i;
                        if (idx < frames)
                        {
                            channels[1][idx] = (short)r; // keep the expected data in sync
                        }
                    }

                    values[0][i] = (l + r) / 2;
                    values[1][i] = (l - r) / 2;
                }
            }

            for (var c = 0; c < channels.Length; c++)
            {
                var v = values[c];
                int bits;
                int baseValue = 0;
                int[] packed;
                if (mode == 0)
                {
                    baseValue = v[0];
                    packed = new int[n];
                    for (var i = 0; i < n - 1; i++)
                    {
                        packed[i] = v[i + 1] - v[i];
                    }

                    bits = Math.Max(2, packed.Max(x => BitsFor(x)));
                }
                else if (mode == 1)
                {
                    packed = v;
                    bits = -Math.Max(2, v.Max(x => BitsFor(x)));
                }
                else
                {
                    packed = v;
                    bits = 0;
                }

                blockBin.U32(0x3E9A0C16).I32(baseValue).I16(bits).U16(midSide ? 1 : 0).U32(0);
                blockBin.Bytes(Pack(packed, bits == 0 ? 16 : Math.Abs(bits)));
            }

            blockData.Add(blockBin.ToArray());
        }

        var header = new Bin().U32(0xD69EA801).U32(0x130).U16(channels.Length).U16(16).U32(sampleRate).U32(frames)
            .U32(120).U32(120 + 4 * (numBlocks + 1)).U32(0).Zeros(88);
        var offset = 0;
        header.U32(0);
        foreach (var block in blockData)
        {
            offset += block.Length;
            header.U32(offset);
        }

        foreach (var block in blockData)
        {
            header.Bytes(block);
        }

        return header.ToArray();
    }

    private static int BitsFor(int value)
    {
        var bits = 1;
        while (value < -(1 << (bits - 1)) || value > (1 << (bits - 1)) - 1)
        {
            bits++;
        }

        return bits;
    }

    private static byte[] Pack(int[] values, int width)
    {
        var bytes = new byte[values.Length * width / 8];
        long bitPos = 0;
        foreach (var value in values)
        {
            var v = (ulong)(uint)value & ((1UL << width) - 1);
            for (var b = 0; b < width; b++, bitPos++)
            {
                if (((v >> b) & 1) != 0)
                {
                    bytes[bitPos / 8] |= (byte)(1 << (int)(bitPos % 8));
                }
            }
        }

        return bytes;
    }
}
