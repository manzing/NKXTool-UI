using NkiConverter.Conversion;
using NkxToolUI.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace NkxToolUI.Views;

/// <summary>Converts Kontakt NKI instruments (e.g. extracted from NKX archives) into Ableton Sampler presets.</summary>
public partial class ConvertWindow : Window
{
    private readonly LocalizationManager _localization = LocalizationManager.Current;
    private readonly ObservableCollection<string> _nkiFiles = [];
    private CancellationTokenSource? _cancellation;
    private bool _isBusy;

    public ConvertWindow()
    {
        InitializeComponent();
        FilesListBox.ItemsSource = _nkiFiles;
        ApplyTranslations();
        StatusTextBlock.Text = _localization.Get("convert.status.ready");
        Closing += ConvertWindow_Closing;
    }

    private void ApplyTranslations()
    {
        Title = _localization.Get("convert.title");
        HeaderTextBlock.Text = _localization.Get("convert.header");
        DescriptionTextBlock.Text = _localization.Get("convert.description");
        InputFilesLabel.Text = _localization.Get("convert.inputFiles");
        AddFilesButton.Content = _localization.Get("convert.addFiles");
        AddFolderButton.Content = _localization.Get("convert.addFolder");
        RemoveSelectedButton.Content = _localization.Get("convert.removeSelected");
        OutputFolderLabel.Text = _localization.Get("convert.outputFolder");
        BrowseOutputFolderButton.Content = _localization.Get("convert.selectFolder");
        SampleFolderLabel.Text = _localization.Get("convert.sampleFolder");
        BrowseSampleFolderButton.Content = _localization.Get("convert.selectFolder");
        LiveVersionLabel.Text = _localization.Get("convert.liveVersion");
        Live12RadioButton.Content = _localization.Get("convert.live12");
        Live11RadioButton.Content = _localization.Get("convert.live11");
        HintTextBlock.Text = _localization.Get("convert.hint");
        LogLabel.Text = _localization.Get("convert.log");
        OpenOutputButton.Content = _localization.Get("convert.openOutput");
        RunConvertButton.Content = _localization.Get("convert.run");
        CloseWindowButton.Content = _localization.Get("action.close");
    }

    private void AddFiles(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            if (string.Equals(Path.GetExtension(file), ".nki", StringComparison.OrdinalIgnoreCase) &&
                !_nkiFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
            {
                _nkiFiles.Add(file);
            }
        }

        if (string.IsNullOrWhiteSpace(OutputFolderTextBox.Text) && _nkiFiles.Count > 0)
        {
            var firstFolder = Path.GetDirectoryName(_nkiFiles[0]);
            if (firstFolder is not null)
            {
                OutputFolderTextBox.Text = Path.Combine(firstFolder, AbletonExportService.DefaultOutputFolderName);
            }
        }
    }

