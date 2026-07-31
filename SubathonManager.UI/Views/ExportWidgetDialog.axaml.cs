using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Data.Widgets;
using SubathonManager.UI.Controls;
using SubathonManager.UI.UiUtils;

namespace SubathonManager.UI.Views;

public partial class ExportWidgetDialog : Window
{
    private readonly Widget? _widget;
    private readonly WidgetPorter.ExportPlan? _plan;
    private readonly ILogger? _logger = AppServices.Provider.GetService<ILogger<ExportWidgetDialog>>();

    private readonly List<FileEntry> _allEntries = new();
    private bool _suppressSelectAllSync;

    public ExportWidgetDialog()
    {
        InitializeComponent();
    }

    public ExportWidgetDialog(Widget widget)
    {
        var factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        using var db = factory.CreateDbContext();
        _widget = db.Widgets.AsNoTracking()
            .Include(w => w.CssVariables)
            .Include(w => w.JsVariables)
            .FirstOrDefault(w => w.Id == widget.Id);

        InitializeComponent();

        if (_widget == null)
        {
            Opened += (_, _) => Close();
            return;
        }

        _plan = WidgetPorter.BuildPlan(_widget);

        WidgetNameBox.Text = _widget.Name;
        AuthorBox.Text = WidgetPorter.ReadExistingMeta(_widget).Author;
        VersionBox.Text = "1.0.0";

        if (WidgetPackPaths.TryResolve(_widget.HtmlPath, out var packFile, out _, out _, out _) &&
            WidgetPackInstaller.ReadManifest(packFile) is { } manifest)
        {
            if (!string.IsNullOrWhiteSpace(manifest.Author)) AuthorBox.Text = manifest.Author;
            if (!string.IsNullOrWhiteSpace(manifest.Version)) VersionBox.Text = manifest.Version;
            if (manifest.Tags.Count > 0) TagsBox.Text = string.Join(", ", manifest.Tags);
            GroupBox.Text = manifest.Group;
            SetPreview(WidgetPorter.ExtractExistingPreview(_widget));
        }

        bool isDevOrBeta = AppServices.AppVersion.Contains('+');
        AppOverrideSection.IsVisible = isDevOrBeta;
        AppVersionBox.Text = AppServices.AppVersion;

        PopulateTree(_plan);
        SyncSelectAllBox();
        UpdateOutputPathText();
        TryEnableDragDrop();
    }

    #region TREE

