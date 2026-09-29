using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Services;
using SubathonManager.UI.Controls;
using SubathonManager.UI.Services;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views;

public partial class SubathonSummaryWindow : Window {
    private static readonly int[] PageSizes = [100, 250, 500, 1000];

    private static readonly (string Label, TimeSpan? Span)[] TimePresets = [
        ("All time", null),
        ("Last 15 minutes", TimeSpan.FromMinutes(15)),
        ("Last hour", TimeSpan.FromHours(1)),
        ("Last 6 hours", TimeSpan.FromHours(6)),
        ("Last 12 hours", TimeSpan.FromHours(12)),
        ("Last 24 hours", TimeSpan.FromDays(1)),
        ("Last 7 days", TimeSpan.FromDays(7)),
        ("Last 30 days", TimeSpan.FromDays(30)),
        ("Custom", null)
    ];

    private readonly ObservableCollection<BreakdownRow> _breakdown = [];

    private readonly IConfig _config = AppServices.Provider.GetRequiredService<IConfig>();

    private readonly IDbContextFactory<AppDbContext> _factory =
        AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();

    private readonly ILogger? _logger = AppServices.Provider.GetService<ILogger<SubathonSummaryWindow>>();

    private readonly ObservableCollection<SummaryEventRow> _rows = [];

    private bool _loading = true;

    private List<EventKey> _matched = [];
    private int _page;
    private int _pageSize = 250;
    private bool _querying;
    private List<SubathonOption> _subathons = [];

    public SubathonSummaryWindow() {
        InitializeComponent();

        EventGrid.ItemsSource = _rows;
        BreakdownList.ItemsSource = _breakdown;
        LbGrid.ItemsSource = _lbRows;

        TimePresetBox.ItemsSource = TimePresets.Select(p => p.Label).ToList();
        TimePresetBox.SelectedIndex = 0;

        PageSizeBox.ItemsSource = PageSizes;
        PageSizeBox.SelectedItem = _pageSize;

        SourcePopout.EmptyText = "All sources";
        TypePopout.EmptyText = "All event types";
        SourcePopout.SetOptions(BuildSourceOptions());
        TypePopout.SetOptions(FilterOption.EventTypes());

        InitLeaderboardTab();

        Opened += async (_, _) => await LoadSubathonsAsync();
    }

    private SubathonOption? Selected => SubathonBox.SelectedItem as SubathonOption;

    private bool CanModify => Selected?.IsActive == true;

    private int PageCount => _matched.Count == 0 ? 1 : (_matched.Count + _pageSize - 1) / _pageSize;

    private async Task LoadSubathonsAsync() {
        List<SubathonOption> options = [];
        try {
            await using AppDbContext db = await _factory.CreateDbContextAsync();
            List<SubathonData> all = await db.SubathonDatas.AsNoTracking().ToListAsync();

            // use first event as subathon timestamp
            Dictionary<Guid, DateTime> firstEvent = await db.SubathonEvents.AsNoTracking()
                .Where(e => e.SubathonId != null)
                .GroupBy(e => e.SubathonId!.Value)
                .Select(g => new { Id = g.Key, First = g.Min(e => e.EventTimestamp) })
                .ToDictionaryAsync(x => x.Id, x => x.First);

            options = all.Select(s => new SubathonOption {
                    Id = s.Id,
                    Name = s.Name,
                    IsActive = s.IsActive,
                    Started = firstEvent.TryGetValue(s.Id, out DateTime first) ? first : null
                })
                .OrderByDescending(s => s.IsActive)
                .ThenByDescending(s => s.Started ?? DateTime.MinValue)
                .ToList();
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "[Summary] Failed to load subathons");
        }

