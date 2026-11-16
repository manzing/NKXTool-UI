using System.Buffers.Binary;
using System.Text;
using NkiConverter.Model;

namespace NkiConverter.Kontakt;

/// <summary>Detects the flavour of a Kontakt NKI file and reads it into format neutral instruments.</summary>
public static class NkiReader
{
    private const uint Kontakt1InstrumentLe = 0x5EE56EB3;
    private const uint Kontakt1InstrumentBe = 0xB36EE55E;
    private const uint Kontakt2InstrumentLe = 0x1290A87F;
    private const uint Kontakt2InstrumentBe = 0x7FA89012;
    private const uint Kontakt2MultiLe = 0x01EF85AB;
    private const uint Kontakt2MultiBe = 0xAB85EF01;
    private const uint Kontakt5Monolith = 0x2F5C204E;

    public static List<Instrument> Read(string nkiFile)
    {
        var data = File.ReadAllBytes(nkiFile);
        return Read(data, nkiFile);
    }

    public static List<Instrument> Read(byte[] data, string sourceFile)
    {
        if (data.Length < 16)
        {
            throw new InvalidDataException("The file is too small to be a Kontakt instrument.");
        }

        // Kontakt 5+ container format: an 'hsin' item starts at offset 12
        if (Encoding.ASCII.GetString(data, 12, 4) == "hsin")
        {
            return Kontakt5Reader.ReadContainer(data, sourceFile);
        }

        // The magic is read as big-endian int (like the reference implementation)
        var magic = BinaryPrimitives.ReadUInt32BigEndian(data);
        switch (magic)
        {
            case Kontakt2InstrumentLe:
            case Kontakt2MultiLe:
            case Kontakt2InstrumentBe:
            case Kontakt2MultiBe:
                var bigEndian = magic is Kontakt2InstrumentBe or Kontakt2MultiBe;
                var next = bigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4))
                    : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
                if (next == 0x24)
                {
                    throw new NotSupportedException("Kontakt 1.5 instruments are not supported. Re-save the instrument with a newer Kontakt version.");
                }

                return Kontakt2Reader.Read(data, sourceFile, bigEndian);

            case Kontakt1InstrumentLe:
            case Kontakt1InstrumentBe:
                throw new NotSupportedException("Kontakt 1 instruments are not supported. Re-save the instrument with a newer Kontakt version.");

            case Kontakt5Monolith:
                throw new NotSupportedException("Kontakt 5+ monolith instruments (samples embedded in the NKI) are not supported. Save the instrument with external samples first.");

            default:
                throw new InvalidDataException($"Unknown Kontakt file type (magic 0x{magic:X8}).");
        }
    }
}