    private void AddFilesButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Kontakt instrument (*.nki)|*.nki|All files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            AddFiles(dialog.FileNames);
        }
    }

    private void AddFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var folder = SelectFolder(_localization.Get("convert.addFolder"), null);
        if (folder is null)
        {
            return;
        }

        var files = AbletonConverter.FindNkiFiles(folder);
        if (files.Count == 0)
        {
            MessageBox.Show(this, _localization.Get("convert.noNkiInFolder"), _localization.Get("app.title"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AddFiles(files);

        // The folder is the most likely place of the samples, too
        if (string.IsNullOrWhiteSpace(SampleFolderTextBox.Text))
        {
            SampleFolderTextBox.Text = folder;
        }
    }

    private void FilesListBox_OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] dropped)
        {
            return;
        }

        foreach (var path in dropped)
        {
            if (Directory.Exists(path))
            {
                AddFiles(AbletonConverter.FindNkiFiles(path));
            }
            else
            {
                AddFiles([path]);
            }
        }
    }

    private void RemoveSelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in FilesListBox.SelectedItems.Cast<string>().ToList())
        {
            _nkiFiles.Remove(item);
        }
    }

    private void BrowseOutputFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var folder = SelectFolder(_localization.Get("convert.outputFolder"), OutputFolderTextBox.Text);
        if (folder is not null)
        {
            OutputFolderTextBox.Text = folder;
        }
    }

    private void BrowseSampleFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var folder = SelectFolder(_localization.Get("convert.sampleFolder"), SampleFolderTextBox.Text);
        if (folder is not null)
        {
            SampleFolderTextBox.Text = folder;
        }
    }

    private static string? SelectFolder(string description, string? current)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = !string.IsNullOrWhiteSpace(current) && Directory.Exists(current)
                ? current
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    private async void RunConvertButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (_nkiFiles.Count == 0)
        {
            MessageBox.Show(this, _localization.Get("convert.validation.inputMissing"), _localization.Get("app.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var outputFolder = OutputFolderTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            MessageBox.Show(this, _localization.Get("convert.validation.outputMissing"), _localization.Get("app.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var searchFolders = new List<string>();
        var sampleFolder = SampleFolderTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(sampleFolder))
        {
            if (!Directory.Exists(sampleFolder))
            {
                MessageBox.Show(this, _localization.Get("convert.validation.sampleFolderMissing"), _localization.Get("app.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            searchFolders.Add(sampleFolder);
        }

        var abletonVersion = Live11RadioButton.IsChecked == true ? 11 : 12;
        _cancellation = new CancellationTokenSource();

        try
        {
            SetBusy(true, _localization.Get("convert.status.running"));
            AppendLog($"[{DateTime.Now:HH:mm:ss}] {_localization.Get("convert.status.running")}");

            var summary = await AbletonExportService.ConvertAsync(
                _nkiFiles.ToList(),
                outputFolder,
                abletonVersion,
                searchFolders,
                line => Dispatcher.Invoke(() => AppendLog(line)),
                _cancellation.Token);

            StatusTextBlock.Text = summary.Failed == 0
                ? $"{_localization.Get("convert.status.completed")} Presets: {summary.Presets}"
                : $"{_localization.Get("convert.status.failed")} Presets: {summary.Presets}, Failed: {summary.Failed}";
            OpenOutputButton.IsEnabled = summary.Presets > 0;
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = _localization.Get("convert.status.cancelled");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            StatusTextBlock.Text = _localization.Get("convert.status.failed");
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            SetBusy(false, StatusTextBlock.Text);
        }
    }

    private void OpenOutputButton_OnClick(object sender, RoutedEventArgs e)
    {
        var folder = OutputFolderTextBox.Text.Trim();
        if (Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
    }

    private void CloseWindowButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ConvertWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isBusy)
        {
            // Stop after the current instrument instead of closing in the middle of writing files
            _cancellation?.Cancel();
            e.Cancel = true;
        }
    }

    private void SetBusy(bool isBusy, string status)
    {
        _isBusy = isBusy;
        AddFilesButton.IsEnabled = !isBusy;
        AddFolderButton.IsEnabled = !isBusy;
        RemoveSelectedButton.IsEnabled = !isBusy;
        BrowseOutputFolderButton.IsEnabled = !isBusy;
        BrowseSampleFolderButton.IsEnabled = !isBusy;
        OutputFolderTextBox.IsEnabled = !isBusy;
        SampleFolderTextBox.IsEnabled = !isBusy;
        Live11RadioButton.IsEnabled = !isBusy;
        Live12RadioButton.IsEnabled = !isBusy;
        FilesListBox.IsEnabled = !isBusy;
        RunConvertButton.IsEnabled = !isBusy;
        StatusTextBlock.Text = status;
    }

    private void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (!string.IsNullOrEmpty(LogTextBox.Text))
        {
            LogTextBox.AppendText(Environment.NewLine);
        }

        LogTextBox.AppendText(message);
        LogTextBox.ScrollToEnd();
    }
}
