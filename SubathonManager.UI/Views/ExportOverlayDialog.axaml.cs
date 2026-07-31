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
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Data.Overlays;
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
        VersionBox.Text = "1.0.0";
        var fileList = BuildFileList(_route);
        PopulateTree(fileList);
        SyncSelectAllBox();
        bool isDevOrBeta = AppServices.AppVersion.Contains('+');
        AppOverrideSection.IsVisible = isDevOrBeta;
        AppVersionBox.Text = $"{AppServices.AppVersion}";
    }

    private sealed record PreviewEntry(
        string ZipEntry,
        string? AbsSource,
        bool DefaultSelected = true,
        bool InUse = false,
        string? UsageHint = null);

    private static List<PreviewEntry> BuildFileList(Route route)
    {
        var result = new List<PreviewEntry>();
        var resourceUsage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string zipEntry, string? absSource) => result.Add(new PreviewEntry(zipEntry, absSource));

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
                if (WidgetFiles.Current.Exists(widget.HtmlPath))
                {
                    string fileName = Path.GetFileName(widget.HtmlPath);
                    Add($"{zipWidgetRoot}/{fileName}", widget.HtmlPath);
                }
            }
            else
            {
                foreach (var file in WidgetFiles.Current.EnumerateFiles(widgetRoot))
                    Add($"{zipWidgetRoot}/{Path.GetRelativePath(widgetRoot, file).Replace('\\', '/')}", file);
            }

            foreach (var jsVar in widget.JsVariables)
            {
                if (!((WidgetVariableType?)jsVar.Type).IsFileVariable()) continue;
                if (string.IsNullOrWhiteSpace(jsVar.Value)) continue;

                if (ResourcePaths.RelativeFromUrl(jsVar.Value) is { } resourceRel)
                {
                    resourceUsage[resourceRel] = $"Used by \"{widget.Name}\" - variable \"{jsVar.Name}\"";
                    continue;
                }

                bool isAbsolute = !jsVar.Value.StartsWith("./") && !jsVar.Value.StartsWith("../")
                                  && Path.IsPathRooted(jsVar.Value);
                if (!isAbsolute) continue;
                bool isFolderType = jsVar.Type == WidgetVariableType.FolderPath;
                if (isFolderType && Directory.Exists(jsVar.Value))
                {
                    foreach (var file in Directory.EnumerateFiles(jsVar.Value, "*", SearchOption.AllDirectories))
                        Add($"{zipWidgetRoot}/_external/{jsVar.Name}/" +
                            $"{Path.GetRelativePath(jsVar.Value, file).Replace('\\', '/')}", file);
                }
                else if (!isFolderType && File.Exists(jsVar.Value))
                {
                    Add($"{zipWidgetRoot}/_external/{Path.GetFileName(jsVar.Value)}", jsVar.Value);
                }
            }
        }

        foreach (var rel in ResourcePaths.EnumerateRelative())
        {
            bool inUse = resourceUsage.TryGetValue(rel, out var hint);
            result.Add(new PreviewEntry(
                $"{OverlayPorter.ResourcesFolder}/{rel}",
                ResourcePaths.ToLocalPath(ResourcePaths.UrlPrefix + rel),
                DefaultSelected: false,
                InUse: inUse,
                UsageHint: hint));
        }

        Add("overlay.json", null);
        return result;
    }

    private void PopulateTree(List<PreviewEntry> files)
    {
        var root = new TreeNode("root");

        foreach (var file in files)
        {
            var parts = file.ZipEntry.Split('/');
            var node = root;
            for (int i = 0; i < parts.Length; i++)
            {
                bool isLeaf = i == parts.Length - 1;
                if (!node.Children.TryGetValue(parts[i], out var child))
                {
                    child = new TreeNode(parts[i])
                    {
                        AbsSource = isLeaf ? file.AbsSource : null,
                        IsLeaf = isLeaf,
                        ZipEntry = isLeaf ? file.ZipEntry : null,
                        ZipPath = string.Join('/', parts.Take(i + 1)),
                        DefaultSelected = !isLeaf || file.DefaultSelected,
                        InUse = isLeaf && file.InUse,
                        UsageHint = isLeaf ? file.UsageHint : null
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
        bool isSharedResources = IsSharedResourceNode(node);

        var checkBox = new CheckBox
        {
            IsChecked = node.DefaultSelected,
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
            IsExpanded = !isSharedResources,
            Padding = new global::Avalonia.Thickness(2)
        };

        if (isLeaf)
        {
            var entry = new FileEntry(node.ZipEntry ?? node.Name, node.AbsSource, checkBox, icon, label)
            {
                IsIncluded = node.DefaultSelected,
                InUse = node.InUse
            };
            _allEntries.Add(entry);

            if (node.UsageHint != null) ToolTip.SetTip(label, node.UsageHint);
            ApplyEntryStyle(entry, entry.IsIncluded);

            checkBox.IsCheckedChanged += (_, _) => OnEntryCheckedChanged(entry, checkBox.IsChecked ?? false);
        }
        else
        {
            foreach (var child in node.Children.Values)
                item.Items.Add(BuildTreeItem(child));

            checkBox.IsChecked = DescendantState(item);
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

    private static readonly SolidColorBrush InUseBrush = new(Color.FromRgb(230, 170, 60));

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
            entry.Label.Foreground = entry.InUse
                ? InUseBrush
                : new SolidColorBrush(Color.FromArgb(200, 180, 60, 60));
            entry.Label.Opacity = entry.InUse ? 1.0 : 0.75;
            entry.Icon.Opacity = entry.InUse ? 0.8 : 0.4;
        }
    }

    private static bool IsSharedResourceNode(TreeNode node)
        => node.ZipPath.Equals(OverlayPorter.ResourcesFolder, StringComparison.OrdinalIgnoreCase);

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

    private static bool? DescendantState(TreeViewItem parent)
    {
        var boxes = new List<CheckBox>();
        CollectLeafBoxes(parent, boxes);
        if (boxes.Count == 0) return false;
        if (boxes.All(b => b.IsChecked == true)) return true;
        if (boxes.All(b => b.IsChecked != true)) return false;
        return null;
    }

    private static void CollectLeafBoxes(TreeViewItem parent, List<CheckBox> into)
    {
        foreach (var obj in parent.Items)
        {
            if (obj is not TreeViewItem child) continue;
            if (child.Items.Count == 0)
            {
                if (child.Header is StackPanel sp && sp.Children.OfType<CheckBox>().FirstOrDefault() is { } cb)
                    into.Add(cb);
            }
            else
            {
                CollectLeafBoxes(child, into);
            }
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

        string safeFileName = Path.GetFileNameWithoutExtension(
            OverlayPackPaths.BuildFileName(AuthorBox.Text?.Trim() ?? string.Empty, exportName, version));
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

            await OverlayPorter.ExportRouteAsync(_route, outputPath, exportName, excludedZipEntries, version, appVersion,
                AuthorBox.Text?.Trim() ?? string.Empty, WidgetPorter.ParseTags(TagsBox.Text));
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
        public string ZipPath { get; init; } = string.Empty;
        public bool DefaultSelected { get; init; } = true;
        public bool InUse { get; init; }
        public string? UsageHint { get; init; }
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
        public bool InUse { get; init; }
    }
}
