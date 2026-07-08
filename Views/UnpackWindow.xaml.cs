using NkxToolUI.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace NkxToolUI.Views;

public partial class UnpackWindow : Window
{
    private readonly NkxToolRunner _runner = new();
    private readonly LocalizationManager _localization = LocalizationManager.Current;
    private readonly ObservableCollection<string> _archiveFiles = [];
    private bool _isBusy;

    public UnpackWindow()
    {
        InitializeComponent();
        ArchivesListBox.ItemsSource = _archiveFiles;
        ApplyTranslations();
        StatusTextBlock.Text = _localization.Get("unpack.status.ready");
    }

    private void ApplyTranslations()
    {
        Title = _localization.Get("unpack.title");
        HeaderTextBlock.Text = _localization.Get("unpack.header");
        DescriptionTextBlock.Text = _localization.Get("unpack.description");
        InputArchivesLabel.Text = _localization.Get("unpack.inputArchives");
        AddArchivesButton.Content = _localization.Get("unpack.addArchives");
        RemoveSelectedButton.Content = _localization.Get("unpack.removeSelected");
        OutputFolderLabel.Text = _localization.Get("unpack.outputFolder");
        BrowseOutputFolderButton.Content = _localization.Get("unpack.selectOutputFolder");
        CreateSubfoldersCheckBox.Content = _localization.Get("unpack.createSubfolders");
        RunUnpackButton.Content = _localization.Get("unpack.run");
        CloseWindowButton.Content = _localization.Get("action.close");
        LogLabel.Text = _localization.Get("unpack.log");
    }

    private void AddArchivesButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "NKX archive (*.nkx)|*.nkx|All files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        foreach (var file in dialog.FileNames)
        {
            if (!_archiveFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
            {
                _archiveFiles.Add(file);
            }
        }
    }

    private void RemoveSelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        var selectedItems = ArchivesListBox.SelectedItems
            .Cast<string>()
            .ToList();

        foreach (var item in selectedItems)
        {
            _archiveFiles.Remove(item);
        }
    }

    private void BrowseOutputFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = _localization.Get("unpack.outputFolder"),
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(OutputFolderTextBox.Text)
                ? OutputFolderTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            OutputFolderTextBox.Text = dialog.SelectedPath;
        }
    }

    private async void RunUnpackButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (_archiveFiles.Count == 0)
        {
            MessageBox.Show(
                this,
                _localization.Get("unpack.validation.archivesMissing"),
                _localization.Get("app.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var outputRoot = OutputFolderTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            MessageBox.Show(
                this,
                _localization.Get("unpack.validation.outputMissing"),
                _localization.Get("app.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Directory.CreateDirectory(outputRoot);

        var createSubfolders = CreateSubfoldersCheckBox.IsChecked == true;
        var successCount = 0;
        var failureCount = 0;

        try
        {
            SetBusy(true, _localization.Get("unpack.status.running"));
            AppendLog($"[{DateTime.Now:HH:mm:ss}] Starting unpack operation.");
            AppendLog($"Output root: {outputRoot}");
            AppendLog($"Subfolders: {createSubfolders}");

            foreach (var archiveFile in _archiveFiles)
            {
                var destination = createSubfolders
                    ? Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(archiveFile))
                    : outputRoot;

                Directory.CreateDirectory(destination);

                AppendLog($"[{DateTime.Now:HH:mm:ss}] Extracting: {archiveFile}");
                AppendLog($"Destination: {destination}");

                var result = await _runner.UnpackAsync(archiveFile, destination);
                AppendResult(result);

                if (result.Success)
                {
                    successCount++;
                }
                else
                {
                    failureCount++;
                }
            }

            StatusTextBlock.Text = failureCount == 0
                ? $"{_localization.Get("unpack.status.completed")} Success: {successCount}"
                : $"{_localization.Get("unpack.status.failed")} Success: {successCount}, Failed: {failureCount}";
        }
        finally
        {
            SetBusy(false, StatusTextBlock.Text);
        }
    }

    private void CloseWindowButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SetBusy(bool isBusy, string status)
    {
        _isBusy = isBusy;
        AddArchivesButton.IsEnabled = !isBusy;
        RemoveSelectedButton.IsEnabled = !isBusy;
        BrowseOutputFolderButton.IsEnabled = !isBusy;
        RunUnpackButton.IsEnabled = !isBusy;
        CloseWindowButton.IsEnabled = !isBusy;
        OutputFolderTextBox.IsEnabled = !isBusy;
        CreateSubfoldersCheckBox.IsEnabled = !isBusy;
        ArchivesListBox.IsEnabled = !isBusy;
        StatusTextBlock.Text = status;
    }

    private void AppendResult(ProcessExecutionResult result)
    {
        var buffer = new StringBuilder();

        buffer.AppendLine($"Command: {result.CommandLine}");

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            buffer.AppendLine("STDOUT:");
            buffer.AppendLine(result.StandardOutput.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            buffer.AppendLine("STDERR:");
            buffer.AppendLine(result.StandardError.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(result.ExceptionMessage))
        {
            buffer.AppendLine("ERROR:");
            buffer.AppendLine(result.ExceptionMessage.TrimEnd());
        }

        buffer.AppendLine(result.Success
            ? "Process finished successfully."
            : $"Process finished with failure. ExitCode={result.ExitCode?.ToString() ?? "n/a"}");

        AppendLog(buffer.ToString().TrimEnd());
    }

    private void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(LogTextBox.Text))
        {
            LogTextBox.AppendText(Environment.NewLine + Environment.NewLine);
        }

        LogTextBox.AppendText(message);
        LogTextBox.ScrollToEnd();
    }
}