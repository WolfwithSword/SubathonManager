using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.UI.Controls;
using SubathonManager.UI.UiUtils;

namespace SubathonManager.UI.Views;

public partial class ImportMissedWindow : Window {
    private static readonly IBrush MissedBrush = new SolidColorBrush(Color.Parse("#4CAF50"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#E8A33D"));
    private static readonly IBrush CapturedBrush = new SolidColorBrush(Color.Parse("#808080"));

    private readonly List<ImportRow> _all = [];
    private readonly IDbContextFactory<AppDbContext> _factory =
        AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    private readonly ILogger? _logger = AppServices.Provider.GetService<ILogger<ImportMissedWindow>>();
    private readonly ObservableCollection<ImportRow> _rows = [];
    private readonly IMissedEventSource? _source;
    private CancellationTokenSource? _fetchCts;
    private bool _hasActiveSubathon;
    private DateTime? _subathonStart;

    public ImportMissedWindow() {
        InitializeComponent();
        EventGrid.ItemsSource = _rows;
        TypePopout.EmptyText = "All event types";
        TypePopout.SelectionChanged += (_, _) => ApplyFilter();

        DateTime now = DateTime.Now;
        DateTime from = now.AddHours(-24);
        FromDatePicker.SelectedDate = new DateTimeOffset(from.Date);
        FromTimePicker.SelectedTime = new TimeSpan(from.Hour, from.Minute, 0);
        ToDatePicker.SelectedDate = new DateTimeOffset(now.Date);
        ToTimePicker.SelectedTime = new TimeSpan(now.Hour, now.Minute, 0);
        Closed += (_, _) => _fetchCts?.Cancel();
    }

    public ImportMissedWindow(SubathonEventSource source, IMissedEventSource service) : this() {
        _source = service;
        Title = $"Import Missed Events - {source}";
        TypePopout.SetOptions(BuildTypeOptions(source), true);
    }

    public static void Open(Control owner, SubathonEventSource source, IMissedEventSource service) {
        var window = new ImportMissedWindow(source, service);
        if (TopLevel.GetTopLevel(owner) is Window parent) {
            UiHelpers.CenterOver(window, parent);
            _ = window.ShowDialog(parent);
            return;
        }

        window.Show();
    }

    private async void Fetch_Click(object? sender, RoutedEventArgs e) {
        if (_source == null) return;
        if (FromDatePicker.SelectedDate is not { } fromDate || ToDatePicker.SelectedDate is not { } toDate) {
            StatusText.Text = "Pick both a From and a To date";
            return;
        }

        DateTime from = fromDate.Date + (FromTimePicker.SelectedTime ?? TimeSpan.Zero);
        DateTime to = toDate.Date + (ToTimePicker.SelectedTime ?? TimeSpan.Zero) + TimeSpan.FromMinutes(1);
        if (from >= to) {
            StatusText.Text = "From has to be before To";
            return;
        }

        _fetchCts?.Cancel();
        _fetchCts = new CancellationTokenSource();
        CancellationToken ct = _fetchCts.Token;
        FetchBtn.IsEnabled = false;
        ImportBtn.IsEnabled = false;
        StatusText.Text = $"Fetching events from {from.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)} " +
                          $"to {to.AddMinutes(-1).ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)}...";

        try {
            List<SubathonEvent> fetched = await Task.Run(() => _source.FetchMissedEventsAsync(from, to, ct), ct);
            List<Guid> ids = fetched.Select(ev => ev.Id).Distinct().ToList();

            await using AppDbContext db = await _factory.CreateDbContextAsync(ct);
            HashSet<(Guid, SubathonEventSource)> captured = (await db.SubathonEvents.AsNoTracking()
                    .Where(ev => ids.Contains(ev.Id))
                    .Select(ev => new { ev.Id, ev.Source })
                    .ToListAsync(ct))
                .Select(ev => (ev.Id, ev.Source))
                .ToHashSet();

            SubathonData? active = await db.SubathonDatas.AsNoTracking().FirstOrDefaultAsync(s => s.IsActive, ct);
            _hasActiveSubathon = active != null;
            _subathonStart = active == null
                ? null
                : await db.SubathonEvents.AsNoTracking()
                    .Where(ev => ev.SubathonId == active.Id)
                    .MinAsync(ev => (DateTime?)ev.EventTimestamp, ct);

            _all.Clear();
            foreach (SubathonEvent ev in fetched.DistinctBy(ev => (ev.Id, ev.Source)).OrderBy(ev => ev.EventTimestamp)) {
                var row = new ImportRow(ev, captured.Contains((ev.Id, ev.Source)),
                    _subathonStart != null && ev.EventTimestamp < _subathonStart);
                row.PropertyChanged += (_, args) => {
                    if (args.PropertyName == nameof(ImportRow.Selected)) UpdateSelectionState();
                };
                _all.Add(row);
            }

            int missed = _all.Count(r => !r.Captured);
            StatusText.Text = _all.Count == 0
                ? "No events found in that time range"
                : $"Found {_all.Count:N0} event(s), {missed:N0} not captured yet" +
                  (missed < _all.Count ? $" ({_all.Count - missed:N0} already captured are hidden unless shown)" : "");
            if (!_hasActiveSubathon) StatusText.Text += ". There's no active subathon to import into";
            ApplyFilter();
        }
        catch (OperationCanceledException) {
            StatusText.Text = "Fetch cancelled";
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "Failed to fetch missed events");
            StatusText.Text = $"Couldn't fetch events: {ex.Message}";
        }
        finally {
            FetchBtn.IsEnabled = true;
            UpdateSelectionState();
        }
    }

    private static List<FilterOption> BuildTypeOptions(SubathonEventSource source) {
        if (source == SubathonEventSource.GoAffPro)
            return GoAffProStoreRegistry.All()
                .OrderBy(s => s.StoreName, StringComparer.OrdinalIgnoreCase)
                .Select(s => {
                    string meta = s.SiteId.ToString(CultureInfo.InvariantCulture);
                    return new FilterOption {
                        Label = GoAffProOrderHelper.GetOrderLabel(meta),
                        Value = FilterKeyFor(SubathonEventType.GoAffProOrder, meta),
                        Group = nameof(SubathonEventSource.GoAffPro)
                    };
                })
                .ToList();

        return Enum.GetValues<SubathonEventType>()
            .Where(t => ((SubathonEventType?)t).GetSource() == source && !t.IsDisabled())
            .Select(t => new FilterOption {
                Label = ((SubathonEventType?)t).GetLabel(), Value = FilterKeyFor(t, null), Group = source.ToString()
            })
            .OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string FilterKeyFor(SubathonEventType? type, string? meta) {
        return type == SubathonEventType.GoAffProOrder ? $"{type}|{meta}" : $"{type}";
    }

    private void ApplyFilter() {
        HashSet<string> types = TypePopout.SelectedOptions.Select(o => o.Value).ToHashSet();
        bool showCaptured = ShowCapturedBox.IsChecked == true;

        _rows.Clear();
        foreach (ImportRow row in _all.Where(r =>
                     (showCaptured || !r.Captured) && (types.Count == 0 || types.Contains(r.FilterKey))))
            _rows.Add(row);
        UpdateSelectionState();
    }

    private void UpdateSelectionState() {
        List<ImportRow> selected = _rows.Where(r => r.Selected).ToList();
        int beforeStart = selected.Count(r => r.BeforeStart);

        SelectionText.Text = _all.Count == 0 ? "" : $"{selected.Count:N0} of {_rows.Count:N0} shown selected";
        WarningText.IsVisible = beforeStart > 0;
        WarningText.Text = beforeStart == 0 || _subathonStart == null
            ? ""
            : $"{beforeStart:N0} selected event(s) happened before this subathon started " +
              $"({_subathonStart.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)})";
        ImportBtn.IsEnabled = selected.Count > 0 && _hasActiveSubathon;
    }

    private void ShowCaptured_Changed(object? sender, RoutedEventArgs e) {
        ApplyFilter();
    }

    private void SelectAll_Click(object? sender, RoutedEventArgs e) {
        foreach (ImportRow row in _rows) row.Selected = true;
    }

    private void SelectNone_Click(object? sender, RoutedEventArgs e) {
        foreach (ImportRow row in _rows) row.Selected = false;
    }

    private async void Import_Click(object? sender, RoutedEventArgs e) {
        List<ImportRow> chosen = _rows.Where(r => r.Selected && !r.Captured).ToList();
        if (chosen.Count == 0 || !_hasActiveSubathon) return;

        int beforeStart = chosen.Count(r => r.BeforeStart);
        var dialog = new FAContentDialog {
            Title = $"Import {chosen.Count:N0} Event(s)",
            PrimaryButtonText = "Import",
            CloseButtonText = "Cancel",
            Content = new TextBlock {
                TextWrapping = TextWrapping.Wrap,
                Text = "They'll be added to the active subathon using your current values." +
                       (beforeStart > 0
                           ? $"\n\n{beforeStart:N0} of them happened before this subathon started"
                           : "")
            }
        };
        if (await dialog.ShowAsync() != FAContentDialogResult.Primary) return;

        foreach (ImportRow row in chosen) {
            SubathonEvents.RaiseSubathonEventCreated(row.Event);
            row.MarkImported();
        }

        _logger?.LogInformation("Imported {Count} missed event(s) from {Title}", chosen.Count, Title);
        StatusText.Text = $"Imported {chosen.Count:N0} event(s)";
        ApplyFilter();
    }

    public sealed class ImportRow(SubathonEvent ev, bool captured, bool beforeStart) : INotifyPropertyChanged {
        private bool _imported;
        private bool _selected;

        public SubathonEvent Event { get; } = ev;
        public bool Captured { get; private set; } = captured;
        public bool BeforeStart { get; } = beforeStart;
        public bool CanSelect => !Captured;
        public DateTime Timestamp => Event.EventTimestamp;
        public string TrueSource { get; } = ev.EventType.GetTypeTrueSource(ev.EventTypeMeta) ?? ev.Source.ToString();
        public string TypeLabel => Event.EventType.GetLabel();
        public string FilterKey => FilterKeyFor(Event.EventType, Event.EventTypeMeta);
        public string User => Event.User ?? "";
        public string DisplayValue => string.IsNullOrWhiteSpace(Event.Currency)
            ? Event.Value
            : $"{Event.Value} {Event.Currency}";
        public int Amount => Event.Amount;
        public string Details => Event.TertiaryValue;

        public string StatusText => _imported ? "Imported"
            : Captured ? "Already captured"
            : BeforeStart ? "Missed (before start)"
            : "Missed";

        public IBrush StatusBrush => Captured ? CapturedBrush : BeforeStart ? WarnBrush : MissedBrush;

        public bool Selected {
            get => _selected;
            set {
                if (_selected == value || (value && Captured)) return;
                _selected = value;
                Raise(nameof(Selected));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void MarkImported() {
            _imported = true;
            Captured = true;
            Selected = false;
            Raise(nameof(Captured));
            Raise(nameof(CanSelect));
            Raise(nameof(StatusText));
            Raise(nameof(StatusBrush));
        }

        private void Raise(string name) {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
