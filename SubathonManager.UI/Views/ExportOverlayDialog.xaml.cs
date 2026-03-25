using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using Wpf.Ui.Appearance;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using TreeViewItem = Wpf.Ui.Controls.TreeViewItem;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views;

public partial class ExportOverlayDialog
{
    private readonly Route? _route;
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<ExportOverlayDialog>>();

    private readonly List<FileEntry> _allEntries = new();
    private bool _suppressSelectAllSync = false;

    public ExportOverlayDialog(Route route)
    {
        var factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        using var db = factory.CreateDbContext();
        _route = db.Routes.AsNoTracking()
            .Include(r => r.Widgets).ThenInclude(w => w.CssVariables)
            .Include(r => r.Widgets).ThenInclude(w => w.JsVariables)
            .FirstOrDefault(r => r.Id == route.Id);

        if (_route == null)
        {
            DialogResult = false;
            Close();
            return;
        }

        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        ExportNameBox.Text = $"{_route.Name} Export";
        var fileList = BuildFileList(_route);
        PopulateTree(fileList);
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

            if (Directory.Exists(widgetRoot))
            {
                result.AddRange(from file in Directory.EnumerateFiles(widgetRoot, "*", SearchOption.AllDirectories) 
                    let relative = Path.GetRelativePath(widgetRoot, file).Replace('\\', '/') select ($"{zipWidgetRoot}/{relative}", file));
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
                        let rel = Path.GetRelativePath(jsVar.Value, file).Replace('\\', '/') select ($"{zipWidgetRoot}/_external/{jsVar.Name}/{rel}", file));
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
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !isGenerated
        };

        var icon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = isLeaf ? Wpf.Ui.Controls.SymbolRegular.Document24
                            : Wpf.Ui.Controls.SymbolRegular.Folder24,
            Margin = new Thickness(0, 0, 5, 0),
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
                : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(checkBox);
        header.Children.Add(icon);
        header.Children.Add(label);

        var item = new TreeViewItem
        {
            Header = header,
            IsExpanded = true,
            Padding = new Thickness(2)
        };

        if (isLeaf)
        {
            var entry = new FileEntry(node.ZipEntry ?? node.Name, node.AbsSource, checkBox, icon, label);
            _allEntries.Add(entry);

            checkBox.Checked += (_, _) => OnEntryCheckedChanged(entry, true);
            checkBox.Unchecked += (_, _) => OnEntryCheckedChanged(entry, false);
        }
        else
        {
            foreach (var child in node.Children.Values)
                item.Items.Add(BuildTreeItem(child));

            checkBox.Checked += (_, _) => SetDescendantLeaves(item, true);
            checkBox.Unchecked += (_, _) => SetDescendantLeaves(item, false);
        }

        return item;
    }
    private void OnEntryCheckedChanged(FileEntry entry, bool isChecked)
    {
        entry.IsIncluded = isChecked;
        ApplyEntryStyle(entry, isChecked);
        SyncSelectAllBox();
    }

    private static void ApplyEntryStyle(FileEntry entry, bool isChecked)
    {
        if (isChecked)
        {
            entry.Label.Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
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

    private void SelectAllBox_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectAllSync) return;
        foreach (var entry in _allEntries.Where(e2 => e2.CheckBox.IsEnabled))
            entry.CheckBox.IsChecked = true;
    }

    private void SelectAllBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectAllSync) return;
        foreach (var entry in _allEntries.Where(e2 => e2.CheckBox.IsEnabled))
            entry.CheckBox.IsChecked = false;
    }

    private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        string exportName = ExportNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(exportName))
            exportName = _route!.Name;

        string safeFileName = string.Concat(exportName.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        string exportsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "exports");
        Directory.CreateDirectory(exportsDir);

        var dialog = new SaveFileDialog
        {
            Title = "Save Overlay Export",
            Filter = "Subathon Manager Overlay (*.smo)|*.smo",
            DefaultExt = "smo",
            FileName = safeFileName,
            InitialDirectory = exportsDir
        };

        if (dialog.ShowDialog(this) != true) return;

        ConfirmButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        ConfirmButton.Content = "Exporting…";

        try
        {
            if (_route == null)
            {
                ShowError("Could not load route data for export.");
                return;
            }

            var excludedZipEntries = _allEntries
                .Where(entry => entry is { IsIncluded: false, AbsSource: not null })
                .Select(entry => entry.ZipEntry)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            await OverlayPorter.ExportRouteAsync(_route, dialog.FileName, exportName, excludedZipEntries);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowError($"Export failed: {ex.Message}");
            _logger?.LogError(ex, "Export failed");
        }
        finally
        {
            ConfirmButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            ConfirmButton.Content = "Export…";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowError(string message)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Export Error",
            Content = message,
            CloseButtonText = "OK",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        _ = box.ShowDialogAsync();
    }

    private class TreeNode(string name)
    {
        public string Name { get; } = name;
        public string? AbsSource { get; set; }
        public bool IsLeaf { get; set; }
        public string? ZipEntry { get; set; }
        public Dictionary<string, TreeNode> Children { get; } = new();
    }

    private class FileEntry(string zipEntry, string? absSource, CheckBox checkBox,
        Wpf.Ui.Controls.SymbolIcon icon, TextBlock label)
    {
        public string ZipEntry { get; } = zipEntry;
        public string? AbsSource { get; } = absSource;
        public CheckBox CheckBox { get; } = checkBox;
        public Wpf.Ui.Controls.SymbolIcon Icon { get; } = icon;
        public TextBlock Label { get; } = label;
        public bool IsIncluded { get; set; } = true;
    }
}