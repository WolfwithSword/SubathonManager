using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.UI.UiUtils;

namespace SubathonManager.UI.Views.Schedule;

public partial class ScheduleView : UserControl {
    private const int UpcomingLimit = 15;

    private readonly IDbContextFactory<AppDbContext> _factory;
    private List<ScheduleItem> _dayItems = [];
    private DateTime _displayMonth;
    private Border? _dragRow;
    private int _dragStartIndex = -1;
    private Border? _dropCell;
    private ScheduleItem? _editing;
    private bool _editingIsNew;
    private bool _initialized;
    private DateTime _selectedDate;
    private int _suppressCount;

    public ScheduleView() {
        _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        InitializeComponent();

        KindBox.ItemsSource = Enum.GetNames<ScheduleItemKind>().ToList();
        BuildWeekdayHeader();

        _selectedDate = DateTime.Today;
        _displayMonth = FirstOfMonth(_selectedDate);
        RefreshAll();

        ScheduleEvents.ScheduleChanged += OnScheduleChangedElsewhere;

        Loaded += (_, _) => {
            if (!_initialized) {
                _initialized = true;
                EnterKeyCommit.Attach(EditorPanel, () => SaveItem_Click(this, new RoutedEventArgs()));
                return;
            }

            RenderCalendar();
            RenderUpcoming();
        };
    }

    private static DayOfWeek FirstDayOfWeek => CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    public void ShowItem(DateTime date, Guid itemId) {
        SelectDate(date, itemId);
        Dispatcher.UIThread.Post(() => ItemsListBorder.Focus(), DispatcherPriority.Loaded);
    }

    private void NotifyScheduleChanged() {
        ScheduleEvents.RaiseScheduleChanged(this);
    }

    private void OnScheduleChangedElsewhere(object? source) {
        if (ReferenceEquals(source, this)) return;
        Dispatcher.UIThread.Post(() => {
            RenderCalendar();
            LoadDay();
            RenderUpcoming();
        });
    }

    private static DateTime FirstOfMonth(DateTime date) {
        return new DateTime(date.Year, date.Month, 1);
    }

    private void RefreshAll() {
        RenderCalendar();
        LoadDay();
        RenderUpcoming();
    }

    private void BuildWeekdayHeader() {
        string[] names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        WeekdayHeader.Children.Clear();
        for (var i = 0; i < 7; i++)
            WeekdayHeader.Children.Add(new TextBlock {
                Text = names[((int)FirstDayOfWeek + i) % 7],
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Classes = { "muted" }
            });
    }

    private void RenderCalendar() {
        MonthLabel.Text = _displayMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture);

        int offset = ((int)_displayMonth.DayOfWeek - (int)FirstDayOfWeek + 7) % 7;
        DateTime gridStart = _displayMonth.AddDays(-offset);
        DateTime gridEnd = gridStart.AddDays(42);

        Dictionary<DateTime, List<ScheduleItem>> byDay;
        using (AppDbContext db = _factory.CreateDbContext()) {
            byDay = db.ScheduleItems.AsNoTracking()
                .Where(i => i.Date >= gridStart && i.Date < gridEnd)
                .ToList()
                .GroupBy(i => i.Date.Date)
                .ToDictionary(g => g.Key, g => g.OrderBy(i => i.SortOrder).ToList());
        }

