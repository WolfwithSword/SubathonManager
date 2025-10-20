using System.Windows;
using Microsoft.EntityFrameworkCore;
using Wpf.Ui.Controls;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Core.Events;

namespace SubathonManager.UI.Views;

public partial class SettingsView
{
    
    private async void UpdateTimerValue(SubathonData subathon, DateTime time)
        {
            if (_lastUpdatedTimerAt == null || time > _lastUpdatedTimerAt)
            {
                Dispatcher.Invoke(() =>
                {
                    _lastUpdatedTimerAt = time;
                    TimerValueSettings.Text = subathon.TimeRemainingRounded().ToString();
                    PauseText2.Text = subathon.IsPaused ? "Resume Timer" : "Pause Timer";
                    PauseIcon2.Symbol = subathon.IsPaused  ? SymbolRegular.Play16 : SymbolRegular.Pause16;
                    LockText2.Text = subathon.IsLocked ? "Unlock Subathon" : "Lock Subathon";
                    LockIcon2.Symbol = subathon.IsLocked ? SymbolRegular.LockOpen16 : SymbolRegular.LockClosed16;
                    PointsValueSettings.Text = $"{subathon.Points.ToString()} Pts";
                });
            }
        }
        
        private async void RemoveSimEvents_Click(object sender, RoutedEventArgs e)
        {
            AppDbContext.UndoSimulatedEvents(new(), true);
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