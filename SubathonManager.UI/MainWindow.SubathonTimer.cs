using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.UI.UiUtils;
using SubathonManager.UI.Services;

namespace SubathonManager.UI;

public partial class MainWindow
{
    // ReSharper disable once InconsistentNaming
    internal readonly IDbContextFactory<AppDbContext> _factory =
        AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();

    private DateTime? _lastUpdatedTimerAt;

    private void InitHome()
    {
        SubathonEvents.SubathonDataUpdate += OnSubathonDataUpdate;
        Closed += (_, _) => SubathonEvents.SubathonDataUpdate -= OnSubathonDataUpdate;

        var currencies = ServiceManager.Events.ValidEventCurrencies().OrderBy(x => x).ToList();
        AdjustCurrencyBox.ItemsSource = currencies;
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        AdjustCurrencyBox.Text = config.Get("Currency", "Primary", "USD")?.Trim().ToUpperInvariant() ?? "USD";
        AdjustCurrencyBox.GotFocus += (_, _) =>
        {
            if ((AdjustCurrencyBox.ItemsSource as IEnumerable<string>)?.Count() is null or <= 1)
            {
                var current = AdjustCurrencyBox.Text;
                AdjustCurrencyBox.ItemsSource = ServiceManager.Events.ValidEventCurrencies().OrderBy(x => x).ToList();
                AdjustCurrencyBox.Text = current;
            }
            AdjustCurrencyBox.IsDropDownOpen = true;
        };
    }

    private void OnSubathonDataUpdate(SubathonData subathon, DateTime time)
    {
        UpdateTimerValue(subathon, time);
        UpdateMultiplierUi(subathon, time);
    }

    private void UpdateTimerValue(SubathonData subathon, DateTime time)
    {
        if (_lastUpdatedTimerAt != null && time <= _lastUpdatedTimerAt) return;

        Dispatcher.UIThread.Post(() =>
        {
            double moneySum = subathon.GetRoundedMoneySumWithCents();
            TimerValue.Text = subathon.TimeRemainingRounded().ToString();
            _lastUpdatedTimerAt = time;

            var pauseGlyph = subathon.IsPaused ? "Play16" : "Pause16";
            if (PauseIcon.Glyph != pauseGlyph) PauseIcon.Glyph = pauseGlyph;

            var lockGlyph = subathon.IsLocked ? "LockOpen16" : "LockClosed16";
            if (LockIcon.Glyph != lockGlyph) LockIcon.Glyph = lockGlyph;

            if (PointsValue.Text != $"{subathon.Points:N0} Pts")
                PointsValue.Text = $"{subathon.Points:N0} Pts";

            var moneyText = $"{subathon.Currency} {moneySum:N2}".Trim();
            if (MoneyValue.Text != moneyText) MoneyValue.Text = moneyText;

            if (LockStatus.IsVisible != subathon.IsLocked) LockStatus.IsVisible = subathon.IsLocked;

            ToolTip.SetTip(TogglePauseTimerBtn, subathon.IsPaused ? "Resume" : "Pause");
            ToolTip.SetTip(ToggleLockTimerBtn, subathon.IsLocked ? "Unlock" : "Lock");

            var capGlyph = subathon.CapDateTime.HasValue && subathon.CapDateTime > DateTime.Now
                ? "Flag20" : "FlagOff20";
            if (CapIcon.Glyph != capGlyph) CapIcon.Glyph = capGlyph;

            if (subathon.IsCapInEffect())
                CapIcon.Foreground = Brushes.Orange;
            else
                CapIcon.SetDynamicResource(FluentIcons.Avalonia.SymbolIcon.ForegroundProperty, "TextFillColorPrimaryBrush");
        });
    }

