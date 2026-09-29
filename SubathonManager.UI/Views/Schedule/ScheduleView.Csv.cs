using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core;
using SubathonManager.Core.Models;
using SubathonManager.Data;

namespace SubathonManager.UI.Views.Schedule;

public partial class ScheduleView {
    private const int MaxErrorsShown = 8;

    private async void ExportCsv_Click(object? sender, RoutedEventArgs e) {
        CommitEditorIfDirty();

        int total;
        await using (AppDbContext db = await _factory.CreateDbContextAsync()) {
            total = await db.ScheduleItems.CountAsync();
        }

        if (total == 0) {
            await ShowMessageAsync("Export Schedule", "There is nothing in the schedule to export");
            return;
        }

        (DateTime from, DateTime to)? range = await AskExportRangeAsync(total);
        if (range == null) return;
        bool everything = range.Value.from == DateTime.MinValue;

        List<ScheduleItem> items;
        await using (AppDbContext db = await _factory.CreateDbContextAsync()) {
            IQueryable<ScheduleItem> query = db.ScheduleItems.AsNoTracking();
            if (!everything) {
                DateTime start = range.Value.from;
                DateTime end = range.Value.to.AddDays(1);
                query = query.Where(i => i.Date >= start && i.Date < end);
            }

            items = await query.OrderBy(i => i.Date).ThenBy(i => i.SortOrder).ThenBy(i => i.CreatedAt).ToListAsync();
        }

        if (items.Count == 0) {
            await ShowMessageAsync("Export Schedule", "Nothing is planned in that date range");
            return;
        }

        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        string suggested = everything
            ? "schedule-all"
            : range.Value.from == range.Value.to
                ? $"schedule-{range.Value.from:yyyy-MM-dd}"
                : $"schedule-{range.Value.from:yyyy-MM-dd}-to-{range.Value.to:yyyy-MM-dd}";

        string exportDir = Path.Combine(Config.DataFolder, "exports");
        Directory.CreateDirectory(exportDir);
        IStorageFolder? startFolder = await top.StorageProvider.TryGetFolderFromPathAsync(exportDir);

        IStorageFile? picked = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
            Title = "Export Schedule",
            SuggestedFileName = suggested,
            DefaultExtension = "csv",
            SuggestedStartLocation = startFolder,
            FileTypeChoices = [new FilePickerFileType("CSV Files") { Patterns = ["*.csv"] }]
        });
        if (picked == null) return;

        string csv = ScheduleCsv.Write(items);
        try {
            if (picked.TryGetLocalPath() is { } path) {
                await File.WriteAllTextAsync(path, csv, new UTF8Encoding(true));
            }
            else {
                await using Stream stream = await picked.OpenWriteAsync();
                stream.SetLength(0);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
                await writer.WriteAsync(csv);
            }
        }
        catch (Exception ex) {
            await ShowMessageAsync("Export Schedule", $"Could not write the file.\n\n{ex.Message}");
        }
    }

    private async Task<(DateTime from, DateTime to)?> AskExportRangeAsync(int total) {
        DateTime monthStart = _displayMonth;
        DateTime monthEnd = _displayMonth.AddMonths(1).AddDays(-1);

        var allRadio = new RadioButton {
            GroupName = "ScheduleExportRange",
            Content = $"Everything ({total} item{(total == 1 ? "" : "s")})",
            IsChecked = true
        };
        var rangeRadio = new RadioButton {
            GroupName = "ScheduleExportRange",
            Content = "Date range",
            Margin = new Thickness(0, 6, 0, 0)
        };
        var fromPicker = new DatePicker { SelectedDate = new DateTimeOffset(monthStart) };
        var toPicker = new DatePicker { SelectedDate = new DateTimeOffset(monthEnd) };
        var countText = new TextBlock { FontSize = 12, Margin = new Thickness(0, 10, 0, 0), Opacity = 0.7 };

        var rangePanel = new StackPanel { Margin = new Thickness(28, 8, 0, 0), IsEnabled = false, Spacing = 6 };
        rangePanel.Children.Add(Labelled("From", fromPicker));
        rangePanel.Children.Add(Labelled("To", toPicker));
        rangePanel.Children.Add(countText);

        async void UpdateCount() {
            if (rangeRadio.IsChecked != true || fromPicker.SelectedDate is not { } f ||
                toPicker.SelectedDate is not { } t) {
                countText.Text = "";
                return;
            }

            (DateTime start, DateTime end) = OrderedRange(f.Date, t.Date);
            DateTime endExclusive = end.AddDays(1);
            await using AppDbContext db = await _factory.CreateDbContextAsync();
            int count = await db.ScheduleItems.CountAsync(i => i.Date >= start && i.Date < endExclusive);
            countText.Text = $"{count} item{(count == 1 ? "" : "s")} in range";
        }

        rangeRadio.IsCheckedChanged += (_, _) => {
            rangePanel.IsEnabled = rangeRadio.IsChecked == true;
            UpdateCount();
        };
        fromPicker.SelectedDateChanged += (_, _) => UpdateCount();
        toPicker.SelectedDateChanged += (_, _) => UpdateCount();

        var body = new StackPanel { Width = 340 };
        body.Children.Add(allRadio);
        body.Children.Add(rangeRadio);
        body.Children.Add(rangePanel);

        var dialog = new FAContentDialog {
            Title = "Export Schedule",
            Content = body,
            PrimaryButtonText = "Export",
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != FAContentDialogResult.Primary) return null;

        if (rangeRadio.IsChecked != true) return (DateTime.MinValue, DateTime.MaxValue);
        if (fromPicker.SelectedDate is not { } from || toPicker.SelectedDate is not { } to) {
            await ShowMessageAsync("Export Schedule", "Pick both a start and an end date");
            return null;
        }

        return OrderedRange(from.Date, to.Date);
    }

    private static (DateTime, DateTime) OrderedRange(DateTime a, DateTime b) {
        return a <= b ? (a, b) : (b, a);
    }

    private static StackPanel Labelled(string label, Control control) {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock {
            Text = label,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 3),
            Opacity = 0.7
        });
        panel.Children.Add(control);
        return panel;
    }

    private async void ImportCsv_Click(object? sender, RoutedEventArgs e) {
        CommitEditorIfDirty();

        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        IReadOnlyList<IStorageFile> picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = "Import Schedule",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("CSV Files") { Patterns = ["*.csv"] }]
        });
        if (picked.Count == 0) return;

        string text;
        try {
            await using Stream stream = await picked[0].OpenReadAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            text = await reader.ReadToEndAsync();
        }
        catch (Exception ex) {
            await ShowMessageAsync("Import Schedule", $"Could not read the file.\n\n{ex.Message}");
            return;
        }

        ScheduleCsv.ParseResult parsed = ScheduleCsv.Parse(text);
        if (parsed.Items.Count == 0) {
            string reason = parsed.Errors.Count > 0
                ? FormatErrors(parsed.Errors)
                : "The file has no schedule rows.";
            await ShowMessageAsync("Import Schedule", $"Nothing was imported.\n\n{reason}");
            return;
        }

        ScheduleCsv.ImportPlan plan;
        await using (AppDbContext db = await _factory.CreateDbContextAsync()) {
            DateTime minDate = parsed.Items.Min(i => i.Date);
            DateTime maxDate = parsed.Items.Max(i => i.Date).AddDays(1);
            List<ScheduleItem> existing = await db.ScheduleItems
                .Where(i => i.Date >= minDate && i.Date < maxDate)
                .OrderBy(i => i.Date).ThenBy(i => i.SortOrder)
                .ToListAsync();

            plan = ScheduleCsv.PlanImport(existing, parsed.Items);
            db.ScheduleItems.AddRange(plan.ToAdd);
            await db.SaveChangesAsync();
        }

        CloseEditor();
        LoadDay();
        RenderCalendar();
        RenderUpcoming();
        NotifyScheduleChanged();

        var summary = new StringBuilder();
        summary.AppendLine($"Added: {plan.ToAdd.Count}");
        summary.AppendLine($"Descriptions updated: {plan.ToUpdate.Count}");
        summary.AppendLine($"Already up to date: {plan.Unchanged}");
        if (parsed.Errors.Count > 0) {
            summary.AppendLine($"Skipped rows: {parsed.Errors.Count}");
            summary.AppendLine();
            summary.Append(FormatErrors(parsed.Errors));
        }

        await ShowMessageAsync("Import Schedule", summary.ToString().TrimEnd());
    }

    private static string FormatErrors(List<string> errors) {
        string shown = string.Join("\n", errors.Take(MaxErrorsShown));
        return errors.Count > MaxErrorsShown ? $"{shown}\n...and {errors.Count - MaxErrorsShown} more" : shown;
    }

    private static async Task ShowMessageAsync(string title, string message) {
        var dialog = new FAContentDialog {
            Title = title,
            CloseButtonText = "OK",
            Content = new TextBlock {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Width = 340,
                Margin = new Thickness(4)
            }
        };
        await dialog.ShowAsync();
    }
}