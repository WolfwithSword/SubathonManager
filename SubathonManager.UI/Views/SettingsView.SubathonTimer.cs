using System.Windows;
using Microsoft.EntityFrameworkCore;
using Wpf.Ui.Controls;
using SubathonManager.Core.Models;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.UI.Services;

namespace SubathonManager.UI.Views;

public partial class SettingsView
{
    
    private void UpdateTimerValue(SubathonData subathon, DateTime time)
        {
            if (_lastUpdatedTimerAt == null || time > _lastUpdatedTimerAt)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    _lastUpdatedTimerAt = time;
                    
                    // doing comparisons first to avoid too much UI updating
                    string timerVal = subathon.TimeRemainingRounded().ToString();
                    if (TimerValueSettings.Text != timerVal) TimerValueSettings.Text = timerVal;

                    if (subathon.IsPaused && PauseText2.Text != "Resume Timer") PauseText2.Text = "Resume Timer";
                    else if (!subathon.IsPaused && PauseText2.Text != "Pause Timer") PauseText2.Text = "Pause Timer";

                    if (subathon.IsPaused && PauseIcon2.Symbol != SymbolRegular.Play16)
                        PauseIcon2.Symbol = SymbolRegular.Play16;
                    else if (!subathon.IsPaused && PauseIcon2.Symbol != SymbolRegular.Pause16)
                        PauseIcon2.Symbol = SymbolRegular.Pause16;
                    
                    if (subathon.IsLocked && LockText2.Text != "Unlock Subathon") LockText2.Text = "Unlock Subathon";
                    else if (!subathon.IsLocked && LockText2.Text != "Lock Subathon") LockText2.Text = "Lock Subathon";
                    
                    if (subathon.IsLocked && LockIcon2.Symbol != SymbolRegular.LockOpen16)
                        LockIcon2.Symbol = SymbolRegular.LockOpen16;
                    else if (!subathon.IsLocked && LockIcon2.Symbol != SymbolRegular.LockClosed16)
                        LockIcon2.Symbol = SymbolRegular.LockClosed16;
                    
                    string pts = $"{subathon.Points} Pts";
                    if (PointsValueSettings.Text != pts) PointsValueSettings.Text = pts;
                    
                    double moneySum =  subathon.GetRoundedMoneySumWithCents();
                    string money = $"{subathon.Currency} {moneySum:N2}".Trim();
                    if (MoneyValueSettings.Text != money) MoneyValueSettings.Text = money;
                });
            }
        }
        
        private void RemoveSimEvents_Click(object sender, RoutedEventArgs e)
        {
            using var db = _factory.CreateDbContext();
            ServiceManager.EventsOrNull?.UndoSimulatedEvents(db, new(), true);
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