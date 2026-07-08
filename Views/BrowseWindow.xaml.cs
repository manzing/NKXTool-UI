using Microsoft.Win32;
using NkxToolUI.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace NkxToolUI.Views;

public partial class BrowseWindow : Window
{
    private readonly NkxToolRunner _runner = new();
    private readonly LocalizationManager _localization = LocalizationManager.Current;
    private readonly ObservableCollection<ArchiveTreeNode> _rootNodes = [];
    private readonly ObservableCollection<BrowseGridItem> _visibleItems = [];
    private readonly string? _startupArchivePath;

    public BrowseWindow()
    {
        InitializeComponent();
        _startupArchivePath = null;
        InitializeUi();
    }

    public BrowseWindow(string archivePath)
    {
        InitializeComponent();
        _startupArchivePath = archivePath;
        InitializeUi();
    }

    private void InitializeUi()
    {
        ApplyTranslations();
        EntriesTreeView.ItemsSource = _rootNodes;
        EntriesDataGrid.ItemsSource = _visibleItems;
        StatusTextBlock.Text = _localization.Get("browse.status.ready");
        Loaded += BrowseWindow_Loaded;
    }

    private async void BrowseWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_startupArchivePath))
        {
            await LoadArchiveAsync(_startupArchivePath);
        }
    }

    private void ApplyTranslations()
    {
        Title = _localization.Get("browse.title");
        HeaderTextBlock.Text = _localization.Get("browse.header");
        DescriptionTextBlock.Text = _localization.Get("browse.description");
        ArchivePathLabel.Text = _localization.Get("browse.archivePath");
        BrowseArchiveButton.Content = _localization.Get("browse.selectArchive");
        RefreshButton.Content = _localization.Get("action.refresh");
        TreeGroupBox.Header = _localization.Get("browse.tree");
        DetailsGroupBox.Header = _localization.Get("browse.details");
        OutputLabel.Text = _localization.Get("browse.output");
        NameColumn.Header = _localization.Get("browse.column.name");
        TypeColumn.Header = _localization.Get("browse.column.type");
        PathColumn.Header = _localization.Get("browse.column.path");
        SizeColumn.Header = _localization.Get("browse.column.size");
    }

    private async void BrowseArchiveButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "NKX archive (*.nkx)|*.nkx|All files (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            await LoadArchiveAsync(dialog.FileName);
        }
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        var archivePath = ArchivePathTextBox.Text.Trim();

        if (!File.Exists(archivePath))
        {
            MessageBox.Show(
                this,
                _localization.Get("browse.validation.archiveMissing"),
                _localization.Get("app.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        await LoadArchiveAsync(archivePath);
    }

    public async Task LoadArchiveAsync(string archivePath)
    {
        ArchivePathTextBox.Text = archivePath;
        StatusTextBlock.Text = "Loading archive...";
        OutputTextBox.Text = string.Empty;
        _rootNodes.Clear();
        _visibleItems.Clear();

        var browseResult = await _runner.BrowseAsync(archivePath);

        BuildTree(archivePath, browseResult.Entries);
        ShowChildren(_rootNodes.FirstOrDefault());

        var rawOutput = string.IsNullOrWhiteSpace(browseResult.Execution.CombinedOutput)
            ? "No raw output returned by NkxTool.exe."
            : browseResult.Execution.CombinedOutput;

        if (browseResult.UsedMockData)
        {
            rawOutput += Environment.NewLine + Environment.NewLine +
                         "A temporary/mock listing is displayed because no parseable archive listing was returned.";
        }

        OutputTextBox.Text = rawOutput;
        StatusTextBlock.Text = browseResult.UsedMockData
            ? _localization.Get("browse.status.mock")
            : _localization.Get("browse.status.loaded");
    }

    private void BuildTree(string archivePath, IReadOnlyList<NkxArchiveEntry> entries)
    {
        var rootNode = new ArchiveTreeNode
        {
            Name = Path.GetFileName(archivePath),
            FullPath = string.Empty,
            IsDirectory = true
        };

        var lookup = new Dictionary<string, ArchiveTreeNode>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = rootNode
        };

        foreach (var entry in entries.OrderBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            var normalizedPath = entry.FullPath
                .Replace('/', '\\')
                .Trim('\\');

            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                continue;
            }

            var segments = normalizedPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var currentPath = string.Empty;
            var parent = rootNode;

            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                currentPath = string.IsNullOrEmpty(currentPath)
                    ? segment
                    : $"{currentPath}\\{segment}";

                var isLast = i == segments.Length - 1;
                var isDirectory = !isLast || entry.IsDirectory;

                if (!lookup.TryGetValue(currentPath, out var node))
                {
                    node = new ArchiveTreeNode
                    {
                        Name = segment,
                        FullPath = currentPath,
                        IsDirectory = isDirectory,
                        Size = isLast ? entry.Size : null
                    };

                    parent.Children.Add(node);
                    lookup[currentPath] = node;
                }

                parent = node;
            }
        }

        _rootNodes.Add(rootNode);
    }

    private void EntriesTreeView_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        ShowChildren(e.NewValue as ArchiveTreeNode);
    }

    private void ShowChildren(ArchiveTreeNode? node)
    {
        _visibleItems.Clear();

        if (node is null)
        {
            return;
        }

        var source = node.Children.Count > 0
            ? node.Children
            : [node];

        foreach (var child in source
                     .OrderByDescending(item => item.IsDirectory)
                     .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            _visibleItems.Add(new BrowseGridItem(child));
        }
    }
}

public sealed class ArchiveTreeNode
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long? Size { get; init; }
    public ObservableCollection<ArchiveTreeNode> Children { get; } = [];
}

public sealed class BrowseGridItem
{
    public BrowseGridItem(ArchiveTreeNode node)
    {
        Name = node.Name;
        ItemType = node.IsDirectory ? "Folder" : "File";
        FullPath = string.IsNullOrWhiteSpace(node.FullPath) ? "\\" : node.FullPath;
        SizeText = node.Size.HasValue ? node.Size.Value.ToString("N0") : string.Empty;
    }

    public string Name { get; }
    public string ItemType { get; }
    public string FullPath { get; }
    public string SizeText { get; }
}