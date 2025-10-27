using System.Windows;
using Microsoft.EntityFrameworkCore;
using Wpf.Ui.Controls;
using SubathonManager.Core.Models;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Data;
using SubathonManager.Core.Events;

namespace SubathonManager.UI
{
    public partial class MainWindow
    {
        private void UpdateTimerValue(SubathonData subathon, DateTime time)
        {
            if (_lastUpdatedTimerAt == null || time > _lastUpdatedTimerAt)
            {
                // TODO perhaps we need to better bind this instead of invoking updates every second? if perf issues
                Dispatcher.Invoke(() =>
                {
                    TimerValue.Text = subathon.TimeRemainingRounded().ToString();
                    _lastUpdatedTimerAt = time;
                    // PauseText.Text = subathon.IsPaused ? "Resume" : "Pause";
                    PauseIcon.Symbol = subathon.IsPaused  ? SymbolRegular.Play32 : SymbolRegular.Pause32;
                    // LockText.Text = subathon.IsLocked ? "Unlock" : "Lock";
                    LockIcon.Symbol = subathon.IsLocked ? SymbolRegular.LockOpen28 : SymbolRegular.LockClosed32;
                    PointsValue.Text = $"{subathon.Points.ToString()} Pts";
                    LockStatus.Visibility = subathon.IsLocked ? Visibility.Visible : Visibility.Hidden;
                });
            }
        }

        private void StartNewSubathon_Click(object sender, RoutedEventArgs e)
        {
            Task.Run(() => AppDbContext.DisableAllTimers(_factory.CreateDbContext()));
            using var db = _factory.CreateDbContext();
            SubathonData subathon = new SubathonData();
            TimeSpan initialMs = Utils.ParseDurationString(InitialSubathonTime.Text);
            if (initialMs == TimeSpan.Zero)
            {
                initialMs = TimeSpan.FromSeconds(1);
            }
            subathon.MillisecondsCumulative += (int) initialMs.TotalMilliseconds;
            subathon.IsPaused = true; 
            db.SubathonDatas.Add(subathon);
            db.SaveChanges();
            db.Entry(subathon).State = EntityState.Detached;
            SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
            SubathonEvents.RaiseSubathonEventsDeleted();
        }

        private void AddTime_Click(object sender, RoutedEventArgs e)
        {
            AdjustSubathonTimeBy(1);
        }

        private void SubtractTime_Click(object sender, RoutedEventArgs e)
        {
            AdjustSubathonTimeBy(-1);
        }

        private void SetTime_Click(object sender, RoutedEventArgs e)
        {
            TimeSpan timeToSet = Utils.ParseDurationString(AdjustSubathonTime.Text);
            if (timeToSet <= TimeSpan.Zero) return;
            
            using var db = _factory.CreateDbContext();
            SubathonData? subathon = db.SubathonDatas.AsNoTracking().FirstOrDefault(s => s.IsActive);
            if (subathon == null) return;

            string rawText = AdjustSubathonTime.Text.Replace(" ", "");
            SubathonEvent subathonEvent = new SubathonEvent
            {
                EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
                Value = $"{SubathonCommandType.SetTime} {rawText}",
                SecondsValue = timeToSet.TotalSeconds,
                Command = SubathonCommandType.SetTime,
                PointsValue = 0,
                Source = SubathonEventSource.Command,
                EventType = SubathonEventType.Command,
                User = "SYSTEM"
            };
            _lastUpdatedTimerAt = null;
            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        }
        
