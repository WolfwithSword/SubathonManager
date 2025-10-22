using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Events;
using SubathonManager.Data;

namespace SubathonManager.UI;

public partial class App
{
    
    public static async void InitSubathonTimer()
    {
        using var db = new AppDbContext();
        var subathon = await db.SubathonDatas.SingleOrDefaultAsync(x => x.IsActive);
        if (subathon != null) SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
    }

    private async void SetPowerHour(double value, TimeSpan? duration, bool applySeconds, bool applyPoints)
    {
        await AppDbContext.ResetPowerHour(new AppDbContext());
        
        using var db = new AppDbContext();
        var subathon = await db.SubathonDatas.Include(s=> s.Multiplier)
            .SingleOrDefaultAsync(x => x.IsActive);
        if (subathon == null) return;

        subathon.Multiplier.Multiplier = value;
        subathon.Multiplier.Duration = duration;
        subathon.Multiplier.Started = DateTime.Now;
        subathon.Multiplier.ApplyToPoints = applyPoints;
        subathon.Multiplier.ApplyToSeconds = applySeconds;

        await db.SaveChangesAsync();
        // if it is zero, we treat as a "power hour until cancelled or restarted"
    }
    
    private async void UpdateSubathonTimers(TimeSpan time)
    {
        using var db = new AppDbContext();
        await db.Database.ExecuteSqlRawAsync("UPDATE SubathonDatas SET MillisecondsElapsed = MillisecondsElapsed + {0}" +
                                             " WHERE IsActive = 1 AND IsPaused = 0 " +
                                             "AND MillisecondsCumulative - MillisecondsElapsed > 0", 
            time.TotalMilliseconds);
        
        var subathon = await db.SubathonDatas.Include(s=> s.Multiplier)
            .SingleOrDefaultAsync(x => x.IsActive &&(!x.IsPaused ||
                                                     x.Multiplier.Multiplier < 1 || x.Multiplier.Multiplier > 1));
        
        if (subathon != null)
        {
            if (subathon.TimeRemainingRounded().TotalSeconds <= 0 && !subathon.IsPaused)
            {
                await db.Database.ExecuteSqlRawAsync("UPDATE SubathonDatas SET IsLocked = 1" +
                                                     " WHERE IsActive = 1 AND IsPaused = 0 " +
                                                     "AND Id = {0}", 
                    subathon.Id);
            }

            if (subathon.Multiplier.Duration != null && (subathon.Multiplier.Multiplier < 1 || subathon.Multiplier.Multiplier > 1) 
                                       && DateTime.Now >= subathon.Multiplier.Started + subathon.Multiplier.Duration)
            {
                await db.Database.ExecuteSqlRawAsync("UPDATE MultiplierDatas SET Multiplier = 1, Duration = null " +
                                                     " WHERE SubathonId = {0}", 
                    subathon.Id);
            }

            await db.Entry(subathon).ReloadAsync();
            SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
        }
        // if (subathon != null) Console.WriteLine($"Subathon Timer Updated: {subathon.MillisecondsCumulative} {subathon.MillisecondsElapsed} {subathon.PredictedEndTime()} {subathon.TimeRemaining()}");
    }
}