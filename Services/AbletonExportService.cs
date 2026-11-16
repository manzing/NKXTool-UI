using System.IO;
using NkiConverter.Conversion;

namespace NkxToolUI.Services;

/// <summary>Runs the NKI -> Ableton Sampler conversion in the background and reports progress as log lines.</summary>
public static class AbletonExportService
{
    /// <summary>Name of the folder (inside the unpack output folder) which receives the Ableton presets.</summary>
    public const string DefaultOutputFolderName = "Ableton Sampler";

    public static Task<AbletonExportSummary> ConvertAsync(
        IReadOnlyList<string> nkiFiles,
        string outputFolder,
        int abletonVersion,
        IReadOnlyList<string> sampleSearchFolders,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Convert(nkiFiles, outputFolder, abletonVersion, sampleSearchFolders, log, cancellationToken), cancellationToken);
    }

    private static AbletonExportSummary Convert(
        IReadOnlyList<string> nkiFiles,
        string outputFolder,
        int abletonVersion,
        IReadOnlyList<string> sampleSearchFolders,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var summary = new AbletonExportSummary { OutputFolder = outputFolder };
        Directory.CreateDirectory(outputFolder);

        var converter = new AbletonConverter(
            new ConversionOptions
            {
                OutputFolder = outputFolder,
                AbletonVersion = abletonVersion,
                SampleSearchFolders = sampleSearchFolders
            },
            log);

        log($"Converting {nkiFiles.Count} instrument(s) to Ableton Live {abletonVersion} Sampler presets.");
        log($"Output: {outputFolder}");

        for (var i = 0; i < nkiFiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nki = nkiFiles[i];
            log($"[{i + 1}/{nkiFiles.Count}] {Path.GetFileName(nki)}");

            var result = converter.Convert(nki, cancellationToken);
            foreach (var warning in result.Warnings)
            {
                log($"    ! {warning}");
            }

            if (result.Success)
            {
                summary.Converted++;
                summary.Presets += result.PresetFiles.Count;
                foreach (var preset in result.PresetFiles)
                {
                    log($"    -> {Path.GetRelativePath(outputFolder, preset)}");
                }
            }
            else
            {
                summary.Failed++;
                log($"    ERROR: {result.Error ?? "No preset was created."}");
            }
        }

        log(summary.Failed == 0
            ? $"Done: {summary.Presets} preset(s) created."
            : $"Done: {summary.Presets} preset(s) created, {summary.Failed} instrument(s) failed.");

        if (summary.Presets > 0)
        {
            log("Tip: copy the 'Presets' and 'Samples' folders into your Ableton User Library, or drag an .adv file onto a MIDI track in Live.");
        }

        return summary;
    }
}

public sealed class AbletonExportSummary
{
    public string OutputFolder { get; init; } = string.Empty;
    public int Converted { get; set; }
    public int Failed { get; set; }
    public int Presets { get; set; }
}
