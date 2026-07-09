using System.IO;
using System.Text.Json;
using System.Windows;

namespace NkxToolUI.Services;

public sealed class ThemeManager
{
    private const string DefaultTheme = "Light";
    private const string SettingsFileName = "ui-settings.json";

    public static ThemeManager Current { get; } = new();

    public IReadOnlyList<string> AvailableThemes { get; } = ["Light", "Dark"];

    public string CurrentTheme { get; private set; } = DefaultTheme;

    public event EventHandler? ThemeChanged;

    private ThemeManager()
    {
    }

    public void Initialize()
    {
        ApplyTheme(LoadSavedTheme(), persist: false);
    }

    public void ApplyTheme(string? themeName, bool persist = true)
    {
        var normalizedTheme = NormalizeTheme(themeName);
        var app = Application.Current;

        if (app is null)
        {
            CurrentTheme = normalizedTheme;
            return;
        }

        var mergedDictionaries = app.Resources.MergedDictionaries;
        var themeUri = new Uri($"Themes/{normalizedTheme}.xaml", UriKind.Relative);
        var existingThemeDictionaries = mergedDictionaries
            .Where(IsThemeDictionary)
            .ToList();

        foreach (var dictionary in existingThemeDictionaries)
        {
            mergedDictionaries.Remove(dictionary);
        }

        mergedDictionaries.Add(new ResourceDictionary { Source = themeUri });

        CurrentTheme = normalizedTheme;

        if (persist)
        {
            SaveTheme(normalizedTheme);
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString;
        return !string.IsNullOrWhiteSpace(source) &&
               source.StartsWith("Themes/", StringComparison.OrdinalIgnoreCase);
    }

    private string NormalizeTheme(string? themeName)
    {
        if (string.IsNullOrWhiteSpace(themeName))
        {
            return DefaultTheme;
        }

        var match = AvailableThemes.FirstOrDefault(theme =>
            string.Equals(theme, themeName, StringComparison.OrdinalIgnoreCase));

        return match ?? DefaultTheme;
    }

    private static string GetSettingsDirectory()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NkxToolUI");

        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetSettingsPath()
    {
        return Path.Combine(GetSettingsDirectory(), SettingsFileName);
    }

    private string LoadSavedTheme()
    {
        try
        {
            var settingsPath = GetSettingsPath();
            if (!File.Exists(settingsPath))
            {
                return DefaultTheme;
            }

            var json = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<UiSettings>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            return string.IsNullOrWhiteSpace(settings?.Theme)
                ? DefaultTheme
                : NormalizeTheme(settings.Theme);
        }
        catch
        {
            return DefaultTheme;
        }
    }

    private static void SaveTheme(string themeName)
    {
        try
        {
            var settings = new UiSettings
            {
                Theme = themeName
            };

            var json = JsonSerializer.Serialize(
                settings,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

            File.WriteAllText(GetSettingsPath(), json);
        }
        catch
        {
        }
    }

    private sealed class UiSettings
    {
        public string Theme { get; set; } = DefaultTheme;
    }
}
