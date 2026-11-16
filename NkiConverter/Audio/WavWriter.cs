using System.Text;

namespace NkiConverter.Audio;

/// <summary>Writes PCM / IEEE float WAV files.</summary>
public static class WavWriter
{
    private const ushort FormatPcm = 1;
    private const ushort FormatIeeeFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    public static void Write(string file, DecodedAudio audio)
    {
        var bytesPerSample = audio.BitsPerSample / 8;
        var blockAlign = audio.Channels * bytesPerSample;
        var dataSize = (long)audio.Frames * blockAlign;
        if (dataSize > uint.MaxValue - 100)
        {
            throw new InvalidDataException("The decoded sample is too large for a WAV file.");
        }

        // WAVE_FORMAT_EXTENSIBLE is required for more than 2 channels or more than 16 bit integer samples
        var extensible = audio.Channels > 2 || (!audio.IsFloat && audio.BitsPerSample > 16);
        var fmtSize = extensible ? 40 : (audio.IsFloat ? 18 : 16);
        var format = audio.IsFloat ? FormatIeeeFloat : FormatPcm;

        using var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var w = new BinaryWriter(stream);

        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write((uint)(4 + 8 + fmtSize + 8 + dataSize + (dataSize & 1)));
        w.Write(Encoding.ASCII.GetBytes("WAVE"));

        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write((uint)fmtSize);
        w.Write(extensible ? FormatExtensible : format);
        w.Write((ushort)audio.Channels);
        w.Write((uint)audio.SampleRate);
        w.Write((uint)(audio.SampleRate * blockAlign));
        w.Write((ushort)blockAlign);
        w.Write((ushort)audio.BitsPerSample);
        if (extensible)
        {
            w.Write((ushort)22);
            w.Write((ushort)audio.BitsPerSample); // valid bits
            w.Write(audio.Channels switch { 1 => 0x4u, 2 => 0x3u, _ => 0u }); // channel mask
            // Sub-format GUID: xxxxxxxx-0000-0010-8000-00aa00389b71
            w.Write((uint)format);
            w.Write(new byte[] { 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 });
        }
        else if (audio.IsFloat)
        {
            w.Write((ushort)0);
        }

        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write((uint)dataSize);

        var frameBuffer = new byte[blockAlign * 4096];
        var frame = 0;
        while (frame < audio.Frames)
        {
            var count = Math.Min(4096, audio.Frames - frame);
            var p = 0;
            for (var i = 0; i < count; i++)
            {
                for (var c = 0; c < audio.Channels; c++)
                {
                    var value = audio.Samples[c][frame + i];
                    switch (bytesPerSample)
                    {
                        case 1:
                            frameBuffer[p++] = (byte)(value + 128); // 8 bit WAV is unsigned
                            break;
                        case 2:
                            frameBuffer[p++] = (byte)value;
                            frameBuffer[p++] = (byte)(value >> 8);
                            break;
                        case 3:
                            frameBuffer[p++] = (byte)value;
                            frameBuffer[p++] = (byte)(value >> 8);
                            frameBuffer[p++] = (byte)(value >> 16);
                            break;
                        default:
                            frameBuffer[p++] = (byte)value;
                            frameBuffer[p++] = (byte)(value >> 8);
                            frameBuffer[p++] = (byte)(value >> 16);
                            frameBuffer[p++] = (byte)(value >> 24);
                            break;
                    }
                }
            }

            w.Write(frameBuffer, 0, p);
            frame += count;
        }

        if ((dataSize & 1) != 0)
        {
            w.Write((byte)0);
        }
    }
}
