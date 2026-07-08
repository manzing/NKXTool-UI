using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NkxToolUI.Services;

public sealed class NkxToolRunner
{
    private readonly string? _customExecutablePath;

    public NkxToolRunner(string? executablePath = null)
    {
        _customExecutablePath = executablePath;
    }

    public Task<ProcessExecutionResult> PackAsync(
        string destinationArchive,
        string sourceFolderOrFileList,
        string? rootPath = null,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(BuildPackArguments(destinationArchive, sourceFolderOrFileList, rootPath), cancellationToken);
    }

    public Task<ProcessExecutionResult> UnpackAsync(
        string archiveFile,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(BuildUnpackArguments(archiveFile, outputDirectory), cancellationToken);
    }

    public async Task<NkxBrowseResult> BrowseAsync(
        string archiveFile,
        CancellationToken cancellationToken = default)
    {
        var execution = await RunAsync(BuildBrowseArguments(archiveFile), cancellationToken);
        var parsedEntries = ParseBrowseOutput(execution.StandardOutput);

        var usedMockData = parsedEntries.Count == 0;
        if (usedMockData)
        {
            parsedEntries = BuildMockEntries(archiveFile);
        }

        return new NkxBrowseResult
        {
            Execution = execution,
            Entries = parsedEntries,
            UsedMockData = usedMockData
        };
    }

    public async Task<ProcessExecutionResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var executablePath = ResolveExecutablePath();
        var commandLine = BuildCommandLine(executablePath, arguments);

