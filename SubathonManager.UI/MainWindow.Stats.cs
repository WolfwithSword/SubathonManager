using System.Windows;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Enums;

namespace SubathonManager.UI;

public partial class MainWindow
{
    private void TimerHoverArea_MouseEnter(object sender, MouseEventArgs e)
    {
        RefreshStatsPanel();
        StatsPopup.IsOpen = true;
    }

    private void TimerHoverArea_MouseLeave(object sender, MouseEventArgs e)
    {
        StatsPopup.IsOpen = false;
    }

    private void RefreshStatsPanel()
    {
        Dispatcher.Invoke(() =>
        {     
            using var db = _factory.CreateDbContext();
            var subathon = db.SubathonDatas
                .AsNoTracking()
                .FirstOrDefault(s => s.IsActive);

            if (subathon == null) return;
            TimeSpan elapsed = TimeSpan.FromMilliseconds(subathon.MillisecondsElapsed);

            TimeSpan totalAccumulated = TimeSpan.FromMilliseconds(subathon.MillisecondsCumulative);
            
            ////////////////////////
            StatsElapsedTime.Text = FormatTimeSpan(elapsed);
            if (subathon.IsSubathonReversed())
            {
                StatsTotalAccumulated.Visibility = Visibility.Collapsed;
                StatsAccu.Visibility = Visibility.Collapsed;
            }
            else
            {
                StatsTotalAccumulated.Visibility = Visibility.Visible;
                StatsAccu.Visibility = Visibility.Visible;
                StatsTotalAccumulated.Text = FormatTimeSpan(totalAccumulated);
            }

            int eventCount = db.SubathonEvents.Count(ev => ev.SubathonId == subathon.Id &&
                                                           ev.Command == SubathonCommandType.None && 
                                                           ev.Source != SubathonEventSource.Simulated);
            StatsTotalEvents.Text = $"{eventCount:N0}";
        });
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalDays >= 1)
            return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Minutes}m {ts.Seconds}s";
    }
}