        await Dispatcher.UIThread.InvokeAsync(() => {
            _subathons = options;
            SubathonBox.ItemsSource = _subathons;
            _loading = false;
            if (_subathons.Count > 0) SubathonBox.SelectedIndex = 0;
            else ResultStatus.Text = "No subathons found";
        });
    }

    private static List<FilterOption> BuildSourceOptions() {
        return Enum.GetValues<SubathonEventSource>()
            .Where(s => s != SubathonEventSource.Unknown)
            .OrderBy(SubathonEventSourceHelper.GetSourceOrder)
            .ThenBy(s => s.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(s => new FilterOption {
                Label = s.GetDescription(),
                Value = s.ToString(),
                Group = SourceGroupHeader(s)
            })
            .ToList();
    }

    private static string SourceGroupHeader(SubathonEventSource source) {
        SubathonSourceGroup group = source.GetGroup();
        return group is SubathonSourceGroup.UseSource or SubathonSourceGroup.Unknown ? "Other" : group.GetLabel();
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e) {
        await RunQueryAsync();
    }

    private async void SubathonBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        if (_loading) return;
        ReadOnlyNotice.IsVisible = Selected != null && !CanModify;
        UpdateRowButtons();
        UpdateSubathonPinBoxes();
        InvalidateAmounts();
        await RunQueryAsync();
    }

    private async Task RunQueryAsync() {
        SubathonOption? subathon = Selected;
        if (subathon == null || _querying) return;

        (DateTime? from, DateTime? to, string? timeError) = ParseTimeRange();
        if (timeError != null) {
            ResultStatus.Text = timeError;
            return;
        }

        string idSearch = (IdSearchBox.Text ?? string.Empty).Trim();
        bool exactId = Guid.TryParse(idSearch, out Guid wantedId);
        if (!exactId && idSearch.Length is > 0 and < 4) {
            ResultStatus.Text = "Enter at least 4 characters of an event Id";
            return;
        }

        HashSet<string> sources = SourcePopout.SelectedOptions.Select(s => s.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<SubathonEventType?> typeFilter = TypePopout.SelectedOptions
            .Select(t => Enum.TryParse((string?)t.Value, out SubathonEventType parsed) ? (SubathonEventType?)parsed : null)
            .Where(t => t != null)
            .ToList();

        bool includeUnprocessed = IncludeUnprocessedBox.IsChecked == true;
        List<string> userList = SplitCsv(UserListBox.Text);
        bool whitelist = UserWhitelistRadio.IsChecked == true;

        _lastFilterCells = DescribeFilters(from, to, sources, typeFilter, userList, whitelist, includeUnprocessed,
            idSearch);

        _querying = true;
        ResultStatus.Text = "Scanning...";
        List<EventKey> matched = [];

        try {
            await using AppDbContext db = await _factory.CreateDbContextAsync();
            IQueryable<SubathonEvent> q = db.SubathonEvents.AsNoTracking()
                .Where(ev => ev.SubathonId == subathon.Id);

            if (exactId) {
                q = q.Where(ev => ev.Id == wantedId);
            }
            else {
                if (!includeUnprocessed) q = q.Where(ev => ev.ProcessedToSubathon);
                if (from != null) q = q.Where(ev => ev.EventTimestamp >= from);
                if (to != null) q = q.Where(ev => ev.EventTimestamp <= to);
                if (typeFilter.Count > 0) q = q.Where(ev => typeFilter.Contains(ev.EventType));
            }

            await foreach (ScanRow scan in q
                               .OrderByDescending(ev => ev.EventTimestamp)
                               .Select(ev => new ScanRow {
                                   Id = ev.Id,
                                   Source = ev.Source,
                                   Type = ev.EventType,
                                   Meta = ev.EventTypeMeta,
                                   User = ev.User
                               })
                               .AsAsyncEnumerable()) {
                if (!exactId) {
                    if (idSearch.Length > 0 &&
                        !scan.Id.ToString().StartsWith(idSearch, StringComparison.OrdinalIgnoreCase)) continue;

                    if (sources.Count > 0) {
                        string trueSource = scan.Type.GetTypeTrueSource(scan.Meta) ?? scan.Source.ToString();
                        if (!sources.Contains(trueSource) && !sources.Contains(scan.Source.ToString())) continue;
                    }

                    if (!MatchesUserFilter(scan.User, userList, whitelist)) continue;
                }

                matched.Add(new EventKey(scan.Id, scan.Source));
            }
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "[Summary] Query failed");
            ResultStatus.Text = "Query failed, check the logs";
            _querying = false;
            return;
        }

        _querying = false;
        _matched = matched;
        _page = 0;
        await LoadPageAsync();

        if (matched.Count == 0 && exactId) await ReportMissingIdAsync(wantedId, subathon);
    }

    private async Task ReportMissingIdAsync(Guid id, SubathonOption current) {
        try {
            await using AppDbContext db = await _factory.CreateDbContextAsync();
            Guid? owner = await db.SubathonEvents.AsNoTracking()
                .Where(ev => ev.Id == id)
                .Select(ev => ev.SubathonId)
                .FirstOrDefaultAsync();

            if (owner == null) {
                ResultStatus.Text = $"No event with Id {id} exists in any subathon";
                return;
            }

            SubathonOption? other = _subathons.FirstOrDefault(s => s.Id == owner);
            ResultStatus.Text = other == null
                ? $"That event belongs to a different subathon ({owner})"
                : $"That event is in \"{other.Name}\", not \"{current.Name}\". Switch subathon to see it";
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[Summary] Cross-subathon id lookup failed");
        }
    }

    private async Task LoadPageAsync() {
        _page = Math.Clamp(_page, 0, PageCount - 1);
        List<EventKey> slice = _matched.Skip(_page * _pageSize).Take(_pageSize).ToList();

        List<SummaryEventRow> rows = [];
        if (slice.Count > 0)
            try {
                List<Guid> ids = slice.Select(k => k.Id).Distinct().ToList();
                await using AppDbContext db = await _factory.CreateDbContextAsync();
                List<SubathonEvent> events = await db.SubathonEvents.AsNoTracking()
                    .Where(ev => ids.Contains(ev.Id))
                    .ToListAsync();

                Dictionary<EventKey, SubathonEvent> byKey = events
                    .GroupBy(ev => new EventKey(ev.Id, ev.Source))
                    .ToDictionary(g => g.Key, g => g.First());

                rows = slice.Where(byKey.ContainsKey)
                    .Select(key => {
                        SubathonEvent ev = byKey[key];
                        return new SummaryEventRow(ev,
                            ev.EventType.GetTypeTrueSource(ev.EventTypeMeta) ?? ev.Source.ToString());
                    })
                    .ToList();
            }
            catch (Exception ex) {
                _logger?.LogError(ex, "[Summary] Failed to load page");
                ResultStatus.Text = "Could not load that page, see logs";
                return;
            }

        _rows.Clear();
        foreach (SummaryEventRow row in rows) _rows.Add(row);

        int total = _matched.Count;
        int firstRow = total == 0 ? 0 : _page * _pageSize + 1;
        int lastRow = Math.Min((_page + 1) * _pageSize, total);

        ResultStatus.Text = total == 0
            ? "No events matched the current filters"
            : $"Showing {firstRow:N0}-{lastRow:N0} of {total:N0} matching events";
        PageStatus.Text = $"Page {_page + 1:N0} of {PageCount:N0}";

        FirstPageBtn.IsEnabled = PrevPageBtn.IsEnabled = _page > 0;
        NextPageBtn.IsEnabled = LastPageBtn.IsEnabled = _page < PageCount - 1;

        UpdateRowButtons();
        if (!BreakdownPane.IsVisible) return;
        await RebuildBreakdownAsync();
    }

    private static List<string> DescribeFilters(DateTime? from, DateTime? to, HashSet<string> sources,
        List<SubathonEventType?> types, List<string> users, bool whitelist, bool includeUnprocessed, string idSearch) {
        string Stamp(DateTime value) {
            return value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        return [
            $"Time From: {(from == null ? "any" : Stamp(from.Value))}",
            $"Time To: {(to == null ? "any" : Stamp(to.Value))}",
            $"Sources: {(sources.Count == 0 ? "all" : string.Join(" | ", sources.OrderBy(x => x)))}",
            $"Event Types: {(types.Count == 0 ? "all" : string.Join(" | ", types.Select(t => t.ToString()).OrderBy(x => x)))}",
            $"Users ({(whitelist ? "whitelist" : "blacklist")}): {(users.Count == 0 ? "none" : string.Join(" | ", users))}",
            $"Include Unprocessed: {(includeUnprocessed ? "Yes" : "No")}",
            $"Event ID: {(idSearch.Length == 0 ? "none" : idSearch)}"
        ];
    }

    private static bool MatchesUserFilter(string? user, List<string> list, bool whitelist) {
        if (list.Count == 0) return true;
        string name = (user ?? string.Empty).Trim();
        bool hit = list.Any(entry => entry.EndsWith('*')
            ? name.StartsWith(entry[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, entry, StringComparison.OrdinalIgnoreCase));
        return whitelist ? hit : !hit;
    }

    private static List<string> SplitCsv(string? raw) {
        return (raw ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private async void FirstPage_Click(object? sender, RoutedEventArgs e) {
        _page = 0;
        await LoadPageAsync();
    }

    private async void LastPage_Click(object? sender, RoutedEventArgs e) {
        _page = PageCount - 1;
        await LoadPageAsync();
    }

    private async void PrevPage_Click(object? sender, RoutedEventArgs e) {
        _page--;
        await LoadPageAsync();
    }

    private async void NextPage_Click(object? sender, RoutedEventArgs e) {
        _page++;
        await LoadPageAsync();
    }

    private async void PageSize_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        if (PageSizeBox.SelectedItem is not int size || size == _pageSize) return;
        _pageSize = size;
        _page = 0;
        if (!_loading) await LoadPageAsync();
    }

    private void TimePreset_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        int index = TimePresetBox.SelectedIndex;
        if (index < 0 || index >= TimePresets.Length) return;

        (string label, TimeSpan? span) = TimePresets[index];
        if (label == "Custom") return;

        if (span == null) {
            FromBox.Text = string.Empty;
            ToBox.Text = string.Empty;
            return;
        }

        FromBox.Text = DateTime.Now.Subtract(span.Value).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        ToBox.Text = string.Empty;
    }

    private (DateTime?, DateTime?, string?) ParseTimeRange() {
        DateTime? from = null;
        DateTime? to = null;

        if (!string.IsNullOrWhiteSpace(FromBox.Text)) {
            if (!DateTime.TryParse(FromBox.Text.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.None,
                    out DateTime parsed))
                return (null, null, $"Could not read the 'From' time: {FromBox.Text}");
            from = parsed;
        }

        if (!string.IsNullOrWhiteSpace(ToBox.Text)) {
            if (!DateTime.TryParse(ToBox.Text.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.None,
                    out DateTime parsed))
                return (null, null, $"Could not read the 'To' time: {ToBox.Text}");
            to = parsed;
        }

        if (from != null && to != null && from > to)
            return (null, null, "The 'From' time is after the 'To' time");

        return (from, to, null);
    }

    private async void ResetFilters_Click(object? sender, RoutedEventArgs e) {
        SourcePopout.ClearSelection();
        TypePopout.ClearSelection();
        UserListBox.Text = string.Empty;
        IdSearchBox.Text = string.Empty;
        UserBlacklistRadio.IsChecked = true;
        IncludeUnprocessedBox.IsChecked = false;
        TimePresetBox.SelectedIndex = 0;
        FromBox.Text = string.Empty;
        ToBox.Text = string.Empty;
        await RunQueryAsync();
    }

    private void EventGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        UpdateRowButtons();
    }

    private List<SummaryEventRow> SelectedRows() {
        return EventGrid.SelectedItems?.OfType<SummaryEventRow>().ToList() ?? [];
    }

    private void UpdateRowButtons() {
        List<SummaryEventRow> selected = SelectedRows();
        int deletable = selected.Count(r => r.IsDeletable);

        EditUserBtn.IsEnabled = selected.Count > 0 && CanModify;
        DeleteEventBtn.IsEnabled = deletable > 0 && CanModify;

        EditUserBtn.Content = selected.Count > 1 ? $"Edit User ({selected.Count:N0})" : "Edit User";
        DeleteEventBtn.Content = deletable > 1 ? $"Delete ({deletable:N0})" : "Delete Event";
    }

    private async void EditUser_Click(object? sender, RoutedEventArgs e) {
        List<SummaryEventRow> selected = SelectedRows();
        if (selected.Count == 0 || !CanModify) return;

        string[] existing = selected.Select(r => r.User).Distinct(StringComparer.Ordinal).ToArray();
        var input = new TextBox {
            Text = existing.Length == 1 ? existing[0] : string.Empty,
            PlaceholderText = "Username",
            MinWidth = 320
        };

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock {
            Text = selected.Count == 1
                ? $"{selected[0].TrueSource} · {selected[0].TypeLabel} · {selected[0].Timestamp:yyyy-MM-dd HH:mm:ss}"
                : $"{selected.Count:N0} events selected, currently from {existing.Length:N0} " +
                  $"user{(existing.Length == 1 ? "" : "s")}.",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(input);

        var summary = new TextBlock {
            Opacity = 0.85, FontSize = 12, TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(summary);
        panel.Children.Add(new TextBlock {
            Text = "Only the user is changed. Timer, points and money are untouched",
            Opacity = 0.7, FontSize = 11, TextWrapping = TextWrapping.Wrap
        });

        void RefreshSummary() {
            string typed = (input.Text ?? string.Empty).Trim();
            int count = typed.Length == 0
                ? 0
                : selected.Count(r => !string.Equals(r.User, typed, StringComparison.Ordinal));
            summary.Text = typed.Length == 0
                ? "Enter a username"
                : $"Will change {count:N0} event{(count == 1 ? "" : "s")} to be from \"{typed}\"";
        }

        RefreshSummary();
        input.TextChanged += (_, _) => RefreshSummary();

        var dialog = new FAContentDialog {
            Title = selected.Count == 1 ? "Edit Event User" : $"Edit User on {selected.Count:N0} Events",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            Content = panel
        };

        if (await dialog.ShowAsync() != FAContentDialogResult.Primary) return;

        string updated = (input.Text ?? string.Empty).Trim();
        if (updated.Length == 0) return;

        List<SummaryEventRow> changing = selected
            .Where(r => !string.Equals(r.User, updated, StringComparison.Ordinal)).ToList();
        if (changing.Count == 0) {
            ResultStatus.Text = "Nothing to change, those events are already from that user";
            return;
        }

        try {
            await using AppDbContext db = await _factory.CreateDbContextAsync();
            HashSet<EventKey> wanted = changing.Select(r => new EventKey(r.Id, r.EventSource)).ToHashSet();
            List<Guid> ids = changing.Select(r => r.Id).Distinct().ToList();

            List<SubathonEvent> tracked = await db.SubathonEvents
                .Where(ev => ids.Contains(ev.Id))
                .ToListAsync();

            foreach (SubathonEvent ev in tracked.Where(ev => wanted.Contains(new EventKey(ev.Id, ev.Source))))
                ev.User = updated;
            await db.SaveChangesAsync();
            foreach (SummaryEventRow row in changing) row.User = updated;

            ResultStatus.Text =
                $"Changed {changing.Count:N0} event{(changing.Count == 1 ? "" : "s")} to be from {updated}.";
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "[Summary] Failed to update event user");
            ResultStatus.Text = "Could not update the user, check the logs";
        }
    }

    private async void DeleteEvent_Click(object? sender, RoutedEventArgs e) {
        if (!CanModify) return;

        List<SummaryEventRow> selected = SelectedRows();
        List<SummaryEventRow> deletable = selected.Where(r => r.IsDeletable).ToList();
        if (deletable.Count == 0) return;

        int skipped = selected.Count - deletable.Count;
        string detail = deletable.Count == 1
            ? $"{deletable[0].TrueSource} · {deletable[0].TypeLabel}\n" +
              $"{deletable[0].User} - {deletable[0].DisplayValue}\n{deletable[0].Timestamp:yyyy-MM-dd HH:mm:ss}"
            : string.Join("\n", deletable.Take(8)
                  .Select(r => $"{r.Timestamp:yyyy-MM-dd HH:mm:ss}  {r.TrueSource} · {r.TypeLabel} · {r.User}")) +
              (deletable.Count > 8 ? $"\n...and {deletable.Count - 8:N0} more" : string.Empty);

        string what = deletable.Count == 1 ? "this event" : $"these {deletable.Count:N0} events";
        string skipNote = skipped > 0
            ? $"\n\n{skipped:N0} selected control command(s) cannot be deleted and will be kept"
            : string.Empty;

        var dialog = new FAContentDialog {
            Title = deletable.Count == 1 ? "Delete Event" : $"Delete {deletable.Count:N0} Events",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            Content = new TextBlock {
                TextWrapping = TextWrapping.Wrap,
                Text = $"Permanently delete {what}?\n\n{detail}{skipNote}\n\n" +
                       "The time and points they added will be reversed out of the subathon. This cannot be undone"
            }
        };

        if (await dialog.ShowAsync() != FAContentDialogResult.Primary) return;

        ResultStatus.Text = $"Deleting {deletable.Count:N0} event{(deletable.Count == 1 ? "" : "s")}...";
        var failed = 0;

        try {
            await Task.Run(async () => {
                EventService? service = ServiceManager.EventsOrNull;
                if (service == null) return;

                foreach (SummaryEventRow row in deletable)
                    try {
                        await using AppDbContext db = await _factory.CreateDbContextAsync();
                        await service.DeleteSubathonEvent(db, row.Event);
                    }
                    catch (Exception ex) {
                        failed++;
                        _logger?.LogError(ex, "[Summary] Failed to delete event {Id}", row.Id);
                    }
            });
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "[Summary] Bulk delete failed");
            ResultStatus.Text = "Could not delete the events, see logs";
            return;
        }

        await RunQueryAsync();
        if (failed > 0) ResultStatus.Text = $"{failed:N0} event(s) could not be deleted, see logs.";
    }

    private readonly record struct EventKey(Guid Id, SubathonEventSource Source);

    private sealed class ScanRow {
        public Guid Id { get; init; }
        public SubathonEventSource Source { get; init; }
        public SubathonEventType? Type { get; init; }
        public string? Meta { get; init; }
        public string? User { get; init; }
    }

    public sealed class SummaryEventRow : INotifyPropertyChanged {
        private string _user;

        public SummaryEventRow(SubathonEvent ev, string trueSource) {
            Event = ev;
            TrueSource = trueSource;
            _user = ev.User ?? string.Empty;
        }

        public string User {
            get => _user;
            set {
                if (_user == value) return;
                _user = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(User)));
            }
        }

        public SubathonEvent Event { get; }
        public Guid Id => Event.Id;
        public SubathonEventSource EventSource => Event.Source;
        public DateTime Timestamp => Event.EventTimestamp;
        public string TrueSource { get; }
        public string TypeLabel => Event.EventType.GetLabel();
        public SubathonEventType? Type => Event.EventType;
        public string Meta => string.IsNullOrWhiteSpace(Event.EventTypeMeta) ? "" : Event.EventTypeMeta!;
        public int Amount => Event.Amount;
        public double FinalSeconds => Math.Round(Event.GetFinalSecondsValueRaw(), 2);
        public double FinalPoints => Event.GetFinalPointsValue();
        public string ProcessedText => Event.ProcessedToSubathon ? "Yes" : "No";
        public bool IsDeletable => !Event.Command.IsControlTypeCommand();

        public string DisplayValue {
            get {
                string value = Event.Value;
                if (Event.EventType is SubathonEventType.TwitchSub or SubathonEventType.TwitchGiftSub)
                    value = value switch {
                        "1000" => "T1",
                        "2000" => "T2",
                        "3000" => "T3",
                        _ => value
                    };

                return string.IsNullOrWhiteSpace(Event.Currency) ? value : $"{value} {Event.Currency}";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class SubathonOption {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public bool IsActive { get; init; }
        public DateTime? Started { get; init; }

        public override string ToString() {
            string started = Started == null
                ? "no events yet"
                : Started.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return IsActive ? $"(Active) {Name}  -  {started}" : $"{Name}  -  {started}";
        }
    }

    public sealed class BreakdownRow {
        public string Label { get; init; } = "";
        public string Value { get; init; } = "";
        public int Level { get; init; }
        public bool IsHeader { get; init; }
        public Thickness Indent => new(Level * 16, 0, 0, 0);
    }
}