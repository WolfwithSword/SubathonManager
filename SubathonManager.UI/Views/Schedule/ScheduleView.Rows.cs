using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.UI.Controls;

namespace SubathonManager.UI.Views.Schedule;

public partial class ScheduleView {
    private void RenderRows() {
        ItemsStack.Children.Clear();
        foreach (ScheduleItem item in _dayItems)
            ItemsStack.Children.Add(BuildRow(item));
        NoItemsText.IsVisible = _dayItems.Count == 0;
        UpdateRowSelection();
    }

    private Border BuildRow(ScheduleItem item) {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*") };

        Border grip = CreateDragGrip();

        var check = new CheckBox {
            IsChecked = item.IsDone,
            MinWidth = 0,
            Margin = new Thickness(2, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(check, "Mark as done");

        bool isEvent = item.Kind == ScheduleItemKind.Event;
        var kindIcon = new SymIcon {
            Glyph = isEvent ? "CalendarToday20" : "TaskListSquare20",
            Opacity = 0.8,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(kindIcon, isEvent ? "Event" : "Task");

        var time = new TextBlock {
            Text = item.TimeLabel(),
            Width = 120,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "muted" }
        };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock {
            Text = DisplayTitle(item),
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        title.Classes.Add("title");
        text.Children.Add(title);
        string firstLine = item.Description.Split('\n', 2)[0].Trim();
        if (firstLine.Length > 0)
            text.Children.Add(new TextBlock {
                Text = firstLine,
                FontSize = 12,
                Opacity = 0.65,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

        Grid.SetColumn(grip, 0);
        Grid.SetColumn(check, 1);
        Grid.SetColumn(kindIcon, 2);
        Grid.SetColumn(time, 3);
        Grid.SetColumn(text, 4);
        grid.Children.Add(grip);
        grid.Children.Add(check);
        grid.Children.Add(kindIcon);
        grid.Children.Add(time);
        grid.Children.Add(text);

        var row = new Border { Child = grid, Tag = item };
        row.Classes.Add("schedrow");
        if (item.IsDone) row.Classes.Add("done");
        if (!string.IsNullOrWhiteSpace(item.Description)) ToolTip.SetTip(text, item.Description.Trim());

        row.ContextFlyout = BuildRowMenu(item);

        check.IsCheckedChanged += (_, _) => SetDone(item, row, check.IsChecked == true);
        row.Tapped += (_, e) => {
            if (IsWithin<CheckBox>(e.Source) || IsWithinGrip(e.Source)) return;
            CommitEditorIfDirty();
            ScheduleItem? current = _dayItems.FirstOrDefault(i => i.Id == item.Id);
            if (current == null) return;
            OpenEditor(current, false);
            ItemsListBorder.Focus();
        };

        grip.PointerPressed += (_, e) => BeginRowDrag(grip, row, e);
        grip.PointerMoved += (_, e) => RowDragMove(e);
        grip.PointerReleased += (_, e) => {
            e.Pointer.Capture(null);
            FinishRowDrag();
        };
        grip.PointerCaptureLost += (_, _) => FinishRowDrag();
        return row;
    }

    private MenuFlyout BuildRowMenu(ScheduleItem item) {
        var duplicate = new MenuItem { Header = "Duplicate", Icon = new SymIcon { Glyph = "Copy16" } };
        duplicate.Click += (_, _) => DuplicateItem(item.Id);

        var delete = new MenuItem { Header = "Delete", Icon = new SymIcon { Glyph = "Delete16" } };
        delete.Click += async (_, _) => {
            ScheduleItem? current = _dayItems.FirstOrDefault(i => i.Id == item.Id);
            if (current != null) await DeleteItemAsync(current);
        };

        return new MenuFlyout { Items = { duplicate, new Separator(), delete } };
    }

    private void DuplicateItem(Guid id) {
        CommitEditorIfDirty();

        ScheduleItem? source = _dayItems.FirstOrDefault(i => i.Id == id);
        if (source == null) return;

        var copy = new ScheduleItem {
            Date = source.Date,
            Kind = source.Kind,
            Title = source.Title,
            Description = source.Description,
            StartMinute = source.StartMinute,
            EndMinute = source.EndMinute
        };

        List<Guid> order = _dayItems.Select(i => i.Id).ToList();
        order.Insert(order.IndexOf(id) + 1, copy.Id);

        DateTime day = _selectedDate;
        DateTime next = day.AddDays(1);
        using (AppDbContext db = _factory.CreateDbContext()) {
            Dictionary<Guid, ScheduleItem> tracked = db.ScheduleItems
                .Where(i => i.Date >= day && i.Date < next)
                .ToDictionary(i => i.Id);
            tracked[copy.Id] = copy;
            db.ScheduleItems.Add(copy);
            for (var i = 0; i < order.Count; i++)
                if (tracked.TryGetValue(order[i], out ScheduleItem? t))
                    t.SortOrder = i;
            db.SaveChanges();
        }

        LoadDay();
        RenderCalendar();
        RenderUpcoming();
        NotifyScheduleChanged();

        ScheduleItem? created = _dayItems.FirstOrDefault(i => i.Id == copy.Id);
        if (created == null) return;
        OpenEditor(created, false);
        ItemsListBorder.Focus();
    }

    private static bool IsWithin<T>(object? source) where T : Visual {
        var v = source as Visual;
        while (v != null) {
            if (v is T) return true;
            v = v.GetVisualParent();
        }

        return false;
    }

    private static bool IsWithinGrip(object? source) {
        var v = source as Visual;
        while (v != null) {
            if (v is Border { Tag: "grip" }) return true;
            v = v.GetVisualParent();
        }

        return false;
    }

    private void UpdateRowSelection() {
        foreach (Border row in ItemsStack.Children.OfType<Border>()) {
            bool selected = _editing != null && !_editingIsNew && row.Tag is ScheduleItem i && i.Id == _editing.Id;
            SetClass(row, "selected", selected);
        }
    }

    private void SetDone(ScheduleItem item, Border row, bool done) {
        if (item.IsDone == done) return;
        item.IsDone = done;
        SetClass(row, "done", done);

        using (AppDbContext db = _factory.CreateDbContext()) {
            db.ScheduleItems.Where(i => i.Id == item.Id)
                .ExecuteUpdate(s => s.SetProperty(i => i.IsDone, done));
        }

        if (_editing != null && _editing.Id == item.Id) _editing.IsDone = done;
        UpdateDaySummary();
        RenderCalendar();
        RenderUpcoming();
        NotifyScheduleChanged();
    }

    private Border CreateDragGrip() {
        var grip = new Border {
            Width = 18,
            Tag = "grip",
            Margin = new Thickness(0, 0, 2, 0),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Stretch,
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new SymIcon {
                Glyph = "ReOrderDotsVertical20",
                Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        ToolTip.SetTip(grip, "Drag to reorder, or drop on a calendar day to move it there");
        return grip;
    }

    private List<Border> Rows() {
        return ItemsStack.Children.OfType<Border>().ToList();
    }

    private void BeginRowDrag(Border grip, Border row, PointerPressedEventArgs e) {
        if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
        List<Border> rows = Rows();
        int index = rows.IndexOf(row);
        if (index < 0) return;

        _dragRow = row;
        _dragStartIndex = index;
        row.Opacity = 0.6;
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void RowDragMove(PointerEventArgs e) {
        if (_dragRow == null) return;

        List<Border> rows = Rows();
        int current = rows.IndexOf(_dragRow);
        if (current < 0) return;

        Border? dayCell = DayCellAt(e);
        SetDropCell(dayCell);
        if (dayCell != null) {
            // over the calendar
            if (current != _dragStartIndex && _dragStartIndex < rows.Count)
                ItemsStack.Children.Move(current, _dragStartIndex);
            return;
        }

        if (rows.Count < 2) return;

        double y = e.GetPosition(ItemsStack).Y;
        int target = current;
        if (y <= rows[0].Bounds.Top)
            target = 0;
        else if (y >= rows[^1].Bounds.Bottom)
            target = rows.Count - 1;
        else
            for (var i = 0; i < rows.Count; i++) {
                if (y < rows[i].Bounds.Top || y > rows[i].Bounds.Bottom) continue;
                target = i;
                break;
            }

        if (target == current) return;
        ItemsStack.Children.Move(current, target);
    }

    private void FinishRowDrag() {
        if (_dragRow == null) return;

        Border row = _dragRow;
        _dragRow = null;
        row.Opacity = 1;

        int startIndex = _dragStartIndex;
        _dragStartIndex = -1;

        Border? dropCell = _dropCell;
        SetDropCell(null);
        if (dropCell != null) {
            if (dropCell.Tag is DateTime day && day != _selectedDate && row.Tag is ScheduleItem moving)
                MoveItemToDay(moving.Id, day);
            return;
        }

        List<ScheduleItem> ordered = Rows().Select(r => r.Tag).OfType<ScheduleItem>().ToList();
        if (ordered.IndexOf((ScheduleItem)row.Tag!) == startIndex) return;

        PersistOrder(ordered);
    }

    private Border? DayCellAt(PointerEventArgs e) {
        Point p = e.GetPosition(DayGrid);
        if (!new Rect(DayGrid.Bounds.Size).Contains(p)) return null;
        return DayGrid.Children.OfType<Border>().FirstOrDefault(c => c.Bounds.Contains(p));
    }

    private void SetDropCell(Border? cell) {
        if (ReferenceEquals(cell, _dropCell)) return;
        if (_dropCell != null) SetClass(_dropCell, "droptarget", false);
        _dropCell = cell;
        if (cell != null) SetClass(cell, "droptarget", true);
    }

    private void MoveItemToDay(Guid id, DateTime day) {
        CommitEditorIfDirty();

        using (AppDbContext db = _factory.CreateDbContext()) {
            ScheduleItem? tracked = db.ScheduleItems.Find(id);
            if (tracked == null || tracked.Date.Date == day) return;

            DateTime next = day.AddDays(1);
            int maxOrder = db.ScheduleItems
                .Where(i => i.Date >= day && i.Date < next)
                .Select(i => (int?)i.SortOrder)
                .Max() ?? -1;
            tracked.Date = day;
            tracked.SortOrder = maxOrder + 1;
            db.SaveChanges();
        }

        if (_editing?.Id == id) CloseEditor();
        LoadDay();
        RenderCalendar();
        RenderUpcoming();
        NotifyScheduleChanged();
    }

    private void PersistOrder(List<ScheduleItem> ordered) {
        DateTime day = _selectedDate;
        DateTime next = day.AddDays(1);
        using AppDbContext db = _factory.CreateDbContext();
        Dictionary<Guid, ScheduleItem> tracked = db.ScheduleItems
            .Where(i => i.Date >= day && i.Date < next)
            .ToDictionary(i => i.Id);

        for (var i = 0; i < ordered.Count; i++) {
            ordered[i].SortOrder = i;
            if (tracked.TryGetValue(ordered[i].Id, out ScheduleItem? t)) t.SortOrder = i;
        }

        db.SaveChanges();
        _dayItems = ordered;
        RenderCalendar();
        RenderUpcoming();
        NotifyScheduleChanged();
    }

    private void SortByTime_Click(object? sender, RoutedEventArgs e) {
        if (_dayItems.Count < 2) return;
        List<ScheduleItem> ordered = _dayItems
            .OrderBy(i => i.StartMinute.HasValue ? 1 : 0)
            .ThenBy(i => i.StartMinute ?? 0)
            .ThenBy(i => i.SortOrder)
            .ToList();
        PersistOrder(ordered);
        RenderRows();
    }
}