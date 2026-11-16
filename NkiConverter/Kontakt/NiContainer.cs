using NkiConverter.IO;

namespace NkiConverter.Kontakt;

/// <summary>Chunk types of the Native Instruments container format ("hsin" items) used by Kontakt 5 and later.</summary>
internal enum NiChunkType
{
    Terminator = 1,
    BniSoundPreset = 3,
    BniSoundHeader = 4,
    Bank = 100,
    AuthoringApplication = 101,
    BankContainer = 102,
    PresetContainer = 103,
    BinaryChunkItem = 104,
    Authorization = 106,
    SoundinfoItem = 108,
    PresetChunkItem = 109,
    ExternalFileReference = 110,
    Resources = 111,
    AudioSampleItem = 112,
    InternalResourceReferenceItem = 113,
    PictureItem = 114,
    SubTreeItem = 115,
    EncryptionItem = 116,
    AppSpecific = 117,
    ContainerRoot = 118,
    AutomationParameters = 120,
    ControllerAssignments = 121,
    Module = 122,
    ModuleBank = 123
}

/// <summary>An item of the NI container. An item has a stack of data chunks and child items.</summary>
internal sealed class NiContainerItem
{
    public List<NiDataChunk> Chunks { get; } = [];

    public List<NiContainerItem> Children { get; } = [];

    public static NiContainerItem Read(ByteReader input)
    {
        var item = new NiContainerItem();
        var block = input.Block64();

        var headerVersion = block.U32();
        if (headerVersion != 1)
        {
            throw new InvalidDataException($"Unsupported NI container item version {headerVersion}.");
        }

        var domain = block.Ascii(4);
        if (domain != "hsin")
        {
            throw new InvalidDataException("Corrupted NI container: 'hsin' marker not found.");
        }

        block.U32();
        block.U32();
        block.Skip(16); // UUID

        NiDataChunk.ReadStack(block, item.Chunks);

        block.U32(); // item version
        var numChildren = block.U32();
        for (var i = 0; i < numChildren; i++)
        {
            block.U32(); // child index
            block.Ascii(4); // domain
            block.U32(); // chunk type
            item.Children.Add(Read(block));
        }

        return item;
    }

    /// <summary>Depth-first search for chunks of the given type, descending into (unencrypted) sub-trees and children.</summary>
    public IEnumerable<NiDataChunk> FindAll(NiChunkType type)
    {
        foreach (var chunk in Chunks)
        {
            if (chunk.Type == type)
            {
                yield return chunk;
            }

            if (chunk.SubTree is not null)
            {
                foreach (var found in chunk.SubTree.FindAll(type))
                {
                    yield return found;
                }
            }
        }

        foreach (var child in Children)
        {
            foreach (var found in child.FindAll(type))
            {
                yield return found;
            }
        }
    }

    public NiDataChunk? Find(NiChunkType type) => FindAll(type).FirstOrDefault();

    public bool HasEncryptedSubTree()
    {
        foreach (var chunk in Chunks)
        {
            if (chunk.IsEncrypted || chunk.SubTree?.HasEncryptedSubTree() == true)
            {
                return true;
            }
        }

        return Children.Any(c => c.HasEncryptedSubTree());
    }
}

/// <summary>A data chunk of an item. Only the chunk types needed for conversion are decoded, all others are kept raw.</summary>
internal sealed class NiDataChunk
{
    public int TypeId { get; private init; }

    public NiChunkType Type => (NiChunkType)TypeId;

    public byte[] Data { get; private init; } = [];

    public NiContainerItem? SubTree { get; private set; }

    public bool IsEncrypted { get; private set; }

    /// <summary>
    /// Data chunks are nested: each chunk block contains the header, then the next chunk of the stack
    /// (recursively, until a terminator), then its own data.
    /// </summary>
    public static void ReadStack(ByteReader input, List<NiDataChunk> stack)
    {
        var block = input.Block64();
        block.Ascii(4); // domain
        var typeId = (int)block.U32();
        var version = block.U32();
        if (version != 1)
        {
            throw new InvalidDataException($"Unsupported NI container chunk version {version}.");
        }

        var nested = new List<NiDataChunk>();
        if (typeId != (int)NiChunkType.Terminator)
        {
            ReadStack(block, nested);
        }

        var chunk = new NiDataChunk { TypeId = typeId, Data = block.RestBytes() };
        if (chunk.Type == NiChunkType.SubTreeItem)
        {
            chunk.ParseSubTree();
        }

        stack.Add(chunk);
        stack.AddRange(nested);
    }

    private void ParseSubTree()
    {
        try
        {
            var reader = new ByteReader(Data);
            reader.U32(); // version
            var isCompressed = reader.U8() > 0;
            if (isCompressed)
            {
                var uncompressedSize = (int)reader.U32();
                var compressedSize = (int)reader.U32();
                var compressed = reader.Bytes(compressedSize);
                byte[] uncompressed;
                try
                {
                    uncompressed = FastLz.Decompress(compressed, uncompressedSize);
                }
                catch (InvalidDataException)
                {
                    // Encrypted sub-trees cannot be decompressed (Kontakt Player / licensed content)
                    IsEncrypted = true;
                    return;
                }

                SubTree = NiContainerItem.Read(new ByteReader(uncompressed));
            }
            else
            {
                SubTree = NiContainerItem.Read(reader);
            }
        }
        catch (InvalidDataException)
        {
            IsEncrypted = true;
        }
    }
}

/// <summary>Decoded "Soundinfo" chunk: the metadata shown in the Kontakt / Komplete browser.</summary>
internal sealed class SoundInfo
{
    public string Name { get; private set; } = string.Empty;
    public string Author { get; private set; } = string.Empty;
    public string Vendor { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public List<string> Tags { get; } = [];
    public List<string> Attributes { get; } = [];
    public Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static SoundInfo? TryParse(byte[] data)
    {
        try
        {
            var r = new ByteReader(data);
            r.U32(); // chunk data version
            r.U32();
            r.U32();
            r.U32(); // soundinfo version major/minor/patch
            var info = new SoundInfo
            {
                Name = r.Utf16WithLength(),
                Author = r.Utf16WithLength(),
                Vendor = r.Utf16WithLength(),
                Description = r.Utf16WithLength()
            };
            r.U32();
            r.U64();
            r.U64();
            r.U64();
            r.U32();
            r.U32();
            var numTags = r.U32();
            for (var i = 0; i < numTags; i++)
            {
                info.Tags.Add(r.Utf16WithLength());
            }

            var numAttributes = r.U32();
            for (var i = 0; i < numAttributes; i++)
            {
                var value = r.Utf16WithLength();
                if (value != "KontaktInstrument")
                {
                    info.Attributes.Add(value);
                }
            }

            if (r.Remaining >= 8)
            {
                r.U32();
                var numProperties = r.U32();
                for (var i = 0; i < numProperties; i++)
                {
                    info.Properties[r.Utf16WithLength()] = r.Utf16WithLength();
                }
            }

            return info;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
