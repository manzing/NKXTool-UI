using NkxToolUI.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace NkxToolUI.Views;

public partial class PackWindow : Window
{
    private readonly NkxToolRunner _runner = new();
    private readonly LocalizationManager _localization = LocalizationManager.Current;
    private readonly ObservableCollection<string> _sourceFolders = [];
    private bool _isBusy;

    public PackWindow()
    {
        InitializeComponent();
        SourceFoldersListBox.ItemsSource = _sourceFolders;
        ApplyTranslations();
        FolderModeRadioButton.IsChecked = true;
        StatusTextBlock.Text = _localization.Get("pack.status.ready");
        UpdatePackModeUi();
    }

    private void ApplyTranslations()
    {
        Title = _localization.Get("pack.title");
        HeaderTextBlock.Text = _localization.Get("pack.header");
        DescriptionTextBlock.Text = _localization.Get("pack.description");
        ModeGroupBox.Header = _localization.Get("pack.mode");
        FolderModeRadioButton.Content = _localization.Get("pack.mode.folder");
        FileListModeRadioButton.Content = _localization.Get("pack.mode.fileList");

        SourceFolderLabel.Text = _localization.Get("pack.sourceFolder");
        FileListLabel.Text = _localization.Get("pack.fileList");
        RootPathLabel.Text = _localization.Get("pack.rootPath");
        OutputArchiveLabel.Text = _localization.Get("pack.outputArchive");

        BrowseSourceFolderButton.Content = _localization.Get("pack.selectSourceFolder");
        BrowseFileListButton.Content = _localization.Get("pack.selectFileList");
        BrowseRootPathButton.Content = _localization.Get("pack.selectSourceFolder");
        BrowseOutputArchiveButton.Content = _localization.Get("pack.selectOutputArchive");

        RunPackButton.Content = _localization.Get("pack.run");
        CloseWindowButton.Content = _localization.Get("action.close");
        HintTextBlock.Text = _localization.Get("pack.hint");
    }

    private bool IsFileListMode => FileListModeRadioButton.IsChecked == true;
    private bool IsMultiFolderMode => MultiFolderModeRadioButton.IsChecked == true;

    private void PackModeRadioButton_OnChecked(object sender, RoutedEventArgs e)
    {
        UpdatePackModeUi();
    }

    private void UpdatePackModeUi()
    {
        var fileListMode = IsFileListMode;
        var multiFolderMode = IsMultiFolderMode;

        SinglePackPanel.Visibility = multiFolderMode ? Visibility.Collapsed : Visibility.Visible;
        MultiPackPanel.Visibility = multiFolderMode ? Visibility.Visible : Visibility.Collapsed;

        SourceFolderTextBox.IsEnabled = !fileListMode && !multiFolderMode && !_isBusy;
        BrowseSourceFolderButton.IsEnabled = !fileListMode && !multiFolderMode && !_isBusy;

        FileListTextBox.IsEnabled = fileListMode && !_isBusy;
        BrowseFileListButton.IsEnabled = fileListMode && !_isBusy;

        RootPathTextBox.IsEnabled = fileListMode && !_isBusy;
        BrowseRootPathButton.IsEnabled = fileListMode && !_isBusy;

        OutputArchiveTextBox.IsEnabled = !multiFolderMode && !_isBusy;
        BrowseOutputArchiveButton.IsEnabled = !multiFolderMode && !_isBusy;

        SourceFoldersListBox.IsEnabled = multiFolderMode && !_isBusy;
        AddSourceFoldersButton.IsEnabled = multiFolderMode && !_isBusy;
        RemoveSourceFoldersButton.IsEnabled = multiFolderMode && !_isBusy;
        OutputFolderTextBox.IsEnabled = multiFolderMode && !_isBusy;
        BrowseOutputFolderButton.IsEnabled = multiFolderMode && !_isBusy;
        IncludeParentFolderCheckBox.IsEnabled = multiFolderMode && !_isBusy;
    }

