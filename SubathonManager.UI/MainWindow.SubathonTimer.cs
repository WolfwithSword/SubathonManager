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
        private async void UpdateTimerValue(SubathonData subathon, DateTime time)
        {
            if (_lastUpdatedTimerAt == null || time > _lastUpdatedTimerAt)
            {
                Dispatcher.Invoke(() =>
                {
                    TimerValue.Text = subathon.TimeRemainingRounded().ToString();
                    _lastUpdatedTimerAt = time;
                    PauseText.Text = subathon.IsPaused ? "Resume Timer" : "Pause Timer";
                    PauseIcon.Symbol = subathon.IsPaused  ? SymbolRegular.Play16 : SymbolRegular.Pause16;
                    LockText.Text = subathon.IsLocked ? "Unlock Subathon" : "Lock Subathon";
                    LockIcon.Symbol = subathon.IsLocked ? SymbolRegular.LockOpen16 : SymbolRegular.LockClosed16;
                    
                    
                    TimerValueSettings.Text = subathon.TimeRemainingRounded().ToString();
                    PauseText2.Text = subathon.IsPaused ? "Resume Timer" : "Pause Timer";
                    PauseIcon2.Symbol = subathon.IsPaused  ? SymbolRegular.Play16 : SymbolRegular.Pause16;
                    LockText2.Text = subathon.IsLocked ? "Unlock Subathon" : "Lock Subathon";
                    LockIcon2.Symbol = subathon.IsLocked ? SymbolRegular.LockOpen16 : SymbolRegular.LockClosed16;
                    
                    PointsValue.Text = $"{subathon.Points.ToString()} Pts";
                    PointsValueSettings.Text = subathon.Points.ToString();
                });
            }
        }
        
        private async void RemoveSimEvents_Click(object sender, RoutedEventArgs e)
        {
            AppDbContext.UndoSimulatedEvents(new(), true);
        }


        private void StartNewSubathon_Click(object sender, RoutedEventArgs e)
        {
            Task.Run(() => AppDbContext.DisableAllTimers(new AppDbContext()));
            using var db = new AppDbContext();
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
            
            using var db = new AppDbContext();
            SubathonData? subathon = db.SubathonDatas.FirstOrDefault(s => s.IsActive);
            if (subathon == null) return;

            TimeSpan diff = timeToSet - subathon.TimeRemainingRounded();

            string rawText = AdjustSubathonTime.Text.Replace(" ", "");
            SubathonEvent _event = new SubathonEvent
            {
                EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
                Value = $"SET {rawText}",
                SecondsValue = diff.TotalSeconds,
                PointsValue = 0,
                Source = SubathonEventSource.Command,
                EventType = SubathonEventType.Command,
                User = "SYSTEM"
            };
            _lastUpdatedTimerAt = null;
            SubathonEvents.RaiseSubathonEventCreated(_event);
        }
        
        private void AdjustSubathonTimeBy(int direction)
        {
            TimeSpan timeToAdjust = Utils.ParseDurationString(AdjustSubathonTime.Text);
            if (timeToAdjust == TimeSpan.Zero) return;

            string rawText = AdjustSubathonTime.Text.Replace(" ", "");
            string cmd = direction > 0 ? "ADD" : "SUBTRACT";
            SubathonEvent _event = new SubathonEvent
            {
                EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
                Value = $"{cmd} {rawText}",
                SecondsValue = direction * timeToAdjust.TotalSeconds,
                PointsValue = 0,
                Source = SubathonEventSource.Command,
                EventType = SubathonEventType.Command,
                User = "SYSTEM"
            };
            _lastUpdatedTimerAt = null;
            SubathonEvents.RaiseSubathonEventCreated(_event);
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
            
            using var db = new AppDbContext();
            SubathonData? subathon = db.SubathonDatas.FirstOrDefault(s => s.IsActive);
            if (subathon == null) return;

            int diff = parsedInt - subathon.Points;

            SubathonEvent _event = new SubathonEvent
            {
                EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
                Value = $"SET {AdjustSubathonPoints.Text}",
                SecondsValue = 0,
                PointsValue = diff,
                Source = SubathonEventSource.Command,
                EventType = SubathonEventType.Command,
                User = "SYSTEM"
            };
            _lastUpdatedTimerAt = null;
            SubathonEvents.RaiseSubathonEventCreated(_event);
        }
        
        private void AdjustSubathonPointsBy(int direction)
        {
            if (!int.TryParse(AdjustSubathonPoints.Text, out var parsedInt))
                return;
            if (parsedInt < 0) return;

            string cmd = direction > 0 ? "ADD" : "SUBTRACT";
            SubathonEvent _event = new SubathonEvent
            {
                EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
                Value = $"{cmd} {AdjustSubathonPoints.Text}",
                SecondsValue = 0,
                PointsValue = direction * parsedInt,
                Source = SubathonEventSource.Command,
                EventType = SubathonEventType.Command,
                User = "SYSTEM"
            };
            _lastUpdatedTimerAt = null;
            SubathonEvents.RaiseSubathonEventCreated(_event);
        }
        
        private void TogglePauseSubathon_Click(object sender, RoutedEventArgs e)
        {
            using var db = new AppDbContext();
            int affected = db.Database.ExecuteSqlRaw(
                "UPDATE SubathonDatas SET IsPaused = 1 - IsPaused " +
                "AND MillisecondsCumulative - MillisecondsElapsed > 0");
            if (affected > 0)
            {
                SubathonData? subathon = db.SubathonDatas.FirstOrDefault(s => s.IsActive);
                if (subathon != null) SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
            }
        }

        private void ToggleLockSubathon_Click(object sender, RoutedEventArgs e)
        {
            using var db = new AppDbContext();
            int affected = db.Database.ExecuteSqlRaw(
                "UPDATE SubathonDatas SET IsLocked = 1 - IsLocked " +
                "AND MillisecondsCumulative - MillisecondsElapsed > 0");
            if (affected > 0)
            {
                SubathonData? subathon = db.SubathonDatas.FirstOrDefault(s => s.IsActive);
                if (subathon != null) SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
            }
        }
    }
}
