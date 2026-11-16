using NkiConverter.Ableton;
using NkiConverter.Audio;
using NkiConverter.Kontakt;
using NkiConverter.Model;

namespace NkiConverter.Conversion;

public sealed class ConversionOptions
{
    /// <summary>
    /// Root of the output. The layout matches an Ableton User Library:
    /// "Presets/Instruments/Sampler/*.adv" and "Samples/Imported/&lt;instrument&gt;/*".
    /// </summary>
    public required string OutputFolder { get; init; }

    /// <summary>11 or 12. Live 12 presets support native round robin.</summary>
    public int AbletonVersion { get; init; } = 12;

    /// <summary>Additional folders which are searched for samples that are not found at their stored location.</summary>
    public IReadOnlyList<string> SampleSearchFolders { get; init; } = [];
}

public sealed class ConversionResult
{
    public required string NkiFile { get; init; }
    public List<string> PresetFiles { get; } = [];
    public List<string> Warnings { get; } = [];
    public string? Error { get; set; }
    public bool Success => Error is null && PresetFiles.Count > 0;
}

/// <summary>
/// Converts Kontakt NKI files into Ableton Sampler presets. One instance should be used for a
/// whole batch: samples used by several instruments are only written once.
/// </summary>
public sealed class AbletonConverter
{
    private readonly ConversionOptions _options;
    private readonly Action<string>? _log;
    private readonly Dictionary<string, WrittenSample> _writtenSamples = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _usedSampleTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _writtenPresets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SampleResolver> _resolvers = new(StringComparer.OrdinalIgnoreCase);

    public AbletonConverter(ConversionOptions options, Action<string>? log = null)
    {
        _options = options;
        _log = log;
    }

    public string PresetFolder => Path.Combine(_options.OutputFolder, "Presets", "Instruments", "Sampler");

    public string SampleFolder => Path.Combine(_options.OutputFolder, "Samples", "Imported");

    /// <summary>Finds all NKI files in the given folder and its sub-folders.</summary>
    public static List<string> FindNkiFiles(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.nki", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive })
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    public ConversionResult Convert(string nkiFile, CancellationToken cancellationToken = default)
    {
        var result = new ConversionResult { NkiFile = nkiFile };
        try
        {
            var instruments = NkiReader.Read(nkiFile);
            foreach (var instrument in instruments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Warnings.AddRange(instrument.Warnings);
                var preset = ConvertInstrument(instrument, nkiFile, result.Warnings, cancellationToken);
                result.PresetFiles.Add(preset);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    private string ConvertInstrument(Instrument instrument, string nkiFile, List<string> warnings, CancellationToken cancellationToken)
    {
        var resolver = GetResolver(nkiFile);
        var safeName = SafeFileName(instrument.Name);
        var samples = new Dictionary<SampleZone, WrittenSample>();
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var zone in instrument.AllZones)
        {
            cancellationToken.ThrowIfCancellationRequested();
            zone.ResolvedSampleFile = resolver.Resolve(nkiFile, zone.SamplePath);
            if (zone.ResolvedSampleFile is null)
            {
                missing.Add(zone.SamplePath);
                continue;
            }

            try
            {
                samples[zone] = WriteSample(zone.ResolvedSampleFile, safeName);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                failed.Add($"{Path.GetFileName(zone.ResolvedSampleFile)}: {ex.Message}");
            }
        }

        if (missing.Count > 0)
        {
            warnings.Add($"{missing.Count} sample file(s) not found, their zones were skipped (e.g. {string.Join(", ", missing.Take(3))}). Unpack the NKX archive(s) containing the samples or add their folder to the search folders.");
        }

        foreach (var failure in failed.Take(5))
        {
            warnings.Add($"Sample could not be converted - {failure}");
        }

        if (samples.Count == 0)
        {
            throw new InvalidDataException(missing.Count > 0
                ? $"None of the {missing.Count} referenced sample file(s) could be found (e.g. {missing.First()})."
                : "The instrument does not contain any sample zones (it may be script- or synth-based).");
        }

        var presetFile = UniquePresetFile(safeName);
        AdvWriter.Write(instrument, samples, presetFile, _options.AbletonVersion, warnings);
        _writtenPresets.Add(presetFile);
        return presetFile;
    }

    private SampleResolver GetResolver(string nkiFile)
    {
        // Search the NKI's folder tree up to the library root (typically "Library/Instruments/Sub/x.nki")
        var roots = new List<string>();
        var folder = Path.GetDirectoryName(Path.GetFullPath(nkiFile));
        for (var level = 0; level < 3 && folder is not null; level++)
        {
            roots.Add(folder);
            folder = Path.GetDirectoryName(folder);
        }

        // Avoid indexing whole drives or user folders
        var tooBroad = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var special in new[]
                 {
                     Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.Windows
                 })
        {
            var path = Environment.GetFolderPath(special);
            if (!string.IsNullOrEmpty(path))
            {
                tooBroad.Add(Path.TrimEndingDirectorySeparator(path));
            }
        }

        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        tooBroad.Add(downloads);

        roots = roots
            .Select(Path.TrimEndingDirectorySeparator)
            .Where(r => Path.GetPathRoot(r) is { } root && Path.TrimEndingDirectorySeparator(root) != r && !tooBroad.Contains(r))
            .Concat(_options.SampleSearchFolders)
            .ToList();

        var key = string.Join("|", roots.Order(StringComparer.OrdinalIgnoreCase));
        if (!_resolvers.TryGetValue(key, out var resolver))
        {
            resolver = new SampleResolver(roots);
            _resolvers[key] = resolver;
        }

        return resolver;
    }

    private WrittenSample WriteSample(string sourceFile, string instrumentFolderName)
    {
        if (_writtenSamples.TryGetValue(sourceFile, out var existing))
        {
            return existing;
        }

        var isNcw = NcwDecoder.IsNcw(sourceFile);
        var extension = isNcw ? ".wav" : Path.GetExtension(sourceFile).ToLowerInvariant();
        var folder = Path.Combine(SampleFolder, instrumentFolderName);
        Directory.CreateDirectory(folder);

        var baseName = SafeFileName(Path.GetFileNameWithoutExtension(sourceFile));
        var target = Path.Combine(folder, baseName + extension);
        for (var i = 2; _usedSampleTargets.Contains(target); i++)
        {
            target = Path.Combine(folder, $"{baseName} ({i}){extension}");
        }

        _usedSampleTargets.Add(target);

        AudioFileInfo info;
        if (isNcw)
        {
            _log?.Invoke($"    decoding {Path.GetFileName(sourceFile)}");
            info = NcwDecoder.ConvertToWav(sourceFile, target);
        }
        else
        {
            info = AudioFileInfo.Read(sourceFile);
            if (!string.Equals(Path.GetFullPath(sourceFile), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourceFile, target, overwrite: true);
            }
        }

        var relative = Path.GetRelativePath(_options.OutputFolder, target).Replace('\\', '/');
        var written = new WrittenSample(target, relative, info);
        _writtenSamples[sourceFile] = written;
        return written;
    }

    private string UniquePresetFile(string safeName)
    {
        var file = Path.Combine(PresetFolder, safeName + ".adv");
        for (var i = 2; _writtenPresets.Contains(file); i++)
        {
            file = Path.Combine(PresetFolder, $"{safeName} {i}.adv");
        }

        return file;
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var chars = name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(result) ? "Instrument" : result;
    }
}
