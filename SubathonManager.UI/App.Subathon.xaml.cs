using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Events;
using SubathonManager.Core;
using SubathonManager.Data;

namespace SubathonManager.UI;

public partial class App
{
    
    public static async void InitSubathonTimer()
    {
        var factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db =  await factory.CreateDbContextAsync();
        var subathon = await db.SubathonDatas.AsNoTracking().SingleOrDefaultAsync(x => x.IsActive);
        if (subathon != null) SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
    }
    
    private async void UpdateSubathonTimers(TimeSpan time)
    {
        await using var db = await _factory!.CreateDbContextAsync();
        var subathon = await db.SubathonDatas.Include(s=> s.Multiplier)
            .SingleOrDefaultAsync(x => x.IsActive &&(!x.IsPaused ||
                                                     x.Multiplier.Multiplier < 1 || x.Multiplier.Multiplier > 1));

        if (subathon == null) return;
        int ran = -1;
        if (subathon.IsSubathonReversed())
        {
            ran = await db.Database.ExecuteSqlRawAsync(
                "UPDATE SubathonDatas SET MillisecondsElapsed = MillisecondsElapsed + {0} WHERE IsActive = 1 AND IsPaused = 0 AND MillisecondsElapsed + MillisecondsCumulative > 0",
                time.TotalMilliseconds);   
        }
        else
        {
            ran = await db.Database.ExecuteSqlRawAsync(
                "UPDATE SubathonDatas SET MillisecondsElapsed = MillisecondsElapsed + {0} WHERE IsActive = 1 AND IsPaused = 0 AND MillisecondsCumulative - MillisecondsElapsed > 0",
                time.TotalMilliseconds);
        }

        if (ran == 0)
        {
            // try to set to equal 0 if it would go negative
            if (subathon.IsSubathonReversed())
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE SubathonDatas SET MillisecondsElapsed = -MillisecondsCumulative WHERE IsActive = 1 AND IsPaused = 0 AND MillisecondsElapsed + MillisecondsCumulative + 1000 <= 0");
            }

            else
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE SubathonDatas SET MillisecondsElapsed = MillisecondsCumulative WHERE IsActive = 1 AND IsPaused = 0 AND MillisecondsCumulative - MillisecondsElapsed - 1000 <= 0");
            }
        }

        await db.Entry(subathon).ReloadAsync();

        if (subathon.TimeRemainingRounded().TotalSeconds <= 0 && !subathon.IsPaused)
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE SubathonDatas SET IsLocked = 1 WHERE IsActive = 1 AND IsPaused = 0 AND Id = {0}", 
                subathon.Id);
        }

        if (subathon.Multiplier.Duration != null && (subathon.Multiplier.Multiplier < 1 || subathon.Multiplier.Multiplier > 1) 
                                   && DateTime.Now >= subathon.Multiplier.Started + subathon.Multiplier.Duration)
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE MultiplierDatas SET Multiplier = 1, Duration = null  WHERE SubathonId = {0}", 
                subathon.Id);
        }

        await db.Entry(subathon).ReloadAsync();
        db.Entry(subathon).State = EntityState.Detached;
        db.Entry(subathon.Multiplier).State = EntityState.Detached;
        SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
    }
}