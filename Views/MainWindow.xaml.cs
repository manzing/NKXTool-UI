using NkxToolUI.Services;
using System.Windows;
using System.Windows.Controls;

namespace NkxToolUI.Views;

public partial class MainWindow : Window
{
    private readonly LocalizationManager _localization = LocalizationManager.Current;
    private bool _isUpdatingThemeSelector;

    public MainWindow()
    {
        InitializeComponent();
        ApplyTranslations();
        InitializeThemeSelector();
        ThemeManager.Current.ThemeChanged += ThemeManager_OnThemeChanged;
        Closed += MainWindow_Closed;
    }

    private void ApplyTranslations()
    {
        Title = _localization.Get("main.title");
        HeaderTextBlock.Text = _localization.Get("main.header");
        DescriptionTextBlock.Text = _localization.Get("main.description");
        PackButton.Content = _localization.Get("action.pack");
        UnpackButton.Content = _localization.Get("action.unpack");
        BrowseButton.Content = _localization.Get("action.browse");
    }

    private void InitializeThemeSelector() => SetThemeComboSelection(ThemeManager.Current.CurrentTheme);

    private void ThemeManager_OnThemeChanged(object? sender, EventArgs e) => SetThemeComboSelection(ThemeManager.Current.CurrentTheme);

    private void SetThemeComboSelection(string themeName)
    {
        _isUpdatingThemeSelector = true;
        try
        {
            foreach (var item in ThemeComboBox.Items.OfType<ComboBoxItem>())
                if (string.Equals(item.Content?.ToString(), themeName, StringComparison.OrdinalIgnoreCase))
                {
                    ThemeComboBox.SelectedItem = item;
                    break;
                }
        }
        finally { _isUpdatingThemeSelector = false; }
    }

    private void ThemeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingThemeSelector) return;
        if (ThemeComboBox.SelectedItem is ComboBoxItem item && item.Content is string themeName)
            ThemeManager.Current.ApplyTheme(themeName);
    }

    private void MainWindow_Closed(object? sender, EventArgs e) => ThemeManager.Current.ThemeChanged -= ThemeManager_OnThemeChanged;

    private void PackButton_OnClick(object sender, RoutedEventArgs e) { new PackWindow { Owner = this }.ShowDialog(); }
    private void UnpackButton_OnClick(object sender, RoutedEventArgs e) { new UnpackWindow { Owner = this }.ShowDialog(); }
    private void BrowseButton_OnClick(object sender, RoutedEventArgs e) { new BrowseWindow { Owner = this }.ShowDialog(); }
}