        DayGrid.Children.Clear();
        for (var i = 0; i < 42; i++) {
            DateTime day = gridStart.AddDays(i);
            byDay.TryGetValue(day, out List<ScheduleItem>? items);
            DayGrid.Children.Add(BuildDayCell(day, items ?? []));
        }
    }

    private Border BuildDayCell(DateTime day, List<ScheduleItem> items) {
        var content = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(4, 3) };
        content.Children.Add(new TextBlock {
            Text = day.Day.ToString(CultureInfo.CurrentCulture),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontWeight = day == DateTime.Today ? FontWeight.Bold : FontWeight.Normal
        });

        if (items.Count > 0) {
            int done = items.Count(i => i.IsDone);
            var indicator = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 3
            };
            var dot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
            dot.Classes.Add(done == items.Count ? "alldone" : "pending");
            indicator.Children.Add(dot);
            indicator.Children.Add(new TextBlock {
                Text = items.Count.ToString(CultureInfo.CurrentCulture),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetRow(indicator, 1);
            content.Children.Add(indicator);
        }

        var cell = new Border { Child = content, Tag = day };
        cell.Classes.Add("day");
        if (day.Month != _displayMonth.Month) cell.Classes.Add("othermonth");
        if (day == DateTime.Today) cell.Classes.Add("today");
        if (day == _selectedDate) cell.Classes.Add("selected");

        if (items.Count > 0) {
            IEnumerable<string> lines = items.Take(6).Select(i =>
                $"{(i.IsDone ? "✓ " : "")}{i.TimeLabel()}  {DisplayTitle(i)}");
            string tip = string.Join("\n", lines);
            if (items.Count > 6) tip += $"\n+{items.Count - 6} more";
            ToolTip.SetTip(cell, tip);
        }

        cell.Tapped += (_, _) => SelectDate(day);
        return cell;
    }

    private void SelectDate(DateTime date, Guid? openItemId = null) {
        CommitEditorIfDirty();
        date = date.Date;
        _selectedDate = date;
        _displayMonth = FirstOfMonth(date);
        CloseEditor();
        RenderCalendar();
        LoadDay();
        RenderUpcoming();

        if (openItemId == null) return;
        ScheduleItem? item = _dayItems.FirstOrDefault(i => i.Id == openItemId);
        if (item != null) OpenEditor(item, false);
    }

    private void PrevMonth_Click(object? sender, RoutedEventArgs e) {
        _displayMonth = _displayMonth.AddMonths(-1);
        RenderCalendar();
    }

    private void NextMonth_Click(object? sender, RoutedEventArgs e) {
        _displayMonth = _displayMonth.AddMonths(1);
        RenderCalendar();
    }

    private void Today_Click(object? sender, RoutedEventArgs e) {
        SelectDate(DateTime.Today);
    }

    private void LoadDay() {
        DateTime next = _selectedDate.AddDays(1);
        using (AppDbContext db = _factory.CreateDbContext()) {
            _dayItems = db.ScheduleItems.AsNoTracking()
                .Where(i => i.Date >= _selectedDate && i.Date < next)
                .OrderBy(i => i.SortOrder).ThenBy(i => i.CreatedAt)
                .ToList();
        }

        DayTitle.Text = _selectedDate.ToString("dddd, MMMM d, yyyy", CultureInfo.CurrentCulture);
        UpdateDaySummary();
        RenderRows();
    }

    private void UpdateDaySummary() {
        if (_dayItems.Count == 0) {
            DaySummary.Text = _selectedDate == DateTime.Today ? "Today" : "";
            return;
        }

        int events = _dayItems.Count(i => i.Kind == ScheduleItemKind.Event);
        int tasks = _dayItems.Count(i => i.Kind == ScheduleItemKind.Task);
        int done = _dayItems.Count(i => i.IsDone);
        var parts = new List<string>();
        if (_selectedDate == DateTime.Today) parts.Add("Today");
        parts.Add($"{events} event{(events == 1 ? "" : "s")}");
        parts.Add($"{tasks} task{(tasks == 1 ? "" : "s")}");
        parts.Add($"{done}/{_dayItems.Count} done");
        DaySummary.Text = string.Join("  -  ", parts);
    }

    private void RenderUpcoming() {
        DateTime today = DateTime.Today;
        List<ScheduleItem> upcoming;
        using (AppDbContext db = _factory.CreateDbContext()) {
            upcoming = db.ScheduleItems.AsNoTracking()
                .Where(i => i.Date >= today && !i.IsDone)
                .OrderBy(i => i.Date).ThenBy(i => i.SortOrder)
                .Take(UpcomingLimit)
                .ToList();
        }

        UpcomingStack.Children.Clear();
        if (upcoming.Count == 0) {
            UpcomingStack.Children.Add(new TextBlock {
                Text = "Nothing coming up.",
                FontSize = 12,
                Margin = new Thickness(2, 0),
                Classes = { "muted" }
            });
            return;
        }

        foreach (ScheduleItem item in upcoming) {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            string when = item.Date == today ? "Today"
                : item.Date == today.AddDays(1) ? "Tomorrow"
                : item.Date.ToString("ddd MMM d", CultureInfo.CurrentCulture);
            grid.Children.Add(new TextBlock {
                Text = $"{when}  {(item.IsAllDay ? "" : ScheduleItem.FormatMinute(item.StartMinute!.Value))}".Trim(),
                FontSize = 11,
                Width = 110,
                VerticalAlignment = VerticalAlignment.Center,
                Classes = { "muted" }
            });
            var title = new TextBlock {
                Text = DisplayTitle(item),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(title, 1);
            grid.Children.Add(title);

            var entry = new Border { Child = grid };
            entry.Classes.Add("upcoming");
            Guid id = item.Id;
            DateTime date = item.Date;
            entry.Tapped += (_, _) => SelectDate(date, id);
            UpcomingStack.Children.Add(entry);
        }
    }

    private static string DisplayTitle(ScheduleItem item) {
        return string.IsNullOrWhiteSpace(item.Title)
            ? $"(untitled {item.Kind.ToString().ToLowerInvariant()})"
            : item.Title;
    }

    private static void SetClass(StyledElement element, string name, bool on) {
        if (on && !element.Classes.Contains(name)) element.Classes.Add(name);
        else if (!on) element.Classes.Remove(name);
    }
}