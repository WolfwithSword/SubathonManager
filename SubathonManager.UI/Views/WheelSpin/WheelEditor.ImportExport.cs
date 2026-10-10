using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.UI.UiUtils;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views.WheelSpin;

public partial class WheelEditor {
    
    private async void ExportWheel_Click(object? sender, RoutedEventArgs e) {
        if (_activeWheel == null) return;

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        WheelSet? wheel = await db.WheelSets
            .Include(w => w.WheelItems).ThenInclude(i => i.Action)
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == _activeWheel.Id);

        if (wheel == null) return;

        string path = await CsvUtils.ExportAsync(SafeFileName.Sanitize(wheel.Name, string.Empty, "wheel"),
            [["Text", "Weight", "Quantity", "Infinite", "Enabled", "ActionType", "ActionParameter"]],
            wheel.WheelItems.OrderBy(i => i.Index),
            item => [
                item.Text, item.Weight, item.Quantity, item.IsInfinite, item.Enabled,
                item.Action?.ActionType ?? WheelSpinActionType.Manual, item.Action?.Parameter
            ]);
        UiHelpers.OpenFolder(Path.GetDirectoryName(path));
    }

    private async void ImportWheel_Click(object? sender, RoutedEventArgs e) {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        IReadOnlyList<IStorageFile> picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = "Import Wheel",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("CSV Files") { Patterns = ["*.csv"] }]
        });
        if (picked.Count == 0) return;
        string filePath = picked[0].Path.LocalPath;

        List<string[]>? lines = await CsvUtils.ReadFileAsync(filePath);
        if (lines == null || lines.Count < 1 || lines[0].Length < 5) {
            await ShowInvalidWheelCsvPopup();
            return;
        }

        var items =
            new List<(string Text, int Weight, int Qty, bool Infinite, bool Enabled, 
                WheelSpinActionType ActionType, string ActionParam)>();

        foreach (string[] cols in lines.Skip(1)) {
            if (cols.Length < 5
                || !int.TryParse(cols[1].Trim(), out int weight)
                || !int.TryParse(cols[2].Trim(), out int qty)
                || !bool.TryParse(cols[3].Trim(), out bool infinite)
                || !bool.TryParse(cols[4].Trim(), out bool enabled)) {
                await ShowInvalidWheelCsvPopup();
                return;
            }

            string actionTypeStr = cols.Length > 5 ? cols[5].Trim() : "";
            var actionType = WheelSpinActionType.Manual;
            if (!string.IsNullOrEmpty(actionTypeStr) && !Enum.TryParse(actionTypeStr, out actionType)) {
                await ShowInvalidWheelCsvPopup();
                return;
            }

            string actionParam = cols.Length > 6 ? cols[6] : "";
            items.Add((cols[0], weight, qty, infinite, enabled, actionType, actionParam));
        }

        string wheelName = Path.GetFileNameWithoutExtension(filePath);

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        foreach (WheelSet w in db.WheelSets)
            w.IsActive = false;

        var newWheel = new WheelSet { Name = wheelName, IsActive = true };
        db.WheelSets.Add(newWheel);
        await db.SaveChangesAsync();

        for (var idx = 0; idx < items.Count; idx++) {
            (string text, int weight, int qty, bool infinite, bool enabled, WheelSpinActionType actionType,
                string actionParam) = items[idx];

            var newItem = new WheelItem {
                Text = text,
                Weight = weight,
                Quantity = qty,
                IsInfinite = infinite,
                Enabled = enabled,
                Index = idx,
                WheelId = newWheel.Id
            };
            db.WheelItems.Add(newItem);
            await db.SaveChangesAsync();

            db.WheelSpinActions.Add(new WheelSpinAction {
                ActionType = actionType,
                Parameter = actionParam,
                WheelItemId = newItem.Id
            });
        }

        await db.SaveChangesAsync();

        LoadActiveWheel();
    }

    private static async Task ShowInvalidWheelCsvPopup() {
        var dialog = new FAContentDialog {
            Title = "Invalid CSV",
            CloseButtonText = "OK",
            Content = new TextBlock {
                Text = "The selected file is not a valid wheel CSV and could not be imported",
                TextWrapping = TextWrapping.Wrap,
                Width = 300,
                Margin = new Thickness(4)
            }
        };
        await dialog.ShowAsync();
    }
}
