using Microsoft.EntityFrameworkCore;
using Wpf.Ui.Controls;
using SubathonManager.Core.Models;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Data;
using SubathonManager.Core.Events;

namespace SubathonManager.UI;

public partial class MainWindow
{
    
    
    public void TogglePowerMultiplier_Click(object sender, EventArgs e)
    {
        using var db = new AppDbContext();
        SubathonData? subathon = db.SubathonDatas
            .Include(s => s.Multiplier).FirstOrDefault(s => s.IsActive);
        if (subathon == null) return;

        bool isMultiplierActive = subathon.Multiplier.Multiplier > 1 || subathon.Multiplier.Multiplier < 1;
        if (isMultiplierActive)
        {
            // we are stopping
            SubathonEvent stopSubathonEvent = new SubathonEvent
            {
                Source = SubathonEventSource.Command,
                SubathonId = subathon.Id,
                User = "SYSTEM",
                Command = SubathonCommandType.StopMultiplier,
                EventType = SubathonEventType.Command,
                EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1)
            };
            
            SubathonEvents.RaiseSubathonEventCreated(stopSubathonEvent);
        }
        else
        {
            bool applyTime = ApplyTimeCb.IsChecked ?? false;
            bool applyPts = ApplyPtsCb.IsChecked ?? false;
            if (!applyTime && !applyPts)
            {
                
                SubathonEvent stopSubathonEvent = new SubathonEvent
                {
                    Source = SubathonEventSource.Command,
                    SubathonId = subathon.Id,
                    User = "SYSTEM",
                    Value = $"{SubathonCommandType.SetMultiplier} Failed",
                    Command = SubathonCommandType.StopMultiplier,
                    EventType = SubathonEventType.Command,
                    EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1)
                };
            
                SubathonEvents.RaiseSubathonEventCreated(stopSubathonEvent);
                return;
            }
            
            TimeSpan duration = Utils.ParseDurationString(MultiplierDurationInput.Text);
            
            if (!double.TryParse(MultiplierAmtInput.Text, out var parsedAmt))
                return;
            
            string durationStr = duration == TimeSpan.Zero ? "x" : ((int) duration.TotalSeconds).ToString();
            string dataStr = $"{parsedAmt}|{durationStr}s|{applyPts}|{applyTime}";
            SubathonEvent subathonEvent = new SubathonEvent
            {
                Source = SubathonEventSource.Command,
                SubathonId = subathon.Id,
                User = "SYSTEM",
                Command = SubathonCommandType.SetMultiplier,
                EventType = SubathonEventType.Command,
                EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
                Value = dataStr
            };
            
            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        }
        
    }

    private void UpdateMultiplierUi(SubathonData sb, DateTime time)
    {
        using var db = new AppDbContext();
        SubathonData? subathon = db.SubathonDatas
            .Include(s => s.Multiplier).FirstOrDefault(s => s.IsActive && sb.Id == s.Id);
        if (subathon == null) return;

        bool isMultiplierSet = subathon.Multiplier.Multiplier < 1 || subathon.Multiplier.Multiplier > 1;

        TimeSpan? newDuration = subathon.Multiplier.Duration == null || subathon.Multiplier.Started == null ? null :
            (subathon.Multiplier.Started + subathon.Multiplier.Duration) - DateTime.Now;
        if (newDuration != null)
        {
            newDuration = TimeSpan.FromSeconds(Math.Floor(newDuration.Value.TotalSeconds));
        }
        
        Dispatcher.Invoke(() =>
        {
            MultiplierIcon.Symbol = isMultiplierSet ? SymbolRegular.Prohibited20 : SymbolRegular.Play20;
            ToggleMultiplierBtn.ToolTip = isMultiplierSet ? "Stop Multiplier" : "Start Multiplier";
            MultiplierVals.Text = $"Time x{(subathon.Multiplier.ApplyToSeconds ? subathon.Multiplier.Multiplier : 1)}\t" +
                                  $"Points x{(subathon.Multiplier.ApplyToPoints ? subathon.Multiplier.Multiplier : 1)}";
            MultiplierRemainingTime.Text = !isMultiplierSet || newDuration == null ? "" : $"Remaining: {newDuration}";
        });
        
        // color of btn?
    }
}