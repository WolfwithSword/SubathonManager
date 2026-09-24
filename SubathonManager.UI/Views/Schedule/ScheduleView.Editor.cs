using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.UI.UiUtils;

namespace SubathonManager.UI.Views.Schedule;

public partial class ScheduleView {
    private void AddEvent_Click(object? sender, RoutedEventArgs e) {
        StartNewItem(ScheduleItemKind.Event);
    }

    private void AddTask_Click(object? sender, RoutedEventArgs e) {
        StartNewItem(ScheduleItemKind.Task);
    }

    private void StartNewItem(ScheduleItemKind kind) {
        CommitEditorIfDirty();
        var item = new ScheduleItem {
            Date = _selectedDate,
            Kind = kind,
            StartMinute = kind == ScheduleItemKind.Event ? DefaultStartMinute() : null
        };
        OpenEditor(item, true);
        Dispatcher.UIThread.Post(() => TitleBox.Focus(), DispatcherPriority.Background);
    }

    private int DefaultStartMinute() {
        int? lastEnd = _dayItems.Where(i => i.StartMinute != null)
            .Select(i => i.EndMinute ?? i.StartMinute)
            .Max();
        if (lastEnd is { } end and < 23 * 60) return end;
        if (_selectedDate == DateTime.Today) {
            DateTime now = DateTime.Now.AddMinutes(30);
            return now.Date == DateTime.Today ? now.Hour * 60 : 12 * 60;
        }

        return 12 * 60;
    }

    private void OpenEditor(ScheduleItem item, bool isNew) {
        _editing = item;
        _editingIsNew = isNew;

        _suppressCount++;
        try {
            KindBox.SelectedItem = item.Kind.ToString();
            TitleBox.Text = item.Title;
            DescriptionBox.Text = item.Description;
            ItemDatePicker.SelectedDate = new DateTimeOffset(item.Date);
            AllDayCheck.IsChecked = item.IsAllDay;
            StartTimeBox.Text = item.StartMinute is { } s ? ScheduleItem.FormatMinute(s) : "";
            EndTimeBox.Text = item.EndMinute is { } en ? ScheduleItem.FormatMinute(en) : "";
            TimePanel.IsEnabled = !item.IsAllDay;
        }
        finally {
            _suppressCount--;
        }

        DeleteItemBtn.IsVisible = !isNew;
        EditorValidationMsg.Text = "";
        SaveItemBtn.Content = isNew ? "Add" : "Save";
        EditorEmptyText.IsVisible = false;
        EditorPanel.IsVisible = true;
        UiHelpers.UpdateButtonPendingBorder(SaveButtonBorder, isNew);
        UpdateRowSelection();
    }

    private void CloseEditor() {
        _editing = null;
        _editingIsNew = false;
        EditorPanel.IsVisible = false;
        EditorEmptyText.IsVisible = true;
        UiHelpers.UpdateButtonPendingBorder(SaveButtonBorder, false);
        UpdateRowSelection();
    }

    private void CloseEditor_Click(object? sender, RoutedEventArgs e) {
        CloseEditor();
    }

    private void EditorChanged() {
        if (_suppressCount > 0 || _editing == null) return;
        EditorValidationMsg.Text = "";
        UiHelpers.UpdateButtonPendingBorder(SaveButtonBorder, _editingIsNew || IsEditorDirty());
    }

    private void EditorText_Changed(object? sender, TextChangedEventArgs e) {
        EditorChanged();
    }

    private void Kind_Changed(object? sender, SelectionChangedEventArgs e) {
        EditorChanged();
    }
    private void ItemDate_Changed(object? sender, DatePickerSelectedValueChangedEventArgs e) {
        EditorChanged();
    }

    private void AllDay_Changed(object? sender, RoutedEventArgs e) {
        TimePanel.IsEnabled = AllDayCheck.IsChecked != true;
        if (_suppressCount == 0 && AllDayCheck.IsChecked != true && string.IsNullOrWhiteSpace(StartTimeBox.Text))
            StartTimeBox.Text = ScheduleItem.FormatMinute(DefaultStartMinute());
        EditorChanged();
    }

    private void TimeBox_LostFocus(object? sender, RoutedEventArgs e) {
        if (sender is not TextBox box) return;
        if (ScheduleItem.TryParseTime(box.Text, out int? minute) && minute is { } m)
            box.Text = ScheduleItem.FormatMinute(m);
    }

    private bool TryReadEditor(out ScheduleItem values, out string error) {
        values = new ScheduleItem();
        error = "";

        if (ItemDatePicker.SelectedDate is not { } picked) {
            error = "Pick a date.";
            return false;
        }

        values.Date = picked.Date;
        values.Kind = Enum.TryParse($"{KindBox.SelectedItem}", out ScheduleItemKind kind)
            ? kind
            : ScheduleItemKind.Event;
        values.Title = (TitleBox.Text ?? "").Trim();
        values.Description = (DescriptionBox.Text ?? "").TrimEnd();

        if (values.Title.Length == 0) {
            error = "Give it a title.";
            return false;
        }

        if (AllDayCheck.IsChecked == true) return true;

        if (!ScheduleItem.TryParseTime(StartTimeBox.Text, out int? start)) {
            error = "Start time must be HH:MM (00:00 - 23:59)";
            return false;
        }

        if (start == null) {
            error = "Set a start time, or tick \"On the day\"";
            return false;
        }

        if (!ScheduleItem.TryParseTime(EndTimeBox.Text, out int? end)) {
            error = "End time must be HH:MM (00:00 - 23:59), or left blank";
            return false;
        }

        if (end == start) end = null;
        values.StartMinute = start;
        values.EndMinute = end;
        return true;
    }