    private void BrowseSourceFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = _localization.Get("pack.sourceFolder"),
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(SourceFolderTextBox.Text)
                ? SourceFolderTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            SourceFolderTextBox.Text = dialog.SelectedPath;
        }
    }

    private void BrowseFileListButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            FileListTextBox.Text = dialog.FileName;
        }
    }

    private void BrowseRootPathButton_OnClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = _localization.Get("pack.rootPath"),
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(RootPathTextBox.Text)
                ? RootPathTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            RootPathTextBox.Text = dialog.SelectedPath;
        }
    }

    private void BrowseOutputArchiveButton_OnClick(object sender, RoutedEventArgs e)
    {
        var initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var dialog = new SaveFileDialog
        {
            Filter = "NKX archive (*.nkx)|*.nkx|All files (*.*)|*.*",
            DefaultExt = ".nkx",
            AddExtension = true,
            InitialDirectory = initialDirectory,
            FileName = "archive.nkx"
        };

        if (dialog.ShowDialog(this) == true)
        {
            OutputArchiveTextBox.Text = dialog.FileName;
        }
    }

    private void AddSourceFoldersButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Multiselect = true,
            Title = "Select source folders"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        foreach (var folder in dialog.FolderNames)
        {
            if (!_sourceFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                _sourceFolders.Add(folder);
            }
        }
    }

    private void RemoveSourceFoldersButton_OnClick(object sender, RoutedEventArgs e)
    {
        var selected = SourceFoldersListBox.SelectedItems.Cast<string>().ToList();

        foreach (var item in selected)
        {
            _sourceFolders.Remove(item);
        }
    }

    private void BrowseOutputFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Multiselect = false,
            Title = "Select output folder"
        };

        if (dialog.ShowDialog(this) == true)
        {
            OutputFolderTextBox.Text = dialog.FolderName;
        }
    }

    private async void RunPackButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (IsMultiFolderMode)
        {
            await RunMultiFolderPackAsync();
            return;
        }

        await RunSinglePackAsync();
    }

    private async Task RunSinglePackAsync()
    {
        var outputArchive = OutputArchiveTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(outputArchive))
        {
            MessageBox.Show(
                this,
                _localization.Get("pack.validation.outputMissing"),
                _localization.Get("app.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string packSource;
        string? rootPath = null;

        if (IsFileListMode)
        {
            var fileListPath = FileListTextBox.Text.Trim();

            if (!File.Exists(fileListPath))
            {
                MessageBox.Show(
                    this,
                    _localization.Get("pack.validation.fileListMissing"),
                    _localization.Get("app.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            packSource = "@" + fileListPath;
            rootPath = string.IsNullOrWhiteSpace(RootPathTextBox.Text)
                ? null
                : RootPathTextBox.Text.Trim();
        }
        else
        {
            var sourceFolder = SourceFolderTextBox.Text.Trim();

            if (!Directory.Exists(sourceFolder))
            {
                MessageBox.Show(
                    this,
                    _localization.Get("pack.validation.sourceMissing"),
                    _localization.Get("app.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            packSource = sourceFolder;
        }

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputArchive));
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        try
        {
            SetBusy(true, _localization.Get("pack.status.running"));
            AppendLog($"[{DateTime.Now:HH:mm:ss}] Starting pack operation.");
            AppendLog($"Output: {outputArchive}");
            AppendLog($"Source: {packSource}");
            AppendLog($"RootPath: {(string.IsNullOrWhiteSpace(rootPath) ? "<none>" : rootPath)}");

            var result = await _runner.PackAsync(outputArchive, packSource, rootPath);
            AppendResult(result);

            StatusTextBlock.Text = result.Success
                ? _localization.Get("pack.status.completed")
                : _localization.Get("pack.status.failed");
        }
        finally
        {
            SetBusy(false, StatusTextBlock.Text);
        }
    }

    private async Task RunMultiFolderPackAsync()
    {
        if (_sourceFolders.Count == 0)
        {
            MessageBox.Show(
                this,
                "Please add at least one source folder.",
                _localization.Get("app.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var outputFolder = OutputFolderTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            MessageBox.Show(
                this,
                "Please select an output folder.",
                _localization.Get("app.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Directory.CreateDirectory(outputFolder);

        var includeParentFolder = IncludeParentFolderCheckBox.IsChecked == true;
        var successCount = 0;
        var failureCount = 0;

        try
        {
            SetBusy(true, _localization.Get("pack.status.running"));
            AppendLog($"[{DateTime.Now:HH:mm:ss}] Starting multi-pack operation.");
            AppendLog($"Output folder: {outputFolder}");
            AppendLog($"Include parent folder: {includeParentFolder}");
            AppendLog($"Folders count: {_sourceFolders.Count}");

            foreach (var sourceFolder in _sourceFolders)
            {
                if (!Directory.Exists(sourceFolder))
                {
                    AppendLog($"Skipping missing folder: {sourceFolder}");
                    failureCount++;
                    continue;
                }

                var outputArchive = BuildArchivePath(outputFolder, sourceFolder);

                AppendLog($"[{DateTime.Now:HH:mm:ss}] Packing folder: {sourceFolder}");
                AppendLog($"Output archive: {outputArchive}");

                ProcessExecutionResult result;
                string? tempFileList = null;

                try
                {
                    if (includeParentFolder)
                    {
                        result = await _runner.PackAsync(outputArchive, sourceFolder);
                    }
                    else
                    {
                        tempFileList = CreateTempFileList(sourceFolder);
                        result = await _runner.PackAsync(outputArchive, "@" + tempFileList, sourceFolder);
                    }
                }
                finally
                {
                    if (!string.IsNullOrWhiteSpace(tempFileList) && File.Exists(tempFileList))
                    {
                        try
                        {
                            File.Delete(tempFileList);
                        }
                        catch
                        {
                        }
                    }
                }

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
                ? $"{_localization.Get("pack.status.completed")} Success: {successCount}"
                : $"{_localization.Get("pack.status.failed")} Success: {successCount}, Failed: {failureCount}";
        }
        finally
        {
            SetBusy(false, StatusTextBlock.Text);
        }
    }

    private static string BuildArchivePath(string outputFolder, string sourceFolder)
    {
        var folderName = Path.GetFileName(sourceFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Path.Combine(outputFolder, folderName + ".nkx");
    }

    private static string CreateTempFileList(string sourceFolder)
    {
        var tempFile = Path.Combine(
            Path.GetTempPath(),
            $"nkxtoolui-pack-{Guid.NewGuid():N}.txt");

        var files = Directory
            .GetFiles(sourceFolder, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(sourceFolder, file))
            .ToArray();

        File.WriteAllLines(tempFile, files, Encoding.UTF8);
        return tempFile;
    }

    private void CloseWindowButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SetBusy(bool isBusy, string status)
    {
        _isBusy = isBusy;
        FolderModeRadioButton.IsEnabled = !isBusy;
        MultiFolderModeRadioButton.IsEnabled = !isBusy;
        FileListModeRadioButton.IsEnabled = !isBusy;
        RunPackButton.IsEnabled = !isBusy;
        CloseWindowButton.IsEnabled = !isBusy;
        UpdatePackModeUi();
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