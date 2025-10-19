using System.Windows;
using Microsoft.EntityFrameworkCore;
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
                    TogglePauseTimerBtn.Content = subathon.IsPaused ? "Resume Timer" : "Pause Timer";
                    ToggleLockTimerBtn.Content = subathon.IsLocked ? "Unlock Subathon" : "Lock Subathon";
                    
                    
                    TimerValueSettings.Text = subathon.TimeRemainingRounded().ToString();
                    TogglePauseTimerBtnSettings.Content = subathon.IsPaused ? "Resume Timer" : "Pause Timer";
                    ToggleLockTimerBtnSettings.Content = subathon.IsLocked ? "Unlock Subathon" : "Lock Subathon";
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
        }

        private void AddTime_Click(object sender, RoutedEventArgs e)
        {
            AdjustSubathonTimeBy(1);
        }

        private void SubtractTime_Click(object sender, RoutedEventArgs e)
        {
            AdjustSubathonTimeBy(-1);
        }
        
        private void AdjustSubathonTimeBy(int direction)
        {
            TimeSpan timeToAdjust = Utils.ParseDurationString(AdjustSubathonTime.Text);
            if (timeToAdjust == TimeSpan.Zero) return;

            string rawText = AdjustSubathonTime.Text.Replace(" ", "");
            SubathonEvent _event = new SubathonEvent
            {
                EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
                Value = rawText,
                SecondsValue = direction * timeToAdjust.TotalSeconds,
                PointsValue = 0,
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