    private async void StartNewSubathon_Click(object? sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Width = 320,
            Margin = new global::Avalonia.Thickness(4, 4, 4, 12),
            Text = "Confirm deleting the current subathon (time, points, events) and starting a new one?"
        });

        var initialTimeBox = new TextBox { Text = "8h", Width = 140, Margin = new global::Avalonia.Thickness(2, 4, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "Initial Time:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new global::Avalonia.Thickness(16, 4, 8, 0)
        });
        row.Children.Add(initialTimeBox);
        panel.Children.Add(row);

        var reverseCb = new CheckBox
        {
            Content = "Reverse Subathon?",
            IsChecked = false,
            Margin = new global::Avalonia.Thickness(4, 12, 4, 8)
        };
        ToolTip.SetTip(reverseCb, "When enabled, time will tick up, and events reduce the timer");
        panel.Children.Add(reverseCb);

        var dialog = new FAContentDialog
        {
            Title = "Start New Subathon",
            PrimaryButtonText = "Confirm",
            CloseButtonText = "Cancel",
            Content = panel
        };
        if (await dialog.ShowAsync() != FAContentDialogResult.Primary) return;

        await Task.Run(async () => AppDbContext.DisableAllTimers(await _factory.CreateDbContextAsync()));
        await using var db = await _factory.CreateDbContextAsync();

        var subathon = new SubathonData();
        TimeSpan initial = Utils.ParseDurationString(initialTimeBox.Text);
        if (initial == TimeSpan.Zero) initial = TimeSpan.FromSeconds(1);

        var config = AppServices.Provider.GetRequiredService<IConfig>();
        subathon.MillisecondsCumulative += (int)initial.TotalMilliseconds;
        subathon.IsPaused = true;
        subathon.ReversedTime = reverseCb.IsChecked ?? false;
        subathon.Currency = config.Get("Currency", "Primary", "USD")!;
        db.SubathonDatas.Add(subathon);
        await db.SaveChangesAsync();
        db.Entry(subathon).State = EntityState.Detached;

        SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
        SubathonEvents.RaiseSubathonEventsDeleted([]);
    }

    private void AddTime_Click(object? sender, RoutedEventArgs e) => AdjustSubathonTimeBy(1);
    private void SubtractTime_Click(object? sender, RoutedEventArgs e) => AdjustSubathonTimeBy(-1);

    private void SetTime_Click(object? sender, RoutedEventArgs e)
    {
        TimeSpan timeToSet = Utils.ParseDurationString(AdjustSubathonTime.Text ?? "");
        if (timeToSet <= TimeSpan.Zero) return;

        using var db = _factory.CreateDbContext();
        var subathon = db.SubathonDatas.AsNoTracking().FirstOrDefault(s => s.IsActive);
        if (subathon == null) return;

        string rawText = (AdjustSubathonTime.Text ?? "").Replace(" ", "");
        RaiseCommand(new SubathonEvent
        {
            EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
            Value = $"{SubathonCommandType.SetTime} {rawText}",
            SecondsValue = timeToSet.TotalSeconds,
            Command = SubathonCommandType.SetTime,
            PointsValue = 0,
            Source = SubathonEventSource.Command,
            EventType = SubathonEventType.Command,
            User = "SYSTEM"
        });
    }

    private void AdjustSubathonTimeBy(int direction)
    {
        TimeSpan timeToAdjust = Utils.ParseDurationString(AdjustSubathonTime.Text ?? "");
        if (timeToAdjust == TimeSpan.Zero) return;

        string rawText = (AdjustSubathonTime.Text ?? "").Replace(" ", "");
        SubathonCommandType cmd = direction > 0 ? SubathonCommandType.AddTime : SubathonCommandType.SubtractTime;
        RaiseCommand(new SubathonEvent
        {
            EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
            Command = cmd,
            Value = $"{cmd} {rawText}",
            SecondsValue = timeToAdjust.TotalSeconds,
            PointsValue = 0,
            Source = SubathonEventSource.Command,
            EventType = SubathonEventType.Command,
            User = "SYSTEM"
        });
    }

    private void AddMoney_Click(object? sender, RoutedEventArgs e) => AdjustSubathonMoneyBy(1);
    private void SubtractMoney_Click(object? sender, RoutedEventArgs e) => AdjustSubathonMoneyBy(-1);

    private void AdjustSubathonMoneyBy(int direction)
    {
        if (!double.TryParse(AdjustSubathonMoney.Text, out var parsedVal) || parsedVal <= 0) return;
        SubathonCommandType cmd = direction > 0 ? SubathonCommandType.AddMoney : SubathonCommandType.SubtractMoney;
        RaiseCommand(new SubathonEvent
        {
            EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
            Command = cmd,
            Value = AdjustSubathonMoney.Text ?? "",
            Currency = $"{(AdjustCurrencyBox.Text ?? "").Trim().ToUpperInvariant()}",
            SecondsValue = 0,
            PointsValue = 0,
            Source = SubathonEventSource.Command,
            EventType = SubathonEventType.Command,
            User = "SYSTEM"
        });
    }

    private void AddPoints_Click(object? sender, RoutedEventArgs e) => AdjustSubathonPointsBy(1);
    private void SubtractPoints_Click(object? sender, RoutedEventArgs e) => AdjustSubathonPointsBy(-1);

    private void SetPoints_Click(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(AdjustSubathonPoints.Text, out var parsedInt) || parsedInt < 0) return;

        using var db = _factory.CreateDbContext();
        var subathon = db.SubathonDatas.AsNoTracking().FirstOrDefault(s => s.IsActive);
        if (subathon == null) return;

        RaiseCommand(new SubathonEvent
        {
            EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
            Value = $"{SubathonCommandType.SetPoints} {AdjustSubathonPoints.Text}",
            Command = SubathonCommandType.SetPoints,
            SecondsValue = 0,
            PointsValue = parsedInt,
            Source = SubathonEventSource.Command,
            EventType = SubathonEventType.Command,
            User = "SYSTEM"
        });
    }

    private void AdjustSubathonPointsBy(int direction)
    {
        if (!int.TryParse(AdjustSubathonPoints.Text, out var parsedInt) || parsedInt <= 0) return;
        SubathonCommandType cmd = direction > 0 ? SubathonCommandType.AddPoints : SubathonCommandType.SubtractPoints;
        RaiseCommand(new SubathonEvent
        {
            EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
            Command = cmd,
            Value = $"{cmd} {AdjustSubathonPoints.Text}",
            SecondsValue = 0,
            PointsValue = parsedInt,
            Source = SubathonEventSource.Command,
            EventType = SubathonEventType.Command,
            User = "SYSTEM"
        });
    }

    private void TogglePauseSubathon_Click(object? sender, RoutedEventArgs e)
    {
        using var db = _factory.CreateDbContext();
        var subathon = db.SubathonDatas.AsNoTracking().FirstOrDefault(s => s.IsActive);
        if (subathon == null) return;
        SubathonCommandType cmd = subathon.IsPaused ? SubathonCommandType.Resume : SubathonCommandType.Pause;
        RaiseCommand(new SubathonEvent
        {
            EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
            Command = cmd,
            Value = $"{cmd}",
            SecondsValue = 0,
            PointsValue = 0,
            Source = SubathonEventSource.Command,
            EventType = SubathonEventType.Command,
            User = "SYSTEM"
        });
    }

    private void ToggleLockSubathon_Click(object? sender, RoutedEventArgs e)
    {
        using var db = _factory.CreateDbContext();
        var subathon = db.SubathonDatas.AsNoTracking().FirstOrDefault(s => s.IsActive);
        if (subathon == null) return;
        SubathonCommandType cmd = subathon.IsLocked ? SubathonCommandType.Unlock : SubathonCommandType.Lock;
        RaiseCommand(new SubathonEvent
        {
            EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
            Command = cmd,
            Value = $"{cmd}",
            SecondsValue = 0,
            PointsValue = 0,
            Source = SubathonEventSource.Command,
            EventType = SubathonEventType.Command,
            User = "SYSTEM"
        });
    }

    private void RaiseCommand(SubathonEvent subathonEvent)
    {
        _lastUpdatedTimerAt = null;
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }

    private void SendRefreshRequest_Click(object? sender, RoutedEventArgs e)
        => OverlayEvents.RaiseOverlayRefreshAllRequested();
}
