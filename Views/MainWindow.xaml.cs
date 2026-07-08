using NkxToolUI.Services;
using System.Windows;

namespace NkxToolUI.Views;

public partial class MainWindow : Window
{
    private readonly LocalizationManager _localization = LocalizationManager.Current;

    public MainWindow()
    {
        InitializeComponent();
        ApplyTranslations();
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

    private void PackButton_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new PackWindow
        {
            Owner = this
        };

        window.ShowDialog();
    }

    private void UnpackButton_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new UnpackWindow
        {
            Owner = this
        };

        window.ShowDialog();
    }

    private void BrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new BrowseWindow
        {
            Owner = this
        };

        window.ShowDialog();
    }
}