using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace NkxToolUI.Services;

public sealed class LocalizationManager
{
    private readonly Dictionary<string, Dictionary<string, string>> _translations =
        new(StringComparer.OrdinalIgnoreCase);

    private string _defaultLanguage = "en";

    public static LocalizationManager Current { get; } = new();

    public string CurrentLanguage { get; private set; } = "en";

    public IReadOnlyCollection<string> AvailableLanguages =>
        new ReadOnlyCollection<string>(_translations.Keys.OrderBy(key => key).ToList());

    private LocalizationManager()
    {
    }

    public void Load(string filePath, string? preferredLanguage = null)
    {
        _translations.Clear();
        _defaultLanguage = "en";
        CurrentLanguage = "en";

        if (File.Exists(filePath))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var model = JsonSerializer.Deserialize<LocalizationFile>(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                if (model?.Languages is { Count: > 0 })
                {
                    _defaultLanguage = string.IsNullOrWhiteSpace(model.DefaultLanguage)
                        ? "en"
                        : model.DefaultLanguage;

                    foreach (var language in model.Languages)
                    {
                        _translations[language.Key] = language.Value ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch
            {
                LoadBuiltInEnglish();
            }
        }

        if (_translations.Count == 0)
        {
            LoadBuiltInEnglish();
        }

        if (!TrySetLanguage(preferredLanguage) && !TrySetLanguage(_defaultLanguage))
        {
            CurrentLanguage = "en";
        }
    }

    public string Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        if (_translations.TryGetValue(CurrentLanguage, out var currentLanguageMap) &&
            currentLanguageMap.TryGetValue(key, out var currentValue))
        {
            return currentValue;
        }

        if (_translations.TryGetValue(_defaultLanguage, out var defaultLanguageMap) &&
            defaultLanguageMap.TryGetValue(key, out var defaultValue))
        {
            return defaultValue;
        }

        return key;
    }

    public bool SetLanguage(string language)
    {
        return TrySetLanguage(language);
    }

    private bool TrySetLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return false;
        }

        if (_translations.ContainsKey(language))
        {
            CurrentLanguage = language;
            return true;
        }

        return false;
    }

    private void LoadBuiltInEnglish()
    {
        _translations["en"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["app.title"] = "NkxToolUI",
            ["main.title"] = "NkxToolUI",
            ["main.header"] = "NKX Utility",
            ["main.description"] = "Modern Windows interface for packing, unpacking and browsing NKX archives.",
            ["action.pack"] = "Pack",
            ["action.unpack"] = "Unpack",
            ["action.browse"] = "Browse",
            ["action.close"] = "Close",
            ["action.refresh"] = "Refresh",
            ["pack.title"] = "Pack NKX Archive",
            ["pack.header"] = "Create an NKX archive",
            ["pack.description"] = "Create an archive from a source folder or from a @filelist.txt input with an optional root path.",
            ["pack.mode"] = "Pack mode",
            ["pack.mode.folder"] = "Pack a whole folder",
            ["pack.mode.fileList"] = "Pack from @filelist.txt",
            ["pack.sourceFolder"] = "Source folder",
            ["pack.outputArchive"] = "Output archive",
            ["pack.fileList"] = "File list",
            ["pack.rootPath"] = "Root path (optional)",
            ["pack.selectSourceFolder"] = "Browse folder",
            ["pack.selectOutputArchive"] = "Browse file",
            ["pack.selectFileList"] = "Browse file",
            ["pack.run"] = "Create archive",
            ["pack.hint"] = "CLI syntax: NkxTool pack <destination_file> <sourceFolder_OR_@filelist.txt> [rootPath]",
            ["pack.log"] = "Console output",
            ["pack.status.ready"] = "Ready.",
            ["pack.status.running"] = "Packing archive...",
            ["pack.status.completed"] = "Pack completed.",
            ["pack.status.failed"] = "Pack failed.",
            ["pack.validation.sourceMissing"] = "Please select an existing source folder.",
            ["pack.validation.outputMissing"] = "Please select an output .nkx file.",
            ["pack.validation.fileListMissing"] = "Please select an existing file list.",
            ["unpack.title"] = "Unpack NKX Archive",
            ["unpack.header"] = "Extract one or more NKX archives",
            ["unpack.description"] = "Add one or several archives, choose the destination folder, then start extraction.",
            ["unpack.inputArchives"] = "Input archives",
            ["unpack.addArchives"] = "Add NKX files",
            ["unpack.removeSelected"] = "Remove selected",
            ["unpack.outputFolder"] = "Output folder",
            ["unpack.selectOutputFolder"] = "Browse folder",
            ["unpack.createSubfolders"] = "Create one subfolder per archive",
            ["unpack.run"] = "Extract archives",
            ["unpack.log"] = "Console output",
            ["unpack.status.ready"] = "Ready.",
            ["unpack.status.running"] = "Extracting archives...",
            ["unpack.status.completed"] = "Unpack completed.",
            ["unpack.status.failed"] = "One or more archives failed to extract.",
            ["unpack.validation.archivesMissing"] = "Please add at least one NKX archive.",
            ["unpack.validation.outputMissing"] = "Please select an output folder.",
            ["browse.title"] = "Browse NKX Archive",
            ["browse.header"] = "Browse archive contents",
            ["browse.description"] = "Open an NKX archive and inspect its internal tree. If no real listing is available yet, a temporary mock structure is shown.",
            ["browse.archivePath"] = "Archive path",
            ["browse.selectArchive"] = "Open archive",
            ["browse.tree"] = "Archive tree",
            ["browse.details"] = "Selected node contents",
            ["browse.output"] = "Raw tool output",
            ["browse.status.ready"] = "Ready.",
            ["browse.status.loaded"] = "Archive loaded.",
            ["browse.status.mock"] = "Archive opened with temporary/mock listing.",
            ["browse.validation.archiveMissing"] = "Please select an existing NKX archive.",
            ["browse.column.name"] = "Name",
            ["browse.column.type"] = "Type",
            ["browse.column.path"] = "Path",
            ["browse.column.size"] = "Size"
        };

        _defaultLanguage = "en";
        CurrentLanguage = "en";
    }

    private sealed class LocalizationFile
    {
        public string DefaultLanguage { get; set; } = "en";
        public Dictionary<string, Dictionary<string, string>> Languages { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}