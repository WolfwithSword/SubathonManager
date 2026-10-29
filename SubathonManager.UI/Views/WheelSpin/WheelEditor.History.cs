using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.UI.Controls;
using SubathonManager.UI.Services;
using SubathonManager.UI.UiUtils;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views.WheelSpin;

public partial class WheelEditor {
    private async Task LoadHistoryAsync(bool append = false) {
        if (_historyLoading) return;
        _historyLoading = true;
        try {
            if (!append) {
                _historyOffset = 0;
                await Dispatcher.UIThread.InvokeAsync(() => HistoryStack.Children.Clear());
            }

            if (_activeWheel == null) return;

            await using AppDbContext db = await _factory.CreateDbContextAsync();
            List<WheelSpinHistory> entries = await db.WheelSpinHistories
                .Include(h => h.LinkedItem).ThenInclude(i => i!.Action)
                .Where(h => h.WheelId == _activeWheel.Id
                            && (_historyFilter == null || h.Status == _historyFilter))
                .OrderByDescending(h => h.CreatedAt)
                .Skip(_historyOffset)
                .Take(HistoryPageSize)
                .AsNoTracking()
                .ToListAsync();

            _historyOffset += entries.Count;

            await Dispatcher.UIThread.InvokeAsync(() => {
                foreach (WheelSpinHistory entry in entries)
                    HistoryStack.Children.Add(BuildHistoryRow(entry));
            });
        }
        finally {
            _historyLoading = false;
        }
    }

    private void PrependHistoryRow(WheelSpinHistory h) {
        _historyOffset++;
        HistoryStack.Children.Insert(0, BuildHistoryRow(h));
    }

    private Grid BuildHistoryRow(WheelSpinHistory h) {
        var row = new Grid {
            Margin = new Thickness(2, 1, 2, 1),
            MinHeight = 26,
            Tag = h,
            ColumnDefinitions = new ColumnDefinitions("78,1.7*,*,70,80")
        };

        var tsLabel = new TextBlock {
            Text = h.CreatedAt.ToString("MM/dd HH:mm"),
            FontSize = 10,
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(tsLabel, h.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));

        var itemLabel = new TextBlock {
            Text = h.LinkedItem?.Text ?? "(deleted)",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0, 4, 0)
        };
        ToolTip.SetTip(itemLabel, h.LinkedItem?.Text);

        var statusLabel = new TextBlock {
            Text = h.Status.ToString(),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = HistoryStatusBrush(h.Status),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var hoverBtns = new StackPanel {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 12, 0)
        };

        void AddBtn(WheelSpinHistoryStatus target, string glyph, string tip) {
            var btn = new Button {
                Content = new SymIcon { Glyph = glyph },
                Width = 22, Height = 22,
                Padding = new Thickness(1),
                Margin = new Thickness(1, 0, 0, 0),
                IsEnabled = h.Status != target,
                Tag = target
            };
            ToolTip.SetTip(btn, tip);
            btn.Click += async (_, _) => await SetHistoryStatus(h, target);
            hoverBtns.Children.Add(btn);
        }

        AddBtn(WheelSpinHistoryStatus.Done, "Checkmark16", "Mark Done");
        AddBtn(WheelSpinHistoryStatus.Pending, "Clock16", "Mark Pending");
        AddBtn(WheelSpinHistoryStatus.Cancelled, "Dismiss16", "Mark Cancelled");

        WheelSpinActionType? actionType = h.LinkedItem?.Action?.ActionType;
        bool hasPlayBtn = actionType.HasValue && actionType.Value.HasPlayAction();

        string actionStr = DescribeHistoryAction(h.LinkedItem?.Action);

        var actionCell = new Grid {
            Margin = new Thickness(4, 0, 2, 0),
            ColumnDefinitions = hasPlayBtn ? new ColumnDefinitions("*,Auto") : new ColumnDefinitions("*")
        };

