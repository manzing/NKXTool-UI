namespace NkiConverter.IO;

/// <summary>
/// FastLZ decompressor (levels 1 and 2). Kontakt uses FastLZ for the preset data of
/// Kontakt 4.2 NKI files and for compressed sub-trees in the Kontakt 5+ container format.
/// </summary>
internal static class FastLz
{
    public static byte[] Decompress(byte[] input, int expectedSize)
    {
        if (input.Length == 0)
        {
            return [];
        }

        var level = (input[0] >> 5) + 1;
        var output = new byte[Math.Max(0, expectedSize)];
        var length = level switch
        {
            1 => Level1(input, output),
            2 => Level2(input, output),
            _ => throw new InvalidDataException($"Unknown FastLZ level {level}.")
        };

        return length == output.Length ? output : output.AsSpan(0, length).ToArray();
    }

    private static int Level1(byte[] input, byte[] output)
    {
        var ip = 0;
        var op = 0;
        uint ctrl = (uint)(input[ip++] & 31);

        while (true)
        {
            if (ctrl >= 32)
            {
                var len = (int)(ctrl >> 5) - 1;
                var ofs = (int)((ctrl & 31) << 8);
                var refPos = op - ofs - 1;
                if (len == 7 - 1)
                {
                    len += Next(input, ref ip);
                }

                refPos -= Next(input, ref ip);
                len += 3;
                CopyMatch(output, ref op, refPos, len);
            }
            else
            {
                var len = (int)ctrl + 1;
                CopyLiteral(input, ref ip, output, ref op, len);
            }

            if (ip >= input.Length)
            {
                break;
            }

            ctrl = input[ip++];
        }

        return op;
    }

    private static int Level2(byte[] input, byte[] output)
    {
        const int maxL2Distance = 8191;
        var ip = 0;
        var op = 0;
        uint ctrl = (uint)(input[ip++] & 31);

        while (true)
        {
            if (ctrl >= 32)
            {
                var len = (int)(ctrl >> 5) - 1;
                var ofs = (int)((ctrl & 31) << 8);
                var refPos = op - ofs - 1;

                if (len == 7 - 1)
                {
                    int code;
                    do
                    {
                        code = Next(input, ref ip);
                        len += code;
                    }
                    while (code == 255);
                }

                var code2 = Next(input, ref ip);
                refPos -= code2;
                len += 3;

                // match from 16-bit distance
                if (code2 == 255 && ofs == (31 << 8))
                {
                    ofs = Next(input, ref ip) << 8;
                    ofs += Next(input, ref ip);
                    refPos = op - ofs - maxL2Distance - 1;
                }

                CopyMatch(output, ref op, refPos, len);
            }
            else
            {
                var len = (int)ctrl + 1;
                CopyLiteral(input, ref ip, output, ref op, len);
            }

            if (ip >= input.Length)
            {
                break;
            }

            ctrl = input[ip++];
        }

        return op;
    }

    private static int Next(byte[] input, ref int ip)
    {
        if (ip >= input.Length)
        {
            throw new InvalidDataException("FastLZ: unexpected end of compressed data.");
        }

        return input[ip++];
    }

    private static void CopyMatch(byte[] output, ref int op, int refPos, int len)
    {
        if (refPos < 0)
        {
            throw new InvalidDataException("FastLZ: invalid back reference.");
        }

        if (op + len > output.Length)
        {
            throw new InvalidDataException("FastLZ: output overflow.");
        }

        // byte by byte: source and destination may overlap
        for (var i = 0; i < len; i++)
        {
            output[op++] = output[refPos++];
        }
    }

    private static void CopyLiteral(byte[] input, ref int ip, byte[] output, ref int op, int len)
    {
        if (ip + len > input.Length || op + len > output.Length)
        {
            throw new InvalidDataException("FastLZ: literal run out of range.");
        }

        Buffer.BlockCopy(input, ip, output, op, len);
        ip += len;
        op += len;
    }
}
