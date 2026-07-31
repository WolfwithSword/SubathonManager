using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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

    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private bool _skipDeleteConfirm;
    private bool _busy;

    public WidgetBrowserDialog() : this(null) { }

    public WidgetBrowserDialog(Func<WidgetCatalogEntry, Task<bool>>? onAdd)
    {
        InitializeComponent();
        SectionsList.ItemsSource = _sections;

        _onAdd = onAdd;
        AddSelectedButton.IsVisible = onAdd != null;

        LoadState();

        Opened += async (_, _) => await RefreshAsync();
    }

    #region persisted state

    private void LoadState()
    {
        try
        {
            using var db = _factory.CreateDbContext();

            string packed = StateValueHelper.Get<string>(db, StateKeys.WidgetBrowserCollapsed);
            foreach (var key in packed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                _collapsed.Add(key);

            _skipDeleteConfirm = StateValueHelper.Get(db, StateKeys.WidgetBrowserSkipDeleteConfirm, false);
            AllVersionsCheck.IsChecked = StateValueHelper.Get(db, StateKeys.WidgetBrowserAllVersions, false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not read widget browser state");
        }
    }

    private void SaveState()
    {
        try
        {
            using var db = _factory.CreateDbContext();

            StateValueHelper.Set(db, StateKeys.WidgetBrowserCollapsed, string.Join('\n', _collapsed));
            StateValueHelper.Set(db, StateKeys.WidgetBrowserSkipDeleteConfirm, _skipDeleteConfirm);
            StateValueHelper.Set(db, StateKeys.WidgetBrowserAllVersions, AllVersionsCheck.IsChecked == true);

            db.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not save widget browser state");
        }
    }

    private void SetCollapsed(string key, bool collapsed)
    {
        if (collapsed) _collapsed.Add(key);
        else _collapsed.Remove(key);
    }

    #endregion

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
            UpdateAddSelectedButton();
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
            string sectionKey = "S:" + sectionGroup.Key;
            var section = new CatalogSection
            {
                Title = sectionGroup.Key,
                Key = sectionKey,
                IsExpanded = !_collapsed.Contains(sectionKey)
            };

            foreach (var groupGroup in sectionGroup
                         .GroupBy(i => i.GroupTitle, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g.Key == CatalogItem.UngroupedName ? 1 : 0)
                         .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                string key = $"G:{sectionGroup.Key}|{groupGroup.Key}";
                var group = new CatalogGroup
                {
                    Title = groupGroup.Key,
                    Key = key,
                    IsExpanded = !_collapsed.Contains(key),
                    CountLabel = $"({groupGroup.Count()})"
                };

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

    private void GroupToggle_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: CatalogGroup group } toggle) return;
        bool expanded = toggle.IsChecked == true;

        group.IsExpanded = expanded;
        SetCollapsed(group.Key, !expanded);
    }

    private void SectionExpander_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander { Tag: CatalogSection section } expander) return;

        section.IsExpanded = expander.IsExpanded;
        SetCollapsed(section.Key, !expander.IsExpanded);
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

    private void Card_Tapped(object? sender, TappedEventArgs e)
    {
        if (_onAdd == null || ItemFrom(sender) is not { } item) return;

        item.IsSelected = !item.IsSelected;
        UpdateAddSelectedButton();
    }

    private async void Card_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ItemFrom(sender) is not { } item) return;
        if (item.IsSelected)
        {
            item.IsSelected = false;
            UpdateAddSelectedButton();
        }

        await AddAsync(item);
    }

    private async void AddEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } item) return;

        if (item.IsSelected)
        {
            item.IsSelected = false;
            UpdateAddSelectedButton();
        }

        await AddAsync(item);
    }

    private async void AddSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (_onAdd == null || _busy) return;

        var selected = _all.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        _busy = true;
        AddSelectedButton.IsEnabled = false;
        SetStatus($"Adding {selected.Count} widget(s)...");

        int added = 0;

        try
        {
            foreach (var item in from item in selected 
                     let file = WidgetCatalog.ToAbsolutePath(item.Entry.PackPath) 
                     where File.Exists(file) select item)
            {
                try
                {
                    if (await _onAdd(item.Entry)) added++;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to add catalogued widget {Path}", 
                        item.Entry.PackPath);
                }
            }

            foreach (var item in selected) item.IsSelected = false;

            SetStatus(added == selected.Count
                ? $"Added {added} widget(s) to the overlay."
                : $"Added {added} of {selected.Count} widget(s); the rest could not be added.");
        }
        finally
        {
            _busy = false;
            UpdateAddSelectedButton();
        }
    }

    private void UpdateAddSelectedButton()
    {
        if (_onAdd == null) return;

        int count = _all.Count(i => i.IsSelected);

        AddSelectedButton.IsEnabled = count > 0 && !_busy;
        AddSelectedButton.Content = count > 0 ? $"Add {count} Widget(s)" : "Add Widget(s)";
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

    private async void RefreshEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } item || _busy) return;

        _busy = true;
        SetStatus($"Refreshing \"{item.Name}\"...");

        try
        {
            string packPath = item.Entry.PackPath;
            var updated = await Task.Run(() => WidgetCatalog.RefreshEntryAsync(_factory, packPath));

            DropCachedPreview(item.Entry.PreviewCachePath);
            if (updated == null)
            {
                _all.Remove(item);
                BuildTree();
                UpdateAddSelectedButton();
                SetStatus($"\"{item.Name}\" is no longer readable and was removed from the list.");
                return;
            }

            int index = _all.IndexOf(item);
            var replacement = new CatalogItem
            {
                Entry = updated,
                Preview = LoadPreview(updated.PreviewCachePath),
                TagList = CatalogItem.SplitTags(updated.Tags),
                CanAdd = _onAdd != null,
                SearchBlob = BuildSearchBlob(updated),
                IsSelected = item.IsSelected
            };

            if (index >= 0) _all[index] = replacement;
            else _all.Add(replacement);

            BuildTree();
            SetStatus($"Refreshed \"{replacement.Name}\".");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to refresh catalogued widget {Path}", item.Entry.PackPath);
            SetStatus($"Could not refresh \"{item.Name}\".");
        }
        finally
        {
            _busy = false;
        }
    }

    private void DropCachedPreview(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!_previews.Remove(path, out var bitmap)) return;

        try { bitmap.Dispose(); }
        catch { /**/ }
    }

    private void ViewEntryDocs_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { DocsUrl: { } url }) return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to open docs {Url}", url);
            SetStatus($"Could not open the docs for \"{ItemFrom(sender)?.Name}\".");
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

        if (!_skipDeleteConfirm && !await ConfirmDeleteAsync(item)) return;

        bool deleted = await Task.Run(() => WidgetCatalog.DeleteAsync(_factory, item.Entry));

        if (!deleted)
        {
            SetStatus($"Could not delete \"{item.Name}\".");
            return;
        }

        _all.Remove(item);
        BuildTree();
        UpdateAddSelectedButton();
        SetStatus($"Deleted \"{item.Name}\".");
    }

    private async Task<bool> ConfirmDeleteAsync(CatalogItem item)
    {
        var skipBox = new CheckBox
        {
            Content = "Don't ask again",
            Margin = new Thickness(0, 14, 0, 0)
        };

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = $"Permanently delete \"{item.Name}\" {item.VersionLabel} from disk?",
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(skipBox);

        var confirm = new FAContentDialog
        {
            Title = "Delete Widget Package",
            Content = body,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel"
        };

        if (await confirm.ShowAsync() != FAContentDialogResult.Primary) return false;

        if (skipBox.IsChecked == true)
        {
            _skipDeleteConfirm = true;
            SaveState();
        }

        return true;
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        SaveState();

        foreach (var bitmap in _previews.Values)
        {
            try { bitmap.Dispose(); }
            catch { /**/ }
        }
        _previews.Clear();
    }
}
