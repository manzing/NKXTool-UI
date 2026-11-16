using NkiConverter.Audio;

namespace NkiConverter.Conversion;

/// <summary>
/// Finds the sample files referenced by an NKI. Kontakt stores paths relative to the NKI (or
/// absolute paths of the author's machine), and after extracting NKX archives the folder layout
/// may differ, so several strategies are tried: the stored path, alternative file extensions
/// (.wav / .ncw / .aif) and finally a search by file name in the given search folders.
/// </summary>
public sealed class SampleResolver
{
    private static readonly string[] AlternativeExtensions = [".ncw", ".wav", ".aif", ".aiff", ".flac"];

    private readonly List<string> _searchRoots;
    private Dictionary<string, List<string>>? _indexByName;
    private Dictionary<string, List<string>>? _indexByStem;

    public SampleResolver(IEnumerable<string> searchRoots)
    {
        _searchRoots = searchRoots
            .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> SearchRoots => _searchRoots;

    public string? Resolve(string nkiFile, string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return null;
        }

        var normalized = storedPath.Replace('\\', '/');
        var nkiFolder = Path.GetDirectoryName(Path.GetFullPath(nkiFile)) ?? string.Empty;

        foreach (var candidate in CandidatePaths(normalized, nkiFolder))
        {
            var found = WithAlternativeExtensions(candidate);
            if (found is not null)
            {
                return found;
            }
        }

        return SearchByName(normalized);
    }

    private static IEnumerable<string> CandidatePaths(string normalized, string nkiFolder)
    {
        var isAbsolute = normalized.StartsWith('/') || (normalized.Length > 2 && normalized[1] == ':');
        if (isAbsolute)
        {
            yield return normalized;
        }
        else
        {
            yield return SafeCombine(nkiFolder, normalized);

            // Kontakt libraries often reference "../Samples/..." from an "Instruments" folder - also try
            // without the leading parent references in case the NKI was extracted somewhere else
            var trimmed = normalized;
            while (trimmed.StartsWith("../", StringComparison.Ordinal))
            {
                trimmed = trimmed[3..];
                yield return SafeCombine(nkiFolder, trimmed);
            }
        }
    }

    private static string SafeCombine(string folder, string relative)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(folder, relative));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    private static string? WithAlternativeExtensions(string candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return null;
        }

        try
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var withoutExtension = Path.Combine(Path.GetDirectoryName(candidate) ?? string.Empty, Path.GetFileNameWithoutExtension(candidate));
            foreach (var extension in AlternativeExtensions)
            {
                var alternative = withoutExtension + extension;
                if (File.Exists(alternative))
                {
                    return alternative;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            // invalid path characters from foreign systems - fall through to the search
        }

        return null;
    }

    private string? SearchByName(string normalized)
    {
        if (_searchRoots.Count == 0)
        {
            return null;
        }

        BuildIndex();
        var fileName = GetFileName(normalized);
        var stem = Path.GetFileNameWithoutExtension(fileName);

        if (!_indexByName!.TryGetValue(fileName, out var matches) && !_indexByStem!.TryGetValue(stem, out matches))
        {
            return null;
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        // Several files with the same name: prefer the one whose folders match the stored path best
        var wantedFolders = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse().Skip(1).ToArray();
        return matches
            .OrderByDescending(m => CountMatchingFolders(m, wantedFolders))
            .ThenBy(m => m.Length)
            .First();
    }

    private static string GetFileName(string normalized)
    {
        var index = normalized.LastIndexOf('/');
        return index >= 0 ? normalized[(index + 1)..] : normalized;
    }

    private static int CountMatchingFolders(string file, string[] wantedFolders)
    {
        var folders = file.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse().Skip(1).ToArray();
        var count = 0;
        for (var i = 0; i < Math.Min(folders.Length, wantedFolders.Length); i++)
        {
            if (!string.Equals(folders[i], wantedFolders[i], StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            count++;
        }

        return count;
    }

    private void BuildIndex()
    {
        if (_indexByName is not null)
        {
            return;
        }

        _indexByName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        _indexByStem = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

        foreach (var root in _searchRoots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                var extension = Path.GetExtension(file);
                if (!AudioFileInfo.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                Add(_indexByName, Path.GetFileName(file), file);
                Add(_indexByStem, Path.GetFileNameWithoutExtension(file), file);
            }
        }
    }

    private static void Add(Dictionary<string, List<string>> index, string key, string file)
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index[key] = list;
        }

        if (!list.Contains(file, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(file);
        }
    }
}
