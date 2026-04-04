using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Events;
using SubathonManager.Core;
using SubathonManager.Core.Models;
using SubathonManager.Data;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI;

public partial class App
{
    private record SubathonTickState(Guid Id, bool IsPaused, bool IsReversed, double MultiplierValue, DateTime? MultiplierExpiry);
    private SubathonTickState? _cachedTickState;
    
    public static async void InitSubathonTimer()
    {
        var factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db =  await factory.CreateDbContextAsync();
        var subathon = await db.SubathonDatas.AsNoTracking().SingleOrDefaultAsync(x => x.IsActive);
        if (subathon != null) SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
    }
    
    private void UpdateTickStateCache(SubathonData data, DateTime _)
    {
        _cachedTickState = new SubathonTickState(
            data.Id,
            data.IsPaused,
            data.IsSubathonReversed(),
            data.Multiplier.Multiplier,
            data.Multiplier.Duration == null ? null : data.Multiplier.Started + data.Multiplier.Duration
        );
    }
    
    private async void UpdateSubathonTimers(TimeSpan time)
    {
        try
        {
            var state = _cachedTickState;
            if (state is null or { IsPaused: true, MultiplierValue: 1 }) return;

            await using var db = await _factory!.CreateDbContextAsync();

            int ran = -1;
            if (state.IsReversed)
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
                if (state.IsReversed)
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
        
            if (state.MultiplierExpiry != null && !state.MultiplierValue.Equals(1) && DateTime.Now >= state.MultiplierExpiry)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE MultiplierDatas SET Multiplier = 1, Duration = null WHERE SubathonId = {0}",
                    state.Id);
            }
        
            var snapshot = await db.SubathonDatas
                .Where(x => x.Id == state.Id && x.IsActive)
                .Include(x => x.Multiplier)
                .AsNoTracking()
                .FirstOrDefaultAsync();

            if (snapshot == null) return;
        
            if (snapshot.TimeRemainingRounded().TotalSeconds <= 0 && snapshot is { IsPaused: false, IsLocked: false })
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE SubathonDatas SET IsLocked = 1 WHERE IsActive = 1 AND IsPaused = 0 AND Id = {0}",
                    snapshot.Id);
                snapshot.IsLocked = true;
            }
        
            SubathonEvents.RaiseSubathonDataUpdate(snapshot, DateTime.Now);
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Failed to tick down timer");
        }
    }
}