        if (!File.Exists(executablePath))
        {
            return new ProcessExecutionResult
            {
                ProcessStarted = false,
                ExecutablePath = executablePath,
                CommandLine = commandLine,
                ExceptionMessage =
                    $"NkxTool.exe was not found. Checked configured path and default locations. Current resolved path: {executablePath}"
            };
        }

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory
                },
                EnableRaisingEvents = true
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    lock (standardOutput)
                    {
                        standardOutput.AppendLine(e.Data);
                    }
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    lock (standardError)
                    {
                        standardError.AppendLine(e.Data);
                    }
                }
            };

            var started = process.Start();
            if (!started)
            {
                return new ProcessExecutionResult
                {
                    ProcessStarted = false,
                    ExecutablePath = executablePath,
                    CommandLine = commandLine,
                    ExceptionMessage = "The NkxTool process could not be started."
                };
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);
            process.WaitForExit();

            return new ProcessExecutionResult
            {
                ProcessStarted = true,
                ExecutablePath = executablePath,
                CommandLine = commandLine,
                ExitCode = process.ExitCode,
                StandardOutput = standardOutput.ToString().TrimEnd(),
                StandardError = standardError.ToString().TrimEnd()
            };
        }
        catch (Exception ex)
        {
            return new ProcessExecutionResult
            {
                ProcessStarted = false,
                ExecutablePath = executablePath,
                CommandLine = commandLine,
                StandardOutput = standardOutput.ToString().TrimEnd(),
                StandardError = standardError.ToString().TrimEnd(),
                ExceptionMessage = ex.Message
            };
        }
    }

    private IReadOnlyList<string> BuildPackArguments(
        string destinationArchive,
        string sourceFolderOrFileList,
        string? rootPath)
    {
        var arguments = new List<string>
        {
            "pack",
            destinationArchive,
            sourceFolderOrFileList
        };

        if (!string.IsNullOrWhiteSpace(rootPath))
        {
            arguments.Add(rootPath);
        }

        return arguments;
    }

    private IReadOnlyList<string> BuildUnpackArguments(string archiveFile, string outputDirectory)
    {
        return ["unpack", archiveFile, outputDirectory];
    }

    private IReadOnlyList<string> BuildBrowseArguments(string archiveFile)
    {
        return ["list", archiveFile];
    }

    private string ResolveExecutablePath()
    {
        var configuredPath = LoadConfiguredExecutablePath();

        var candidates = new[]
        {
            _customExecutablePath,
            configuredPath,
            Path.Combine(AppContext.BaseDirectory, "NkxTool.exe"),
            Path.Combine(AppContext.BaseDirectory, "Tools", "NkxTool.exe"),
            Path.Combine(Environment.CurrentDirectory, "NkxTool.exe")
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path!)))
        .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return configuredPath is not null
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath))
            : Path.Combine(AppContext.BaseDirectory, "NkxTool.exe");
    }

    private static string? LoadConfiguredExecutablePath()
    {
        try
        {
            var settingsPath = Path.Combine(AppContext.BaseDirectory, "nkxtoolsettings.json");
            if (!File.Exists(settingsPath))
            {
                return null;
            }

            var json = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<NkxToolSettings>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (string.IsNullOrWhiteSpace(settings?.NkxToolPath))
            {
                return null;
            }

            return settings.NkxToolPath.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var formattedArguments = arguments.Select(QuoteForDisplay);
        return $"{Path.GetFileName(executablePath)} {string.Join(' ', formattedArguments)}".Trim();
    }

    private static string QuoteForDisplay(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        return value.Contains(' ') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;
    }

    private static List<NkxArchiveEntry> ParseBrowseOutput(string stdout)
    {
        var results = new Dictionary<string, NkxArchiveEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();

            if (string.IsNullOrWhiteSpace(line) || LooksLikeNoise(line))
            {
                continue;
            }

            var candidatePath = line
                .Replace('/', '\\')
                .Trim()
                .Trim('\\');

            if (string.IsNullOrWhiteSpace(candidatePath))
            {
                continue;
            }

            AddEntryWithParents(results, candidatePath, false, null);
        }

        return results.Values
            .OrderBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool LooksLikeNoise(string line)
    {
        var normalized = line.Trim();

        return normalized.StartsWith("usage", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("error", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("warning", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("done", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("completed", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("packing", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("unpacking", StringComparison.OrdinalIgnoreCase);
    }

    private static List<NkxArchiveEntry> BuildMockEntries(string archiveFile)
    {
        var results = new Dictionary<string, NkxArchiveEntry>(StringComparer.OrdinalIgnoreCase);

        var archiveRootName = Path.GetFileNameWithoutExtension(archiveFile);

        AddEntryWithParents(results, $@"{archiveRootName}\Instruments\Main.nki", false, 245760);
        AddEntryWithParents(results, $@"{archiveRootName}\Samples\Drums\Kick.wav", false, 582144);
        AddEntryWithParents(results, $@"{archiveRootName}\Samples\Drums\Snare.wav", false, 603120);
        AddEntryWithParents(results, $@"{archiveRootName}\Samples\Piano\C3.wav", false, 1293312);
        AddEntryWithParents(results, $@"{archiveRootName}\Resources\Wallpaper.png", false, 125440);
        AddEntryWithParents(results, $@"{archiveRootName}\Docs\Readme.txt", false, 2048);

        return results.Values
            .OrderBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddEntryWithParents(
        IDictionary<string, NkxArchiveEntry> results,
        string fullPath,
        bool isDirectory,
        long? size)
    {
        var normalizedPath = fullPath.Replace('/', '\\').Trim('\\');

        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

        var segments = normalizedPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < segments.Length; i++)
        {
            var currentPath = string.Join('\\', segments.Take(i + 1));
            var currentIsDirectory = i < segments.Length - 1 || isDirectory;

            if (!results.ContainsKey(currentPath))
            {
                results[currentPath] = new NkxArchiveEntry
                {
                    FullPath = currentPath,
                    IsDirectory = currentIsDirectory,
                    Size = currentIsDirectory ? null : size
                };
            }
        }
    }

    private sealed class NkxToolSettings
    {
        public string? NkxToolPath { get; set; }
    }
}

public sealed class ProcessExecutionResult
{
    public bool ProcessStarted { get; init; }
    public int? ExitCode { get; init; }
    public string ExecutablePath { get; init; } = string.Empty;
    public string CommandLine { get; init; } = string.Empty;
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public string ExceptionMessage { get; init; } = string.Empty;

    public bool Success =>
        ProcessStarted &&
        ExitCode == 0 &&
        string.IsNullOrWhiteSpace(ExceptionMessage);

    public string CombinedOutput =>
        string.Join(
            Environment.NewLine,
            new[] { StandardOutput, StandardError, ExceptionMessage }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed class NkxBrowseResult
{
    public ProcessExecutionResult Execution { get; init; } = new();
    public IReadOnlyList<NkxArchiveEntry> Entries { get; init; } = [];
    public bool UsedMockData { get; init; }
}

public sealed class NkxArchiveEntry
{
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long? Size { get; init; }
}