using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
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
        }

        bool isDevOrBeta = AppServices.AppVersion.Contains('+');
        AppOverrideSection.IsVisible = isDevOrBeta;
        AppVersionBox.Text = AppServices.AppVersion;

        PopulateTree(_plan);
        SyncSelectAllBox();
        UpdateOutputPathText();
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
                    child = new TreeNode(parts[i]) { Entry = isLeaf ? entry : null };
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
            IsExpanded = true,
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

    private void ApplyEntryStyle(FileEntry entry, bool isChecked)
    {
        entry.Label.Foreground = isChecked
            ? PrimaryTextBrush()
            : new SolidColorBrush(Color.FromArgb(160, 150, 150, 150));
        entry.Label.Opacity = isChecked ? 1.0 : 0.75;
        entry.Icon.Opacity = isChecked ? 1.0 : 0.4;
    }

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

    private WidgetPorter.SmwExportOptions BuildOptions() => new()
    {
        Name = string.IsNullOrWhiteSpace(WidgetNameBox.Text) ? _widget?.Name ?? "widget" : WidgetNameBox.Text.Trim(),
        Author = AuthorBox.Text?.Trim() ?? string.Empty,
        Group = WidgetPackPaths.NormalizeGroup(GroupBox.Text),
        Version = string.IsNullOrWhiteSpace(VersionBox.Text) ? "1.0.0" : VersionBox.Text.Trim(),
        Tags = WidgetPorter.ParseTags(TagsBox.Text),
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