        var actionLabel = new TextBlock {
            Text = actionStr,
            FontSize = 10,
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ToolTip.SetTip(actionLabel, actionStr);

        Grid.SetColumn(actionLabel, 0);
        actionCell.Children.Add(actionLabel);

        if (hasPlayBtn) {
            bool isMultiplier = actionType == WheelSpinActionType.SetMultiplier;
            bool isVts = actionType is WheelSpinActionType.VTubeStudio or WheelSpinActionType.OBS;
            var playBtn = new Button {
                Content = new SymIcon { Glyph = "Play16" },
                Width = 20, Height = 20,
                Padding = new Thickness(1),
                Margin = new Thickness(3, 0, 0, 0),
                IsEnabled = !isMultiplier || !_multiplierActive,
                IsVisible = h.Status == WheelSpinHistoryStatus.Pending,
                Tag = isMultiplier ? "MultiplierPlayBtn" : null
            };
            ToolTip.SetTip(playBtn, isMultiplier
                ? "Apply multiplier (disabled while one is active)"
                : isVts
                    ? $"Run the {actionType?.GetLabel()} action, or resuming where it stopped"
                    : "Apply command");
            playBtn.Click += async (_, _) => await ExecuteHistoryAction(h, playBtn);
            Grid.SetColumn(playBtn, 1);
            actionCell.Children.Add(playBtn);
        }

        Grid.SetColumn(tsLabel, 0);
        Grid.SetColumn(itemLabel, 1);
        Grid.SetColumn(actionCell, 2);
        Grid.SetColumn(statusLabel, 3);
        Grid.SetColumn(hoverBtns, 4);

        row.Children.Add(tsLabel);
        row.Children.Add(itemLabel);
        row.Children.Add(actionCell);
        row.Children.Add(statusLabel);
        row.Children.Add(hoverBtns);

        return row;
    }

    private static IBrush HistoryStatusBrush(WheelSpinHistoryStatus status) {
        return status switch {
            WheelSpinHistoryStatus.Done => Brushes.MediumSeaGreen,
            WheelSpinHistoryStatus.Pending => Brushes.CornflowerBlue,
            WheelSpinHistoryStatus.Running => Brushes.Goldenrod,
            WheelSpinHistoryStatus.Cancelled => Brushes.IndianRed,
            _ => Brushes.Gray
        };
    }

    private async Task SetHistoryStatus(WheelSpinHistory h, WheelSpinHistoryStatus newStatus) {
        h.Status = newStatus;
        h.UpdatedAt = DateTime.Now;

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        WheelSpinHistory? tracked = await db.WheelSpinHistories.FindAsync(h.Id);
        if (tracked == null) return;
        tracked.Status = newStatus;
        tracked.UpdatedAt = h.UpdatedAt;
        await db.SaveChangesAsync();

        h.LinkedWheel ??= _activeWheel;
        WheelEvents.RaiseWheelSpinStatusChanged(h, _spinsOwed);
    }

    private void OnWheelSpinStatusChanged(WheelSpinHistory history, int spinsOwed) {
        Dispatcher.UIThread.Post(() => {
            Grid? row = HistoryStack.Children.OfType<Grid>()
                .FirstOrDefault(g => g.Tag is WheelSpinHistory h && h.Id == history.Id);
            if (row == null) return;

            if (row.Tag is WheelSpinHistory rowHistory)
                rowHistory.Status = history.Status;

            TextBlock? statusLabel = row.Children.OfType<TextBlock>()
                .FirstOrDefault(c => Grid.GetColumn(c) == 3);
            StackPanel? hoverBtns = row.Children.OfType<StackPanel>()
                .FirstOrDefault(c => Grid.GetColumn(c) == 4);

            if (statusLabel != null) {
                statusLabel.Text = history.Status.ToString();
                statusLabel.Foreground = HistoryStatusBrush(history.Status);
            }

            if (hoverBtns != null)
                foreach (Button btn in hoverBtns.Children.OfType<Button>())
                    btn.IsEnabled = btn.Tag is WheelSpinHistoryStatus s && s != history.Status;

            Grid? actionCell = row.Children.OfType<Grid>().FirstOrDefault(c => Grid.GetColumn(c) == 2);
            if (actionCell == null) return;
            bool pending = history.Status == WheelSpinHistoryStatus.Pending;
            foreach (Button playBtn in actionCell.Children.OfType<Button>()) {
                playBtn.IsVisible = pending;
                playBtn.IsEnabled = pending && (playBtn.Tag?.ToString() != "MultiplierPlayBtn" || !_multiplierActive);
            }
        });
    }

    private void RaiseWheelDataChanged() {
        if (_activeWheel == null) return;
        WheelEvents.RaiseWheelDataChanged(_activeWheel, _spinsOwed);
    }

    private void OnSubathonDataUpdate(SubathonData data, DateTime _) {
        _multiplierActive = data.Multiplier?.IsRunning() ?? false;

        if (Interlocked.CompareExchange(ref _multiplierRefreshQueued, 1, 0) != 0) return;
        Dispatcher.UIThread.Post(() => {
            Interlocked.Exchange(ref _multiplierRefreshQueued, 0);
            RefreshMultiplierButtons(_multiplierActive);
        }, DispatcherPriority.Background);
    }

