using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.UI.Controls;

namespace SubathonManager.UI.Views;

public partial class ExportOverlayDialog : Window
{
    private readonly Route? _route;
    private readonly ILogger? _logger = AppServices.Provider.GetService<ILogger<ExportOverlayDialog>>();

    private readonly List<FileEntry> _allEntries = new();
    private bool _suppressSelectAllSync;

    public ExportOverlayDialog()
    {
        InitializeComponent();
    }

    public ExportOverlayDialog(Route route)
    {
        var factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        using var db = factory.CreateDbContext();
        _route = db.Routes.AsNoTracking()
            .Include(r => r.Widgets).ThenInclude(w => w.CssVariables)
            .Include(r => r.Widgets).ThenInclude(w => w.JsVariables)
            .FirstOrDefault(r => r.Id == route.Id);

        InitializeComponent();

        if (_route == null)
        {
            Opened += (_, _) => Close();
            return;
        }

        ExportNameBox.Text = $"{_route.Name} Export";
        var fileList = BuildFileList(_route);
        PopulateTree(fileList);
        bool isDevOrBeta = AppServices.AppVersion.Contains('+');
        AppOverrideSection.IsVisible = isDevOrBeta;
        AppVersionBox.Text = $"{AppServices.AppVersion}";
    }

    private static List<(string zipEntry, string? absSource)> BuildFileList(Route route)
    {
        var result = new List<(string, string?)>();

        var widgets = route.Widgets.ToList();
        var widgetRoots = widgets.Select(w => w.GetPath()).ToList();
        var zipRoots = OverlayPorter.GetZipWidgetRoots(widgetRoots);

        for (int wi = 0; wi < widgets.Count; wi++)
        {
            var widget = widgets[wi];
            string widgetRoot = widgetRoots[wi];
            string zipWidgetRoot = zipRoots[wi];

            if (widget.Type.IsAsset())
            {
                if (File.Exists(widget.HtmlPath))
                {
                    string fileName = Path.GetFileName(widget.HtmlPath);
                    result.Add(($"{zipWidgetRoot}/{fileName}", widget.HtmlPath));
                }
            }
            else if (Directory.Exists(widgetRoot))
            {
                result.AddRange(from file in Directory.EnumerateFiles(widgetRoot, "*", SearchOption.AllDirectories)
                    let relative = Path.GetRelativePath(widgetRoot, file).Replace('\\', '/')
                    select ($"{zipWidgetRoot}/{relative}", file));
            }

            foreach (var jsVar in widget.JsVariables)
            {
                if (!((WidgetVariableType?)jsVar.Type).IsFileVariable()) continue;
                if (string.IsNullOrWhiteSpace(jsVar.Value)) continue;
                bool isAbsolute = !jsVar.Value.StartsWith("./") && !jsVar.Value.StartsWith("../")
                                  && Path.IsPathRooted(jsVar.Value);
                if (!isAbsolute) continue;
                bool isFolderType = jsVar.Type == WidgetVariableType.FolderPath;
                if (isFolderType && Directory.Exists(jsVar.Value))
                {
                    result.AddRange(from file in Directory.EnumerateFiles(jsVar.Value, "*", SearchOption.AllDirectories)
                        let rel = Path.GetRelativePath(jsVar.Value, file).Replace('\\', '/')
                        select ($"{zipWidgetRoot}/_external/{jsVar.Name}/{rel}", file));
                }
                else if (!isFolderType && File.Exists(jsVar.Value))
                {
                    result.Add(($"{zipWidgetRoot}/_external/{Path.GetFileName(jsVar.Value)}", jsVar.Value));
                }
            }
        }

        result.Add(("overlay.json", null));
        return result;
    }

    private void PopulateTree(List<(string zipEntry, string? absSource)> files)
    {
        var root = new TreeNode("root");

        foreach (var (zipEntry, absSource) in files)
        {
            var parts = zipEntry.Split('/');
            var node = root;
            for (int i = 0; i < parts.Length; i++)
            {
                bool isLeaf = i == parts.Length - 1;
                if (!node.Children.TryGetValue(parts[i], out var child))
                {
                    child = new TreeNode(parts[i])
                    {
                        AbsSource = isLeaf ? absSource : null,
                        IsLeaf = isLeaf,
                        ZipEntry = isLeaf ? zipEntry : null
                    };
                    node.Children[parts[i]] = child;
                }
                node = child;
            }
        }

        foreach (var child in root.Children.Values)
            FileTree.Items.Add(BuildTreeItem(child));
    }

