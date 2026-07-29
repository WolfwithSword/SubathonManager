using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using FluentAvalonia.UI.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Data.Widgets;
using SubathonManager.UI.UiUtils;

namespace SubathonManager.UI.Views;

public partial class WidgetBrowserDialog : Window
{
    private readonly IDbContextFactory<AppDbContext> _factory =
        AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    private readonly ILogger? _logger = AppServices.Provider.GetService<ILogger<WidgetBrowserDialog>>();

    private readonly Func<WidgetCatalogEntry, Task<bool>>? _onAdd;

    private readonly List<CatalogItem> _all = [];
    private readonly ObservableCollection<CatalogSection> _sections = [];
    private readonly Dictionary<string, Bitmap> _previews = new(StringComparer.OrdinalIgnoreCase);
    private bool _busy;

    public WidgetBrowserDialog() : this(null) { }

    public WidgetBrowserDialog(Func<WidgetCatalogEntry, Task<bool>>? onAdd)
    {
        InitializeComponent();
        SectionsList.ItemsSource = _sections;

        _onAdd = onAdd;
        Opened += async (_, _) => await RefreshAsync();
    }

    #region scanning

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        SetStatus("Scanning for widgets...");

        try
        {
            var entries = await Task.Run(() => WidgetCatalog.RefreshAsync(_factory));

            _all.Clear();
            foreach (var entry in entries)
            {
                _all.Add(new CatalogItem
                {
                    Entry = entry,
                    Preview = LoadPreview(entry.PreviewCachePath),
                    TagList = CatalogItem.SplitTags(entry.Tags),
                    CanAdd = _onAdd != null,
                    SearchBlob = BuildSearchBlob(entry)
                });
            }

            BuildTree();
            SetStatus($"{_all.Count} widget package(s) found");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Widget catalog scan failed");
            SetStatus("Could not scan widget folders");
        }
        finally
        {
            _busy = false;
        }
    }

    private static string BuildSearchBlob(WidgetCatalogEntry entry)
        => string.Join(' ',
            entry.Name, entry.Author, entry.Group, entry.Tags,
            entry.Version, WidgetPackPaths.DisplayVersion(entry.Version)).ToLowerInvariant();

    private Bitmap? LoadPreview(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (_previews.TryGetValue(path, out var cached)) return cached;

        try
        {
            if (!File.Exists(path)) return null;
            var bitmap = new Bitmap(path);
            _previews[path] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region tree

    private void BuildTree()
    {
        _sections.Clear();

        var terms = (SearchBox.Text ?? string.Empty)
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        IEnumerable<CatalogItem> items = _all;

        if (terms.Length > 0)
            items = items.Where(i => terms.All(t => i.SearchBlob.Contains(t, StringComparison.Ordinal)));

        if (AllVersionsCheck.IsChecked != true)
        {
            items = items
                .GroupBy(i => i.VersionKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First());
        }

        var ordered = items.ToList();

        foreach (var sectionGroup in ordered
                     .GroupBy(i => i.SectionTitle, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key == CatalogItem.PresetSection ? 1 : 0)
                     .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var section = new CatalogSection { Title = sectionGroup.Key };

            foreach (var groupGroup in sectionGroup
                         .GroupBy(i => i.GroupTitle, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g.Key == CatalogItem.UngroupedName ? 1 : 0)
                         .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var group = new CatalogGroup { Title = groupGroup.Key };
                foreach (var item in groupGroup.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
                    group.Items.Add(item);

                section.Groups.Add(group);
            }

            section.CountLabel = $"({sectionGroup.Count()})";
            _sections.Add(section);
        }

        bool empty = ordered.Count == 0;
        EmptyText.IsVisible = empty;
        EmptyText.Text = _all.Count == 0
            ? "No .smw packages found under presets or imports/widgets."
            : "Nothing matches that search.";
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => BuildTree();

    private void AllVersionsCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        BuildTree();
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await RefreshAsync();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void SetStatus(string text) => StatusText.Text = text;

    #endregion

    #region actions

    private static CatalogItem? ItemFrom(object? sender)
        => sender is Control { Tag: CatalogItem item } ? item : null;

    private async void Card_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ItemFrom(sender) is { } item) await AddAsync(item);
    }

    private async void AddEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is { } item) await AddAsync(item);
    }

    private async Task AddAsync(CatalogItem item)
    {
        if (_onAdd == null)
        {
            SetStatus("Open an overlay for editing to add widgets to it.");
            return;
        }

        if (_busy) return;

        string file = WidgetCatalog.ToAbsolutePath(item.Entry.PackPath);
        if (!File.Exists(file))
        {
            SetStatus($"\"{item.Name}\" is no longer on disk - rescanning.");
            await RefreshAsync();
            return;
        }

        _busy = true;
        SetStatus($"Adding \"{item.Name}\"...");

        try
        {
            bool added = await _onAdd(item.Entry);
            SetStatus(added
                ? $"Added \"{item.Name}\" to the overlay."
                : $"Could not add \"{item.Name}\".");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to add catalogued widget {Path}", item.Entry.PackPath);
            SetStatus($"Could not add \"{item.Name}\".");
        }
        finally
        {
            _busy = false;
        }
    }

    private void OpenEntryFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } item) return;

        string file = WidgetCatalog.ToAbsolutePath(item.Entry.PackPath);
        if (File.Exists(file)) UiHelpers.RevealInFileManager(file);
        else SetStatus($"\"{item.Name}\" is no longer on disk.");
    }

    private async void DeleteEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { CanDelete: true } item) return;

        var confirm = new FAContentDialog
        {
            Title = "Delete Widget Package",
            Content = $"Permanently delete \"{item.Name}\" {item.VersionLabel} from disk?\n\n",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel"
        };

        if (await confirm.ShowAsync() != FAContentDialogResult.Primary) return;

        bool deleted = await Task.Run(() => WidgetCatalog.DeleteAsync(_factory, item.Entry));

        if (!deleted)
        {
            SetStatus($"Could not delete \"{item.Name}\".");
            return;
        }

        _all.Remove(item);
        BuildTree();
        SetStatus($"Deleted \"{item.Name}\".");
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        foreach (var bitmap in _previews.Values)
        {
            try { bitmap.Dispose(); }
            catch { /**/ }
        }
        _previews.Clear();
    }
}