    private void RefreshMultiplierButtons(bool multiplierActive) {
        foreach (Grid row in HistoryStack.Children.OfType<Grid>()) {
            if (row.Tag is not WheelSpinHistory h) continue;
            bool isPending = h.Status == WheelSpinHistoryStatus.Pending;
            foreach (Grid cell in row.Children.OfType<Grid>())
            foreach (Button btn in cell.Children.OfType<Button>())
                if (btn.Tag?.ToString() == "MultiplierPlayBtn")
                    btn.IsEnabled = isPending && !multiplierActive;
        }
    }

    private async Task ExecuteHistoryAction(WheelSpinHistory h, Button playBtn) {
        await Dispatcher.UIThread.InvokeAsync(() => {
            playBtn.IsEnabled = false;
            playBtn.IsVisible = false;
        });

        WheelSpinActionType? actionType = h.LinkedItem?.Action?.ActionType;
        if (actionType == null) return;

        ActionRunResult result = await ServiceManager.Actions.RunWheelSpinAsync(h.Id);
        if (result == ActionRunResult.Skipped && !ServiceManager.Actions.IsRunning(h.Id))
            await Dispatcher.UIThread.InvokeAsync(() => {
                playBtn.IsVisible = h.Status == WheelSpinHistoryStatus.Pending;
                playBtn.IsEnabled = playBtn.IsVisible;
            });

        if (actionType == WheelSpinActionType.SetMultiplier && result == ActionRunResult.Done) {
            _multiplierActive = true;
            await Dispatcher.UIThread.InvokeAsync(() => RefreshMultiplierButtons(true));
        }
    }

    private void HistoryFilter_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        if (_activeWheel == null) return;
        var selection =  (HistoryFilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (Enum.TryParse(selection ?? "", out WheelSpinHistoryStatus statusEnum))
            _historyFilter = statusEnum;
        else 
            _historyFilter = null;
        _ = LoadHistoryAsync();
    }

    private void HistoryScroller_ScrollChanged(object? sender, ScrollChangedEventArgs e) {
        if (_historyLoading) return;
        double scrollable = HistoryScroller.Extent.Height - HistoryScroller.Viewport.Height;
        if (scrollable > 0 && scrollable - HistoryScroller.Offset.Y < 100)
            _ = LoadHistoryAsync(true);
    }

    private async void ExportHistory_Click(object? sender, RoutedEventArgs e) {
        await using AppDbContext db = await _factory.CreateDbContextAsync();
        List<WheelSpinHistory> histories = await db.WheelSpinHistories
            .Include(h => h.LinkedWheel)
            .Include(h => h.LinkedItem).ThenInclude(i => i!.Action)
            .OrderByDescending(h => h.CreatedAt)
            .AsNoTracking()
            .ToListAsync();

        string path = await CsvUtils.ExportAsync("wheel-history",
            [[
                "Id", "Wheel Id", "Wheel Name", "Item Id", "Item Text", "Action Type", "Parameter", "Status",
                "Created At", "Updated At"
            ]],
            histories,
            h => [
                h.Id, h.WheelId, h.LinkedWheel?.Name, h.WheelItemId, h.LinkedItem?.Text,
                h.LinkedItem?.Action?.ActionType.ToString() ?? "Manual", h.LinkedItem?.Action?.Parameter, h.Status,
                h.CreatedAt, h.UpdatedAt
            ]);
        UiHelpers.OpenFolder(Path.GetDirectoryName(path));
    }

    private async void DeleteAllSpinHistory_Click(object? sender, RoutedEventArgs e) {
        if (_activeWheel == null) return;
        Guid wheelId = _activeWheel.Id;

        var dialog = new FAContentDialog {
            Title = "Delete Spin History",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            Content = new TextBlock {
                Text = $"Are you sure you want to delete all spin history for \"{_activeWheel.Name}\"?",
                TextWrapping = TextWrapping.Wrap,
                Width = 320,
                Margin = new Thickness(4)
            }
        };

        if (await dialog.ShowAsync() != FAContentDialogResult.Primary) return;

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        await db.WheelSpinHistories.Where(h => h.WheelId == wheelId).ExecuteDeleteAsync();

        await Dispatcher.UIThread.InvokeAsync(async () => await LoadHistoryAsync());
    }
}