        private void AdjustSubathonTimeBy(int direction)
        {
            TimeSpan timeToAdjust = Utils.ParseDurationString(AdjustSubathonTime.Text);
            if (timeToAdjust == TimeSpan.Zero) return;

            string rawText = AdjustSubathonTime.Text.Replace(" ", "");
            SubathonCommandType cmd = direction > 0 ? SubathonCommandType.AddTime : SubathonCommandType.SubtractTime;
            SubathonEvent subathonEvent = new SubathonEvent
            {
                EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
                Command = cmd,
                Value = $"{cmd} {rawText}",
                SecondsValue = timeToAdjust.TotalSeconds,
                PointsValue = 0,
                Source = SubathonEventSource.Command,
                EventType = SubathonEventType.Command,
                User = "SYSTEM"
            };
            _lastUpdatedTimerAt = null;
            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        }
        
        
        private void AddPoints_Click(object sender, RoutedEventArgs e)
        {
            AdjustSubathonPointsBy(1);
        }

        private void SubtractPoints_Click(object sender, RoutedEventArgs e)
        {
            AdjustSubathonPointsBy(-1);
        }

        private void SetPoints_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(AdjustSubathonPoints.Text, out var parsedInt))
                return;
            if (parsedInt < 0) return;
            
            using var db = _factory.CreateDbContext();
            SubathonData? subathon = db.SubathonDatas.AsNoTracking().FirstOrDefault(s => s.IsActive);
            if (subathon == null) return;

            SubathonEvent subathonEvent = new SubathonEvent
            {
                EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
                Value = $"{SubathonCommandType.SetPoints} {AdjustSubathonPoints.Text}",
                Command = SubathonCommandType.SetPoints,
                SecondsValue = 0,
                PointsValue = parsedInt,
                Source = SubathonEventSource.Command,
                EventType = SubathonEventType.Command,
                User = "SYSTEM"
            };
            _lastUpdatedTimerAt = null;
            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        }
        
        private void AdjustSubathonPointsBy(int direction)
        {
            if (!int.TryParse(AdjustSubathonPoints.Text, out var parsedInt))
                return;
            if (parsedInt < 0) return;
            SubathonCommandType cmd =
                direction > 0 ? SubathonCommandType.AddPoints : SubathonCommandType.SubtractPoints;
            SubathonEvent subathonEvent = new SubathonEvent
            {
                EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
                Command = cmd,
                Value = $"{cmd} {AdjustSubathonPoints.Text}",
                SecondsValue = 0,
                PointsValue = parsedInt,
                Source = SubathonEventSource.Command,
                EventType = SubathonEventType.Command,
                User = "SYSTEM"
            };
            _lastUpdatedTimerAt = null;
            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        }
        
        private void TogglePauseSubathon_Click(object sender, RoutedEventArgs e)
        {
            using var db = _factory.CreateDbContext();
            SubathonData? subathon = db.SubathonDatas.AsNoTracking().FirstOrDefault(s => s.IsActive);
            if (subathon == null) return;
            SubathonCommandType cmd = subathon.IsPaused ? SubathonCommandType.Resume : SubathonCommandType.Pause;
            SubathonEvent subathonEvent = new SubathonEvent
            {
                EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
                Command = cmd, 
                Value = $"{cmd}",
                SecondsValue = 0,
                PointsValue = 0,
                Source = SubathonEventSource.Command,
                EventType = SubathonEventType.Command,
                User = "SYSTEM"
            };
            _lastUpdatedTimerAt = null;
            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        }

        private void ToggleLockSubathon_Click(object sender, RoutedEventArgs e)
        {
            using var db = _factory.CreateDbContext();
            SubathonData? subathon = db.SubathonDatas.AsNoTracking().FirstOrDefault(s => s.IsActive);
            if (subathon == null) return;
            SubathonCommandType cmd = subathon.IsLocked ? SubathonCommandType.Unlock : SubathonCommandType.Lock;
            SubathonEvent subathonEvent = new SubathonEvent
            {
                EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
                Command = cmd, 
                Value = $"{cmd}",
                SecondsValue = 0,
                PointsValue = 0,
                Source = SubathonEventSource.Command,
                EventType = SubathonEventType.Command,
                User = "SYSTEM"
            };
            _lastUpdatedTimerAt = null;
            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        }
    }
}
