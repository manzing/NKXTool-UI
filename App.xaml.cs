using NkxToolUI.Services;
using NkxToolUI.Views;
using System.IO;
using System.Threading;
using System.Windows;

namespace NkxToolUI;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var localizationPath = Path.Combine(AppContext.BaseDirectory, "lang.json");
        var preferredLanguage = Thread.CurrentThread.CurrentUICulture.TwoLetterISOLanguageName;
        LocalizationManager.Current.Load(localizationPath, preferredLanguage);

        var startupWindow = CreateStartupWindow(e.Args);
        MainWindow = startupWindow;
        startupWindow.Show();
    }

    private static Window CreateStartupWindow(string[] args)
    {
        if (args.Length == 0)
        {
            return new MainWindow();
        }

        var firstArgument = NormalizePath(args[0]);

        if (!File.Exists(firstArgument))
        {
            return new MainWindow();
        }

        if (Path.GetExtension(firstArgument).Equals(".nkx", StringComparison.OrdinalIgnoreCase))
        {
            return new BrowseWindow(firstArgument);
        }

        return new MainWindow();
    }

    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim().Trim('"');
        var expanded = Environment.ExpandEnvironmentVariables(trimmed);
        return Path.GetFullPath(expanded);
    }
}