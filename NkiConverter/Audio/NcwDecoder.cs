using System.Buffers.Binary;

namespace NkiConverter.Audio;

/// <summary>
/// Decoder for Native Instruments compressed wave files (NCW), which Kontakt libraries use for
/// their samples. Ableton cannot read NCW, so they are converted to standard WAV files.
/// </summary>
public static class NcwDecoder
{
    private const uint FileMagic = 0xD69EA801;
    private const uint BlockMagic = 0x3E9A0C16;
    private const int SamplesPerBlock = 512;
    private const int HeaderSize = 120;
    private const int BlockHeaderSize = 16;
    private const int FlagMidSide = 1;
    private const int FlagIeeeFloat = 2;

    public static bool IsNcw(string file) => string.Equals(Path.GetExtension(file), ".ncw", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decodes an NCW file and writes it as WAV file.</summary>
    public static AudioFileInfo ConvertToWav(string ncwFile, string wavFile)
    {
        var data = File.ReadAllBytes(ncwFile);
        var decoded = Decode(data);
        WavWriter.Write(wavFile, decoded);
        return new AudioFileInfo(decoded.SampleRate, decoded.Channels, decoded.BitsPerSample, decoded.Frames);
    }

    public static DecodedAudio Decode(byte[] data)
    {
        if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != FileMagic)
        {
            throw new InvalidDataException("Not an NCW file.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        if (version is not (0x130 or 0x131))
        {
            throw new InvalidDataException($"Unsupported NCW version 0x{version:X}.");
        }

        var channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10));
        var sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        var frames = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        var blockAddressOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20));
        var blockDataOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(24));

        if (channels == 0 || bitsPerSample is not (8 or 16 or 24 or 32) || frames < 0 || blockDataOffset < blockAddressOffset || blockDataOffset > data.Length)
        {
            throw new InvalidDataException("Corrupted NCW header.");
        }

        var numOffsets = (blockDataOffset - blockAddressOffset) / 4;
        var offsets = new int[numOffsets];
        for (var i = 0; i < numOffsets; i++)
        {
            offsets[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(blockAddressOffset + i * 4));
        }

        var samples = new int[channels][];
        for (var c = 0; c < channels; c++)
        {
            samples[c] = new int[frames];
        }

        var isFloat = false;
        var block = new int[SamplesPerBlock];

        for (var b = 0; b + 1 < numOffsets; b++)
        {
            var pos = blockDataOffset + offsets[b];
            var frameOffset = b * SamplesPerBlock;
            if (frameOffset >= frames)
            {
                break;
            }

            var length = Math.Min(SamplesPerBlock, frames - frameOffset);
            var isMidSide = false;

            for (var c = 0; c < channels; c++)
            {
                if (pos + BlockHeaderSize > data.Length || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)) != BlockMagic)
                {
                    throw new InvalidDataException($"Corrupted NCW block {b}.");
                }

                var baseValue = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 4));
                int bits = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(pos + 8));
                int flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 10));
                pos += BlockHeaderSize;

                if ((flags & FlagMidSide) != 0)
                {
                    isMidSide = true;
                }

                if ((flags & FlagIeeeFloat) != 0)
                {
                    isFloat = true;
                }

                var width = bits == 0 ? bitsPerSample : Math.Abs(bits);
                var byteCount = SamplesPerBlock * width / 8;
                if (pos + byteCount > data.Length)
                {
                    throw new InvalidDataException($"Truncated NCW block {b}.");
                }

                UnpackValues(data, pos, width, block);
                pos += byteCount;

                if (bits > 0)
                {
                    // Delta encoding: first sample is the base value, followed by the deltas
                    var value = baseValue;
                    for (var i = 0; i < SamplesPerBlock; i++)
                    {
                        var delta = block[i];
                        block[i] = value;
                        value = unchecked(value + delta);
                    }
                }

                Array.Copy(block, 0, samples[c], frameOffset, length);
            }

            if (isMidSide && channels == 2)
            {
                var left = samples[0];
                var right = samples[1];
                for (var i = frameOffset; i < frameOffset + length; i++)
                {
                    if (isFloat)
                    {
                        var mid = BitConverter.Int32BitsToSingle(left[i]);
                        var side = BitConverter.Int32BitsToSingle(right[i]);
                        left[i] = BitConverter.SingleToInt32Bits(mid + side);
                        right[i] = BitConverter.SingleToInt32Bits(mid - side);
                    }
                    else
                    {
                        var mid = left[i];
                        var side = right[i];
                        left[i] = unchecked(mid + side);
                        right[i] = unchecked(mid - side);
                    }
                }
            }
        }

        return new DecodedAudio(sampleRate, channels, isFloat ? 32 : bitsPerSample, isFloat, frames, samples);
    }

    /// <summary>Reads 512 values of the given bit width, packed LSB first, and sign extends them.</summary>
    private static void UnpackValues(byte[] data, int offset, int width, int[] target)
    {
        if (width % 8 == 0)
        {
            var bytes = width / 8;
            for (var i = 0; i < SamplesPerBlock; i++)
            {
                var p = offset + i * bytes;
                target[i] = bytes switch
                {
                    1 => unchecked((sbyte)data[p]),
                    2 => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(p)),
                    3 => (data[p] | data[p + 1] << 8 | (sbyte)data[p + 2] << 16),
                    _ => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p))
                };
            }

            return;
        }

        ulong accumulator = 0;
        var bitsInAccumulator = 0;
        var pos = offset;
        var mask = (1UL << width) - 1;
        for (var i = 0; i < SamplesPerBlock; i++)
        {
            while (bitsInAccumulator < width)
            {
                accumulator |= (ulong)data[pos++] << bitsInAccumulator;
                bitsInAccumulator += 8;
            }

            var value = (long)(accumulator & mask);
            accumulator >>= width;
            bitsInAccumulator -= width;

            if (width < 32 && (value & (1L << (width - 1))) != 0)
            {
                value -= 1L << width;
            }

            target[i] = unchecked((int)value);
        }
    }
}

/// <summary>Decoded PCM data, one array per channel. For float data the ints hold the IEEE bit patterns.</summary>
public sealed record DecodedAudio(int SampleRate, int Channels, int BitsPerSample, bool IsFloat, int Frames, int[][] Samples);
