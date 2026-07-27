using Avalonia.Input;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Enums;

namespace SubathonManager.UI;

public partial class MainWindow
{
    private void TimerHoverArea_PointerEntered(object? sender, PointerEventArgs e)
    {
        RefreshStatsPanel();
        StatsPopup.IsOpen = true;
    }

    private void TimerHoverArea_PointerExited(object? sender, PointerEventArgs e)
    {
        StatsPopup.IsOpen = false;
    }

    private void RefreshStatsPanel()
    {
        using var db = _factory.CreateDbContext();
        var subathon = db.SubathonDatas.AsNoTracking().FirstOrDefault(s => s.IsActive);
        if (subathon == null) return;

        TimeSpan elapsed = TimeSpan.FromMilliseconds(subathon.MillisecondsElapsed);
        TimeSpan totalAccumulated = TimeSpan.FromMilliseconds(subathon.MillisecondsCumulative);

        StatsElapsedTime.Text = FormatTimeSpan(elapsed);
        if (subathon.IsSubathonReversed())
        {
            StatsTotalAccumulated.IsVisible = false;
            StatsAccu.IsVisible = false;
        }
        else
        {
            StatsTotalAccumulated.IsVisible = true;
            StatsAccu.IsVisible = true;
            StatsTotalAccumulated.Text = FormatTimeSpan(totalAccumulated);
        }

        int eventCount = db.SubathonEvents.Count(ev => ev.SubathonId == subathon.Id &&
                                                       ev.Command == SubathonCommandType.None &&
                                                       ev.Source != SubathonEventSource.Simulated);
        StatsTotalEvents.Text = $"{eventCount:N0}";
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