    private TreeViewItem BuildTreeItem(TreeNode node)
    {
        bool isLeaf = node.Children.Count == 0;
        bool isGenerated = isLeaf && node.AbsSource == null;

        var checkBox = new CheckBox
        {
            IsChecked = true,
            Margin = new global::Avalonia.Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !isGenerated
        };

        var icon = new SymIcon
        {
            Glyph = isLeaf ? "Document24" : "Folder24",
            Margin = new global::Avalonia.Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14
        };

        var label = new TextBlock
        {
            Text = node.Name,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = isGenerated
                ? new SolidColorBrush(Color.FromArgb(180, 160, 160, 160))
                : PrimaryTextBrush()
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(checkBox);
        header.Children.Add(icon);
        header.Children.Add(label);

        var item = new TreeViewItem
        {
            Header = header,
            IsExpanded = true,
            Padding = new global::Avalonia.Thickness(2)
        };

        if (isLeaf)
        {
            var entry = new FileEntry(node.ZipEntry ?? node.Name, node.AbsSource, checkBox, icon, label);
            _allEntries.Add(entry);

            checkBox.IsCheckedChanged += (_, _) => OnEntryCheckedChanged(entry, checkBox.IsChecked ?? false);
        }
        else
        {
            foreach (var child in node.Children.Values)
                item.Items.Add(BuildTreeItem(child));

            checkBox.IsCheckedChanged += (_, _) => SetDescendantLeaves(item, checkBox.IsChecked ?? false);
        }

        return item;
    }

    private void OnEntryCheckedChanged(FileEntry entry, bool isChecked)
    {
        entry.IsIncluded = isChecked;
        ApplyEntryStyle(entry, isChecked);
        SyncSelectAllBox();
    }

    private void ApplyEntryStyle(FileEntry entry, bool isChecked)
    {
        if (isChecked)
        {
            entry.Label.Foreground = PrimaryTextBrush();
            entry.Label.Opacity = 1.0;
            entry.Icon.Opacity = 1.0;
        }
        else
        {
            entry.Label.Foreground = new SolidColorBrush(Color.FromArgb(200, 180, 60, 60));
            entry.Label.Opacity = 0.75;
            entry.Icon.Opacity = 0.4;
        }
    }

    private void SetDescendantLeaves(TreeViewItem parent, bool isChecked)
    {
        foreach (var obj in parent.Items)
        {
            if (obj is not TreeViewItem child) continue;
            if (child.Header is StackPanel sp)
            {
                var cb = sp.Children.OfType<CheckBox>().FirstOrDefault();
                if (cb is { IsEnabled: true })
                    cb.IsChecked = isChecked;
            }
            SetDescendantLeaves(child, isChecked);
        }
    }

    private void SyncSelectAllBox()
    {
        _suppressSelectAllSync = true;
        var checkable = _allEntries.Where(e => e.CheckBox.IsEnabled).ToList();
        bool allOn = checkable.All(e => e.IsIncluded);
        bool allOff = checkable.All(e => !e.IsIncluded);
        SelectAllBox.IsChecked = allOn ? true : allOff ? false : null;
        _suppressSelectAllSync = false;
    }

    private void SelectAllBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressSelectAllSync) return;
        bool? state = SelectAllBox.IsChecked;
        if (state is null) return;
        foreach (var entry in _allEntries.Where(e2 => e2.CheckBox.IsEnabled))
            entry.CheckBox.IsChecked = state.Value;
    }

    private async void ConfirmButton_Click(object? sender, RoutedEventArgs e)
    {
        string exportName = ExportNameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(exportName))
            exportName = _route!.Name;

        string appVersion = AppVersionBox.Text?.Trim() ?? string.Empty;
        string version = VersionBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(version)) version = "1";

        string safeFileName = string.Concat(exportName.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string exportsDir = Path.GetFullPath("./exports");
        Directory.CreateDirectory(exportsDir);

        var startFolder = await StorageProvider.TryGetFolderFromPathAsync(exportsDir);
        var picked = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Overlay Export",
            SuggestedFileName = safeFileName,
            DefaultExtension = "smo",
            SuggestedStartLocation = startFolder,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Subathon Manager Overlay (*.smo)") { Patterns = new[] { "*.smo" } }
            }
        });

        if (picked == null) return;
        string outputPath = picked.Path.LocalPath;

        ConfirmButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        ConfirmButton.Content = "Exporting...";

        try
        {
            if (_route == null)
            {
                await ShowError("Could not load route data for export.");
                return;
            }

            var excludedZipEntries = _allEntries
                .Where(entry => entry is { IsIncluded: false, AbsSource: not null })
                .Select(entry => entry.ZipEntry)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            await OverlayPorter.ExportRouteAsync(_route, outputPath, exportName, excludedZipEntries, version, appVersion);
            Close();
        }
        catch (Exception ex)
        {
            await ShowError($"Export failed: {ex.Message}");
            _logger?.LogError(ex, "Export failed");
        }
        finally
        {
            ConfirmButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            ConfirmButton.Content = "Export...";
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close();

    private async Task ShowError(string message)
    {
        var box = new FluentAvalonia.UI.Controls.FAContentDialog
        {
            Title = "Export Error",
            Content = message,
            CloseButtonText = "OK"
        };
        await box.ShowAsync();
    }

    private IBrush PrimaryTextBrush()
        => this.TryFindResource("TextFillColorPrimaryBrush", this.ActualThemeVariant, out var b) && b is IBrush brush
            ? brush
            : Brushes.Gray;

    private class TreeNode(string name)
    {
        public string Name { get; } = name;
        public string? AbsSource { get; set; }
        public bool IsLeaf { get; set; }
        public string? ZipEntry { get; set; }
        public Dictionary<string, TreeNode> Children { get; } = new();
    }

    private class FileEntry(string zipEntry, string? absSource, CheckBox checkBox, SymIcon icon, TextBlock label)
    {
        public string ZipEntry { get; } = zipEntry;
        public string? AbsSource { get; } = absSource;
        public CheckBox CheckBox { get; } = checkBox;
        public SymIcon Icon { get; } = icon;
        public TextBlock Label { get; } = label;
        public bool IsIncluded { get; set; } = true;
    }
}
