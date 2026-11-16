using System.Text;
using NkiConverter.IO;

namespace NkiConverter.Kontakt;

/// <summary>Reads the list of sample files referenced by the zones (FILENAME_LIST / FILENAME_LIST_EX chunks).</summary>
internal static class FileList
{
    public static List<string> ReadSampleFiles(IReadOnlyList<PresetChunk> topChunks)
    {
        var chunk = topChunks.FirstOrDefault(c => c.Id == PresetChunkId.FilenameList)
                    ?? topChunks.FirstOrDefault(c => c.Id == PresetChunkId.FilenameListEx);
        if (chunk is null)
        {
            return [];
        }

        var r = new ByteReader(chunk.PublicData);
        if (chunk.Id == PresetChunkId.FilenameListEx)
        {
            var version = r.U16();
            if (version == 3)
            {
                return ReadFilesV3(r);
            }

            if (version != 2)
            {
                throw new InvalidDataException($"Unsupported file list version {version}.");
            }
        }

        ReadFiles(r); // "special" files (e.g. wallpaper, scripts)
        return ReadFiles(r);
    }

    private static List<string> ReadFiles(ByteReader r)
    {
        var files = new List<string>();
        if (!r.HasMore)
        {
            return files;
        }

        var count = r.I32();
        for (var i = 0; i < count; i++)
        {
            files.Add(ReadFile(r));
        }

        return files;
    }

    private static List<string> ReadFilesV3(ByteReader r)
    {
        var files = new List<string>();
        if (!r.HasMore)
        {
            return files;
        }

        var count = r.I32();
        r.Skip(8);
        for (var i = 0; i < count - 1; i++)
        {
            files.Add(ReadFile(r));
            if (r.HasMore)
            {
                r.Skip(4 + 4 + 20); // unknown, timestamp, unknown
            }
        }

        return files;
    }

    private static string ReadFile(ByteReader r)
    {
        var sb = new StringBuilder();
        var segments = r.U32();
        for (var s = 0; s < segments; s++)
        {
            var type = r.U8();
            switch (type)
            {
                case 0:
                    var oldDrive = r.Ascii(2).Trim('\0', ' ');
                    sb.Append(oldDrive.Length == 0 ? "/" : oldDrive + ":/");
                    break;
                case 1:
                    var drive = r.Utf16WithLength();
                    sb.Append(drive.Length == 0 ? "/" : drive + ":/");
                    break;
                case 2:
                    sb.Append(r.Utf16WithLength()).Append('/');
                    break;
                case 3:
                    sb.Append("../");
                    break;
                case 4:
                case 8:
                case 9:
                    sb.Append(r.Utf16WithLength());
                    break;
                case 6:
                    break;
                default:
                    throw new InvalidDataException($"Unsupported file list path segment type {type}.");
            }
        }

        return sb.ToString();
    }
}
