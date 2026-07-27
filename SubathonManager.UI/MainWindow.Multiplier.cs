using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;

namespace SubathonManager.UI;

public partial class MainWindow
{
    private void TogglePowerMultiplier_Click(object? sender, RoutedEventArgs e)
    {
        using var db = _factory.CreateDbContext();
        var subathon = db.SubathonDatas.Include(s => s.Multiplier).FirstOrDefault(s => s.IsActive);
        if (subathon == null) return;

        bool isMultiplierActive = subathon.Multiplier.Multiplier > 1 || subathon.Multiplier.Multiplier < 1;
        if (isMultiplierActive)
        {
            SubathonEvents.RaiseSubathonEventCreated(new SubathonEvent
            {
                Source = SubathonEventSource.Command,
                SubathonId = subathon.Id,
                User = "SYSTEM",
                Command = SubathonCommandType.StopMultiplier,
                EventType = SubathonEventType.Command,
                EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1)
            });
            return;
        }

        bool applyTime = ApplyTimeCb.IsChecked ?? false;
        bool applyPts = ApplyPtsCb.IsChecked ?? false;
        if (!applyTime && !applyPts)
        {
            SubathonEvents.RaiseSubathonEventCreated(new SubathonEvent
            {
                Source = SubathonEventSource.Command,
                SubathonId = subathon.Id,
                User = "SYSTEM",
                Value = $"{SubathonCommandType.SetMultiplier} Failed",
                Command = SubathonCommandType.StopMultiplier,
                EventType = SubathonEventType.Command,
                EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1)
            });
            return;
        }

        TimeSpan duration = Utils.ParseDurationString(MultiplierDurationInput.Text ?? "");
        if (!double.TryParse(MultiplierAmtInput.Text, out var parsedAmt) || parsedAmt <= 0) return;

        string durationStr = duration == TimeSpan.Zero ? "x" : ((int)duration.TotalSeconds).ToString();
        string dataStr = $"{parsedAmt}|{durationStr}s|{applyPts}|{applyTime}";
        SubathonEvents.RaiseSubathonEventCreated(new SubathonEvent
        {
            Source = SubathonEventSource.Command,
            SubathonId = subathon.Id,
            User = "SYSTEM",
            Command = SubathonCommandType.SetMultiplier,
            EventType = SubathonEventType.Command,
            EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
            Value = dataStr
        });
    }

    private void UpdateMultiplierUi(SubathonData subathon, DateTime time)
    {
        bool isMultiplierSet = subathon.Multiplier.Multiplier < 1 || subathon.Multiplier.Multiplier > 1;

        TimeSpan? newDuration = subathon.Multiplier.Duration == null || subathon.Multiplier.Started == null
            ? null
            : (subathon.Multiplier.Started + subathon.Multiplier.Duration) - DateTime.Now;
        if (newDuration != null)
            newDuration = TimeSpan.FromSeconds(Math.Floor(newDuration.Value.TotalSeconds));

        Dispatcher.UIThread.Post(() =>
        {
            var glyph = isMultiplierSet ? "Prohibited16" : "Play16";
            if (MultiplierIcon.Glyph != glyph) MultiplierIcon.Glyph = glyph;

            ToolTip.SetTip(ToggleMultiplierBtn, isMultiplierSet ? "Stop Multiplier" : "Start Multiplier");

            string valsText = $"Time x{(subathon.Multiplier.ApplyToSeconds ? subathon.Multiplier.Multiplier : 1)}\tPoints x{(subathon.Multiplier.ApplyToPoints ? subathon.Multiplier.Multiplier : 1)}";
            if (MultiplierVals.Text != valsText) MultiplierVals.Text = valsText;

            string remainingText = !isMultiplierSet || newDuration == null ? "" : $"Remaining: {newDuration}";
            if (MultiplierRemainingTime.Text != remainingText) MultiplierRemainingTime.Text = remainingText;

            if (subathon.Multiplier.FromHypeTrain && MultiplierRemainingTime.Text != "HypeTrain")
                MultiplierRemainingTime.Text = "HypeTrain";
        });
    }
}