    private bool IsEditorDirty() {
        if (_editing == null) return false;
        if (_editingIsNew)
            return !string.IsNullOrWhiteSpace(TitleBox.Text) || !string.IsNullOrWhiteSpace(DescriptionBox.Text);

        if (!TryReadEditor(out ScheduleItem v, out _)) return true;
        return v.Date != _editing.Date.Date || v.Kind != _editing.Kind || v.Title != _editing.Title ||
               v.Description != _editing.Description.TrimEnd() || v.StartMinute != _editing.StartMinute ||
               v.EndMinute != _editing.EndMinute;
    }

    private void CommitEditorIfDirty() {
        if (_editing == null || !IsEditorDirty()) return;
        if (TryReadEditor(out _, out _)) SaveEditor(false);
    }

    private async void SaveItem_Click(object? sender, RoutedEventArgs e) {
        if (_editing == null) return;
        if (!SaveEditor(true)) return;

        SaveItemBtn.Content = "Saved!";
        await Task.Delay(1200);
        if (_editing != null && !_editingIsNew) SaveItemBtn.Content = "Save";
    }

    private bool SaveEditor(bool reopen) {
        if (_editing == null) return false;
        if (!TryReadEditor(out ScheduleItem v, out string error)) {
            EditorValidationMsg.Text = error;
            return false;
        }

        ScheduleItem saved;
        using (AppDbContext db = _factory.CreateDbContext()) {
            ScheduleItem? target = _editingIsNew ? null : db.ScheduleItems.Find(_editing.Id);
            bool isNew = target == null;
            target ??= new ScheduleItem { Id = _editing.Id, IsDone = _editing.IsDone, CreatedAt = DateTime.Now };

            if (isNew || target.Date.Date != v.Date) {
                DateTime next = v.Date.AddDays(1);
                int maxOrder = db.ScheduleItems
                    .Where(i => i.Date >= v.Date && i.Date < next && i.Id != target.Id)
                    .Select(i => (int?)i.SortOrder)
                    .Max() ?? -1;
                target.SortOrder = maxOrder + 1;
            }

            target.Date = v.Date;
            target.Kind = v.Kind;
            target.Title = v.Title;
            target.Description = v.Description;
            target.StartMinute = v.StartMinute;
            target.EndMinute = v.EndMinute;

            if (isNew) db.ScheduleItems.Add(target);
            db.SaveChanges();
            saved = target;
        }

        UiHelpers.UpdateButtonPendingBorder(SaveButtonBorder, false);

        if (!reopen) {
            _editing = null;
            _editingIsNew = false;
            LoadDay();
            RenderCalendar();
            RenderUpcoming();
            NotifyScheduleChanged();
            return true;
        }

        if (saved.Date != _selectedDate) {
            _editing = null;
            SelectDate(saved.Date, saved.Id);
            NotifyScheduleChanged();
            return true;
        }

        LoadDay();
        RenderCalendar();
        RenderUpcoming();
        NotifyScheduleChanged();
        ScheduleItem? reloaded = _dayItems.FirstOrDefault(i => i.Id == saved.Id);
        if (reloaded != null) OpenEditor(reloaded, false);
        return true;
    }

    private async void DeleteItem_Click(object? sender, RoutedEventArgs e) {
        await DeleteSelectedAsync();
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Delete || e.KeyModifiers != KeyModifiers.None) return;
        if (_editing == null || _editingIsNew) return;
        if (IsWithin<TextBox>(e.Source) || IsWithin<ComboBox>(e.Source) || IsWithin<DatePicker>(e.Source)) return;

        e.Handled = true;
        _ = DeleteSelectedAsync();
    }

    private async Task DeleteSelectedAsync() {
        if (_editing == null) return;
        if (_editingIsNew) {
            CloseEditor();
            return;
        }

        await DeleteItemAsync(_editing);
    }

    private async Task DeleteItemAsync(ScheduleItem item) {
        if (!await ConfirmDeleteAsync(item)) return;

        await using (AppDbContext db = await _factory.CreateDbContextAsync()) {
            await db.ScheduleItems.Where(i => i.Id == item.Id).ExecuteDeleteAsync();
        }

        if (_editing?.Id == item.Id) CloseEditor();
        LoadDay();
        RenderCalendar();
        RenderUpcoming();
        NotifyScheduleChanged();
    }

    private async Task<bool> ConfirmDeleteAsync(ScheduleItem item) {
        bool skip;
        await using (AppDbContext db = await _factory.CreateDbContextAsync()) {
            skip = StateValueHelper.Get(db, StateKeys.ScheduleSkipDeleteConfirm, false);
        }

        if (skip) return true;

        var skipBox = new CheckBox {
            Content = "Don't ask again",
            Margin = new Thickness(0, 14, 0, 0)
        };

        var body = new StackPanel { Width = 320, Margin = new Thickness(4) };
        body.Children.Add(new TextBlock {
            Text = $"\"{DisplayTitle(item)}\" will be removed from {item.Date:MMMM d}",
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(skipBox);

        var dialog = new FAContentDialog {
            Title = $"Delete {item.Kind.ToString().ToLowerInvariant()}?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Close,
            Content = body
        };
        if (await dialog.ShowAsync() != FAContentDialogResult.Primary) return false;

        if (skipBox.IsChecked == true)
            await StateValueHelper.SetAsync(_factory, StateKeys.ScheduleSkipDeleteConfirm, true);

        return true;
    }
}