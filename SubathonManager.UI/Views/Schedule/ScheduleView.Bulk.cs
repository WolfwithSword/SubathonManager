using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Models;
using SubathonManager.Data;

namespace SubathonManager.UI.Views.Schedule;

public partial class ScheduleView {
    private async void BulkDelete_Click(object? sender, RoutedEventArgs e) {
        CommitEditorIfDirty();

        int completed, total;
        await using (AppDbContext db = await _factory.CreateDbContextAsync()) {
            total = await db.ScheduleItems.CountAsync();
            completed = await db.ScheduleItems.CountAsync(i => i.IsDone);
        }

        if (total == 0) {
            await ShowMessageAsync("Delete Schedule Items", "There is nothing in the schedule to delete");
            return;
        }

        (BulkDeleteMode mode, DateTime before)? choice = await AskBulkDeleteAsync(completed, total);
        if (choice == null) return;
        (BulkDeleteMode mode, DateTime cutoff) = choice.Value;

        if (mode == BulkDeleteMode.Everything && !await ConfirmDeleteEverythingAsync(total)) return;

        int deleted;
        await using (AppDbContext db = await _factory.CreateDbContextAsync()) {
            IQueryable<ScheduleItem> query = FilterForBulkDelete(db.ScheduleItems, mode, cutoff);
            deleted = await query.ExecuteDeleteAsync();
        }

        CloseEditor();
        LoadDay();
        RenderCalendar();
        RenderUpcoming();
        NotifyScheduleChanged();

        await ShowMessageAsync("Delete Schedule Items",
            deleted == 0
                ? "Nothing matched, no items were deleted"
                : $"Deleted {deleted} item{(deleted == 1 ? "" : "s")}");
    }

    private static IQueryable<ScheduleItem> FilterForBulkDelete(IQueryable<ScheduleItem> items,
        BulkDeleteMode mode, DateTime cutoff) {
        return mode switch {
            BulkDeleteMode.Completed => items.Where(i => i.IsDone),
            BulkDeleteMode.Before => items.Where(i => i.Date < cutoff),
            _ => items
        };
    }

    private async Task<(BulkDeleteMode, DateTime)?> AskBulkDeleteAsync(int completed, int total) {
        const string group = "ScheduleBulkDelete";
        var completedRadio = new RadioButton {
            GroupName = group,
            Content = $"All completed items ({completed})",
            IsChecked = true
        };
        var beforeRadio = new RadioButton {
            GroupName = group,
            Content = "Everything before a date",
            Margin = new Thickness(0, 6, 0, 0)
        };
        var beforePicker = new DatePicker { SelectedDate = new DateTimeOffset(DateTime.Today) };
        var beforeCount = new TextBlock { FontSize = 12, Opacity = 0.7 };
        var beforePanel = new StackPanel { Margin = new Thickness(28, 8, 0, 0), IsEnabled = false, Spacing = 6 };
        beforePanel.Children.Add(Labelled("Before (the day itself is kept)", beforePicker));
        beforePanel.Children.Add(beforeCount);

        var everythingRadio = new RadioButton {
            GroupName = group,
            Content = $"Everything ({total})",
            Margin = new Thickness(0, 10, 0, 0)
        };

        async void UpdateBeforeCount() {
            if (beforePicker.SelectedDate is not { } picked) {
                beforeCount.Text = "";
                return;
            }

            DateTime cutoff = picked.Date;
            await using AppDbContext db = await _factory.CreateDbContextAsync();
            int count = await db.ScheduleItems.CountAsync(i => i.Date < cutoff);
            beforeCount.Text = $"{count} item{(count == 1 ? "" : "s")} before {cutoff:MMMM d, yyyy}";
        }

        beforeRadio.IsCheckedChanged += (_, _) => beforePanel.IsEnabled = beforeRadio.IsChecked == true;
        beforePicker.SelectedDateChanged += (_, _) => UpdateBeforeCount();
        UpdateBeforeCount();

        var body = new StackPanel { Width = 340 };
        body.Children.Add(new TextBlock { 
            Text = "This can't be undone. Export a backup if you want to save any schedule",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 12)
        });
        body.Children.Add(completedRadio);
        body.Children.Add(beforeRadio);
        body.Children.Add(beforePanel);
        body.Children.Add(everythingRadio);

        var dialog = new FAContentDialog {
            Title = "Delete Schedule Items",
            Content = body,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != FAContentDialogResult.Primary) return null;

        if (everythingRadio.IsChecked == true) return (BulkDeleteMode.Everything, DateTime.MinValue);
        if (beforeRadio.IsChecked != true) return (BulkDeleteMode.Completed, DateTime.MinValue);

        if (beforePicker.SelectedDate is not { } date) {
            await ShowMessageAsync("Delete Schedule Items", "Pick a date to delete before");
            return null;
        }

        return (BulkDeleteMode.Before, date.Date);
    }

    private static async Task<bool> ConfirmDeleteEverythingAsync(int total) {
        var dialog = new FAContentDialog {
            Title = "Delete everything?",
            PrimaryButtonText = $"Delete all {total}",
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Close,
            Content = new TextBlock {
                Text = $"All {total} schedule item{(total == 1 ? "" : "s")} will be permanently removed",
                TextWrapping = TextWrapping.Wrap,
                Width = 320,
                Margin = new Thickness(4)
            }
        };
        return await dialog.ShowAsync() == FAContentDialogResult.Primary;
    }

    private enum BulkDeleteMode {
        Completed,
        Before,
        Everything
    }
}