using NkiConverter.IO;

namespace NkiConverter.Kontakt;

/// <summary>IDs of the chunks inside the Kontakt preset data (Kontakt 4.2 and later).</summary>
internal static class PresetChunkId
{
    public const int ParModBase = 0x00;
    public const int Bank = 0x03;
    public const int Group = 0x04;
    public const int ParScript = 0x06;
    public const int ParExternalMod = 0x0C;
    public const int ParInternalMod = 0x0D;
    public const int ParFxSendLevels = 0x17;
    public const int ParFx = 0x25;
    public const int Program = 0x28;
    public const int ProgramContainer = 0x29;
    public const int Zone = 0x2C;
    public const int VoiceGroups = 0x32;
    public const int GroupList = 0x33;
    public const int ZoneList = 0x34;
    public const int PrivateRawObject = 0x35;
    public const int ProgramList = 0x36;
    public const int SlotList = 0x37;
    public const int StartCriteriaList = 0x38;
    public const int LoopArray = 0x39;
    public const int ParameterArray8 = 0x3A;
    public const int ParameterArray16 = 0x3B;
    public const int ParameterArray32 = 0x3C;
    public const int FilenameList = 0x3D;
    public const int OutputConfiguration = 0x3E;
    public const int InsertBus = 0x45;
    public const int SaveSettings = 0x47;
    public const int MultiConfiguration = 0x48;
    public const int ParGroupDynamics = 0x4A;
    public const int FilenameListEx = 0x4B;
    public const int QuickBrowseData = 0x4E;
}

/// <summary>A (possibly structured) chunk of the Kontakt preset data.</summary>
internal sealed class PresetChunk
{
    private const int FixedArrayVersionWithSize = 0x13;

    public int Id { get; set; } = -1;

    public int Version { get; private set; } = -1;

    public int SlotIndex { get; private set; } = -1;

    public byte[] PublicData { get; private set; } = [];

    public byte[] PrivateData { get; private set; } = [];

    public List<PresetChunk> Children { get; } = [];

    public static List<PresetChunk> ReadAll(byte[] data)
    {
        var result = new List<PresetChunk>();
        var reader = new ByteReader(data);
        while (reader.Remaining >= 6)
        {
            result.Add(Read(reader));
        }

        return result;
    }

    public static PresetChunk Read(ByteReader input)
    {
        var chunk = new PresetChunk { Id = input.U16() };
        var size = (int)input.U32();
        var obj = input.Sub(size);

        switch (chunk.Id)
        {
            case PresetChunkId.GroupList:
                chunk.ReadArray(obj, false);
                foreach (var child in chunk.Children)
                {
                    child.Id = PresetChunkId.Group;
                }

                break;

            case PresetChunkId.ZoneList:
                chunk.ReadArray(obj, true);
                break;

            case PresetChunkId.Bank:
            case PresetChunkId.ProgramContainer:
            case PresetChunkId.Program:
            case PresetChunkId.ParScript:
            case PresetChunkId.ParFxSendLevels:
            case PresetChunkId.VoiceGroups:
            case PresetChunkId.ParameterArray8:
            case PresetChunkId.InsertBus:
            case PresetChunkId.SaveSettings:
            case PresetChunkId.QuickBrowseData:
                chunk.ReadStructure(obj);
                break;

            case PresetChunkId.ParameterArray16:
                chunk.ReadFixedArray(obj, 16);
                break;

            case PresetChunkId.ParameterArray32:
                chunk.ReadFixedArray(obj, 32);
                break;

            default:
                chunk.PublicData = obj.RestBytes();
                break;
        }

        return chunk;
    }

    private void ReadArray(ByteReader input, bool hasReference)
    {
        var length = input.U32();
        for (var i = 0; i < length; i++)
        {
            var reference = hasReference ? (int)input.U32() : -1;
            var child = new PresetChunk { Id = reference };
            child.ReadStructure(input);
            Children.Add(child);
        }
    }

    private void ReadFixedArray(ByteReader input, int size)
    {
        if (input.Remaining == 0 || input.U8() != 0)
        {
            // Unknown layout - keep nothing rather than failing the whole instrument
            return;
        }

        Version = input.U16();
        var slots = size;
        if (Version >= FixedArrayVersionWithSize)
        {
            slots = (int)input.U32();
        }

        for (var i = 0; i < slots && input.HasMore; i++)
        {
            if (input.U8() == 0)
            {
                continue;
            }

            var child = new PresetChunk
            {
                SlotIndex = i,
                Id = input.U16()
            };
            var dataSize = (int)input.U32();
            child.PublicData = input.Bytes(dataSize);
            Children.Add(child);
        }
    }

    private void ReadStructure(ByteReader input)
    {
        var isStructured = input.U8() > 0;
        if (!isStructured)
        {
            PublicData = input.RestBytes();
            return;
        }

        Version = input.U16();
        PrivateData = input.Bytes((int)input.U32());
        PublicData = input.Bytes((int)input.U32());
        var childrenSize = (int)input.U32();
        var childReader = input.Sub(childrenSize);
        while (childReader.Remaining >= 6)
        {
            Children.Add(Read(childReader));
        }
    }

    public PresetChunk? FindChild(int id) => Children.FirstOrDefault(c => c.Id == id);

    public static IEnumerable<PresetChunk> FindAllRecursive(IEnumerable<PresetChunk> chunks, int id)
    {
        foreach (var chunk in chunks)
        {
            if (chunk.Id == id)
            {
                yield return chunk;
            }
            else
            {
                foreach (var found in FindAllRecursive(chunk.Children, id))
                {
                    yield return found;
                }
            }
        }
    }
}
