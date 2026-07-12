using NkxToolUI.Services;
using System.Windows;

namespace NkxToolUI.Views;

public partial class MainWindow : Window
{
    private readonly LocalizationManager _localization = LocalizationManager.Current;
    private bool _isUpdatingThemeToggle;

    public MainWindow()
    {
        InitializeComponent();
        ApplyTranslations();
        InitializeThemeToggle();

        ThemeManager.Current.ThemeChanged += ThemeManager_OnThemeChanged;
        Closed += MainWindow_Closed;
    }

    private void ApplyTranslations()
    {
        Title = _localization.Get("main.title");

        HeaderTextBlock.Text = "NKXTool";
        DescriptionTextBlock.Text = _localization.Get("main.description");

        PackButton.Content = _localization.Get("action.pack");
        UnpackButton.Content = _localization.Get("action.unpack");
        BrowseButton.Content = _localization.Get("action.browse");
    }

    private void InitializeThemeToggle()
    {
        SetThemeToggleState(ThemeManager.Current.CurrentTheme);
    }
    private async void UpdateDatabaseButton_Click(object sender, RoutedEventArgs e)
    {
        var button = (System.Windows.Controls.Button)sender;
        var originalContent = button.Content;
        
        // Feedback visuel pendant l'opération
        button.IsEnabled = false;
        button.Content = "Updating database...";

        try
        {
            var runner = new NkxToolUI.Services.NkxToolRunner();
            var result = await runner.UpdateDatabaseAsync();

            if (result.Success)
            {
                System.Windows.MessageBox.Show(
                    this, 
                    "NKX Database successfully updated.", 
                    "Update Database", 
                    System.Windows.MessageBoxButton.OK, 
                    System.Windows.MessageBoxImage.Information);
            }
            else
            {
                // Affiche l'erreur si la commande échoue
                var errorMsg = string.IsNullOrWhiteSpace(result.StandardError) 
                    ? result.ExceptionMessage 
                    : result.StandardError;

                System.Windows.MessageBox.Show(
                    this, 
                    $"Failed to update database.\n\n{errorMsg}", 
                    "Error", 
                    System.Windows.MessageBoxButton.OK, 
                    System.Windows.MessageBoxImage.Error);
            }
        }
        finally
        {
            // Restaure l'état du bouton
            button.IsEnabled = true;
            button.Content = originalContent;
        }
    }
    private void ThemeManager_OnThemeChanged(object? sender, EventArgs e)
    {
        SetThemeToggleState(ThemeManager.Current.CurrentTheme);
    }

    private void SetThemeToggleState(string themeName)
    {
        _isUpdatingThemeToggle = true;

        try
        {
            ThemeToggleButton.IsChecked = string.Equals(
                themeName,
                "Dark",
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _isUpdatingThemeToggle = false;
        }
    }

    private void ThemeToggleButton_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingThemeToggle)
        {
            return;
        }

        ThemeManager.Current.ApplyTheme("Dark");
    }

    private void ThemeToggleButton_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingThemeToggle)
        {
            return;
        }

        ThemeManager.Current.ApplyTheme("Light");
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        ThemeManager.Current.ThemeChanged -= ThemeManager_OnThemeChanged;
    }

    private void PackButton_OnClick(object sender, RoutedEventArgs e)
    {
        new PackWindow { Owner = this }.ShowDialog();
    }

    private void UnpackButton_OnClick(object sender, RoutedEventArgs e)
    {
        new UnpackWindow { Owner = this }.ShowDialog();
    }

    private void BrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        new BrowseWindow { Owner = this }.ShowDialog();
    }
}