    private void PopulateTree(WidgetPorter.ExportPlan plan)
    {
        var root = new TreeNode("root");

        foreach (var entry in plan.Entries)
        {
            var parts = entry.ZipEntry.Split('/');
            var node = root;
            for (int i = 0; i < parts.Length; i++)
            {
                bool isLeaf = i == parts.Length - 1;
                if (!node.Children.TryGetValue(parts[i], out var child))
                {
                    child = new TreeNode(parts[i])
                    {
                        Entry = isLeaf ? entry : null,
                        ZipPath = string.Join('/', parts.Take(i + 1))
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

        var checkBox = new CheckBox
        {
            Margin = new global::Avalonia.Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
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
            FontSize = 12
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(checkBox);
        header.Children.Add(icon);
        header.Children.Add(label);

        var item = new TreeViewItem
        {
            Header = header,
            IsExpanded = !IsSharedResourceNode(node),
            Padding = new global::Avalonia.Thickness(2)
        };

        if (isLeaf && node.Entry != null)
        {
            var entry = new FileEntry(node.Entry, checkBox, icon, label)
            {
                IsIncluded = node.Entry.Locked || node.Entry.DefaultSelected
            };
            _allEntries.Add(entry);

            checkBox.IsEnabled = !node.Entry.Locked;
            checkBox.IsChecked = entry.IsIncluded;
            if (node.Entry.Locked)
                ToolTip.SetTip(checkBox, "Always included");
            else if (node.Entry.UsageHint != null)
                ToolTip.SetTip(label, node.Entry.UsageHint);

            ApplyEntryStyle(entry, entry.IsIncluded);
            checkBox.IsCheckedChanged += (_, _) => OnEntryCheckedChanged(entry, checkBox.IsChecked ?? false);
        }
        else
        {
            foreach (var child in node.Children.Values)
                item.Items.Add(BuildTreeItem(child));

            checkBox.IsChecked = DescendantState(item);
            checkBox.IsCheckedChanged += (_, _) =>
            {
                if (checkBox.IsChecked is { } state) SetDescendantLeaves(item, state);
            };
        }

        return item;
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
                if (child.Header is StackPanel sp)
                {
                    var cb = sp.Children.OfType<CheckBox>().FirstOrDefault();
                    if (cb != null) into.Add(cb);
                }
            }
            else
            {
                CollectLeafBoxes(child, into);
            }
        }
    }

    private void SetDescendantLeaves(TreeViewItem parent, bool isChecked)
    {
        var boxes = new List<CheckBox>();
        CollectLeafBoxes(parent, boxes);
        foreach (var cb in boxes.Where(b => b.IsEnabled))
            cb.IsChecked = isChecked;
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
        entry.Label.Foreground = isChecked
            ? PrimaryTextBrush()
            : entry.Entry.InUse
                ? InUseBrush
                : new SolidColorBrush(Color.FromArgb(160, 150, 150, 150));
        entry.Label.Opacity = isChecked || entry.Entry.InUse ? 1.0 : 0.75;
        entry.Icon.Opacity = isChecked ? 1.0 : entry.Entry.InUse ? 0.8 : 0.4;
    }

    private static bool IsSharedResourceNode(TreeNode node)
        => node.ZipPath.Equals(
            $"{WidgetPorter.ContentFolder}/{WidgetPorter.ExternalFolder}/{ResourcePaths.BundleFolder}",
            StringComparison.OrdinalIgnoreCase);

    private void SyncSelectAllBox()
    {
        _suppressSelectAllSync = true;
        var checkable = _allEntries.Where(e => e.CheckBox.IsEnabled).ToList();
        bool allOn = checkable.Count > 0 && checkable.All(e => e.IsIncluded);
        bool allOff = checkable.All(e => !e.IsIncluded);
        SelectAllBox.IsChecked = allOn ? true : allOff ? false : null;
        _suppressSelectAllSync = false;
    }

    private void SelectAllBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressSelectAllSync) return;
        if (SelectAllBox.IsChecked is not { } state) return;
        foreach (var entry in _allEntries.Where(en => en.CheckBox.IsEnabled))
            entry.CheckBox.IsChecked = state;
    }

    #endregion

    #region EXPORT

    private void MetaField_Changed(object? sender, TextChangedEventArgs e) => UpdateOutputPathText();

    #region PREVIEW

    private string _previewImagePath = string.Empty;

    private async void PreviewPick_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select preview image",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Images")
                    {
                        Patterns = WidgetPorter.PreviewExtensions.Select(ext => "*" + ext).ToArray()
                    }
                ]
            });

            var file = picked.FirstOrDefault();
            if (file == null) return;

            SetPreview(file.Path.LocalPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to pick a preview image");
        }
    }

    private void PreviewClear_Click(object? sender, RoutedEventArgs e) => SetPreview(null);

    #region PREVIEW DRAG AND DROP

    private bool _dropEnabled;
    private string EmptyPreviewText => _dropEnabled ? "select or drop an image here" : "No preview";

    private void TryEnableDragDrop()
    {
        try
        {
            DragDrop.SetAllowDrop(PreviewDropZone, true);
            PreviewDropZone.AddHandler(DragDrop.DragOverEvent, PreviewDragOver);
            PreviewDropZone.AddHandler(DragDrop.DragLeaveEvent, PreviewDragLeave);
            PreviewDropZone.AddHandler(DragDrop.DropEvent, PreviewDrop);

            _dropEnabled = true;
            ToolTip.SetTip(PreviewDropZone, "Drop an image here, or use Choose...");

            if (_previewImagePath.Length == 0) PreviewNameText.Text = EmptyPreviewText;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Drag and drop is unavailable for the preview image");
        }
    }

    private void PreviewDragOver(object? sender, DragEventArgs e)
    {
        bool accepted = FirstImagePath(e.DataTransfer) != null;

        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropHighlight(accepted);
        e.Handled = true;
    }

    private void PreviewDragLeave(object? sender, DragEventArgs e) => SetDropHighlight(false);

    private void PreviewDrop(object? sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        e.Handled = true;

        string? path = FirstImagePath(e.DataTransfer);
        if (path == null)
        {
            _logger?.LogDebug("Ignored a drop carrying no supported image file");
            return;
        }

        SetPreview(path);
    }

    private static string? FirstImagePath(IDataTransfer data)
    {
        try
        {
            var files = data.TryGetFiles();
            if (files == null) return null;

            foreach (var item in files)
            {
                string? path = item.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;

                if (WidgetPorter.PreviewExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    return path;
            }
        }
        catch {/**/}

        return null;
    }

    private void SetDropHighlight(bool active)
    {
        PreviewDropZone.BorderBrush = active && AccentBrush() is { } accent
            ? accent
            : Brushes.Transparent;
    }

    private IBrush? AccentBrush()
        => this.TryFindResource("AccentFillColorDefaultBrush", ActualThemeVariant, out var b) && b is IBrush brush
            ? brush
            : null;

    #endregion

    private void SetPreview(string? path)
    {
        _previewImagePath = string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? string.Empty : path;

        bool has = _previewImagePath.Length > 0;
        PreviewClearButton.IsVisible = has;
        PreviewNameText.Text = has ? Path.GetFileName(_previewImagePath) : EmptyPreviewText;
        ToolTip.SetTip(PreviewNameText, has ? _previewImagePath : null);

        if (!has)
        {
            PreviewThumb.Source = null;
            return;
        }

        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(_previewImagePath));
            PreviewThumb.Source = new Bitmap(stream);
        }
        catch (Exception ex)
        {
            PreviewThumb.Source = null;
            _logger?.LogWarning(ex, "Could not render preview thumbnail for {Path}", _previewImagePath);
        }
    }

    #endregion

    private WidgetPorter.SmwExportOptions BuildOptions() => new()
    {
        Name = string.IsNullOrWhiteSpace(WidgetNameBox.Text) ? _widget?.Name ?? "widget" : WidgetNameBox.Text.Trim(),
        Author = AuthorBox.Text?.Trim() ?? string.Empty,
        Group = WidgetPackPaths.NormalizeGroup(GroupBox.Text),
        Version = string.IsNullOrWhiteSpace(VersionBox.Text) ? "1.0.0" : VersionBox.Text.Trim(),
        Tags = WidgetPorter.ParseTags(TagsBox.Text),
        PreviewImagePath = _previewImagePath,
        AppVersion = AppVersionBox.Text?.Trim() ?? string.Empty
    };

    private string BuildOutputPath()
    {
        var opts = BuildOptions();
        return Path.Combine(WidgetPorter.ExportsDirectory,
            WidgetPorter.BuildFileName(opts.Author, opts.Group, opts.Name, opts.Version));
    }

    private void UpdateOutputPathText()
    {
        if (OutputPathText == null) return;
        OutputPathText.Text = BuildOutputPath();
    }

    private async void ConfirmButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_widget == null || _plan == null) return;

        string outputPath = BuildOutputPath();

        if (File.Exists(outputPath))
        {
            var overwrite = new FAContentDialog
            {
                Title = "File Already Exists",
                Content = $"\"{Path.GetFileName(outputPath)}\" already exists in the widget exports folder.\n\nOverwrite it?",
                PrimaryButtonText = "Overwrite",
                CloseButtonText = "Cancel"
            };
            if (await overwrite.ShowAsync() != FAContentDialogResult.Primary) return;
        }

        ConfirmButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        ConfirmButton.Content = "Exporting...";

        try
        {
            foreach (var entry in _allEntries)
                entry.Entry.DefaultSelected = entry.IsIncluded;

            await WidgetPorter.ExportWidgetAsync(_plan, BuildOptions(), outputPath);

            if (WidgetPackInstaller.Install(outputPath) == null)
                _logger?.LogWarning("Exported {Path} but could not file it into the widget store", outputPath);

            UiHelpers.RevealInFileManager(outputPath);
            Close();
        }
        catch (Exception ex)
        {
            await ShowError($"Export failed: {ex.Message}");
            _logger?.LogError(ex, "Widget export failed");
        }
        finally
        {
            ConfirmButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            ConfirmButton.Content = "Export";
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close();

    private static async Task ShowError(string message)
    {
        var box = new FAContentDialog
        {
            Title = "Export Error",
            Content = message,
            CloseButtonText = "OK"
        };
        await box.ShowAsync();
    }

    #endregion

    private IBrush PrimaryTextBrush()
        => this.TryFindResource("TextFillColorPrimaryBrush", this.ActualThemeVariant, out var b) && b is IBrush brush
            ? brush
            : Brushes.Gray;

    private class TreeNode(string name)
    {
        public string Name { get; } = name;
        public string ZipPath { get; init; } = string.Empty;
        public WidgetPorter.SmwEntry? Entry { get; set; }
        public Dictionary<string, TreeNode> Children { get; } = new();
    }

    private class FileEntry(WidgetPorter.SmwEntry entry, CheckBox checkBox, SymIcon icon, TextBlock label)
    {
        public WidgetPorter.SmwEntry Entry { get; } = entry;
        public CheckBox CheckBox { get; } = checkBox;
        public SymIcon Icon { get; } = icon;
        public TextBlock Label { get; } = label;
        public bool IsIncluded { get; set; }
    }
}
