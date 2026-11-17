using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;

namespace SubathonManager.Services;

public class SubathonTimerService(
    IDbContextFactory<AppDbContext> factory,
    ILogger<SubathonTimerService>? logger = null)
    : IDisposable, IAppService {
    private SubathonTickState? _cachedTickState;

    public async Task StartAsync(CancellationToken ct = default) {
        SubathonEvents.SubathonDataUpdate += UpdateTickStateCache;
        TimerEvents.TimerTickEvent += OnTimerTick;

        await using AppDbContext db = await factory.CreateDbContextAsync(ct);
        SubathonData? subathon = await db.SubathonDatas.AsNoTracking()
            .Include(x => x.Multiplier)
            .SingleOrDefaultAsync(x => x.IsActive, ct);
        if (subathon != null) SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
    }

    public Task StopAsync(CancellationToken ct = default) {
        Unsubscribe();
        return Task.CompletedTask;
    }

    public void Dispose() {
        Unsubscribe();
    }

    private void Unsubscribe() {
        SubathonEvents.SubathonDataUpdate -= UpdateTickStateCache;
        TimerEvents.TimerTickEvent -= OnTimerTick;
    }

    private void UpdateTickStateCache(SubathonData data, DateTime _) {
        _cachedTickState = new SubathonTickState(
            data.Id,
            data.IsPaused,
            data.IsSubathonReversed(),
            data.Multiplier.Multiplier,
            data.Multiplier.Duration == null ? null : data.Multiplier.Started + data.Multiplier.Duration,
            data.CapDateTime
        );
    }

    private async void OnTimerTick(TimeSpan time) {
        await TickAsync(time);
    }

    public async Task TickAsync(TimeSpan time) {
        try {
            SubathonTickState? state = _cachedTickState;
            switch (state) {
                case null:
                case { IsPaused: true, MultiplierValue: 1, Cap: null }:
                    return;
            }

            await using AppDbContext db = await factory.CreateDbContextAsync();

            if (state is { IsPaused: false }) {
                int ran;
                if (state.IsReversed)
                    ran = await db.Database.ExecuteSqlRawAsync(
                        "UPDATE SubathonDatas SET MillisecondsElapsed = MillisecondsElapsed + {0} WHERE IsActive = 1 AND IsPaused = 0 AND MillisecondsElapsed + MillisecondsCumulative > 0",
                        time.TotalMilliseconds);
                else
                    ran = await db.Database.ExecuteSqlRawAsync(
                        "UPDATE SubathonDatas SET MillisecondsElapsed = MillisecondsElapsed + {0} WHERE IsActive = 1 AND IsPaused = 0 AND MillisecondsCumulative - MillisecondsElapsed > 0",
                        time.TotalMilliseconds);

                if (ran == 0) {
                    if (state.IsReversed)
                        await db.Database.ExecuteSqlRawAsync(
                            "UPDATE SubathonDatas SET MillisecondsElapsed = -MillisecondsCumulative WHERE IsActive = 1 AND IsPaused = 0 AND MillisecondsElapsed + MillisecondsCumulative + 1000 <= 0");
                    else
                        await db.Database.ExecuteSqlRawAsync(
                            "UPDATE SubathonDatas SET MillisecondsElapsed = MillisecondsCumulative WHERE IsActive = 1 AND IsPaused = 0 AND MillisecondsCumulative - MillisecondsElapsed - 1000 <= 0");
                }
            }

            if (state.MultiplierExpiry != null && !state.MultiplierValue.Equals(1) &&
                DateTime.Now >= state.MultiplierExpiry)
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE MultiplierDatas SET Multiplier = 1, Duration = null WHERE SubathonId = {0}",
                    state.Id);

            SubathonData? snapshot = await db.SubathonDatas
                .Where(x => x.Id == state.Id && x.IsActive)
                .Include(x => x.Multiplier)
                .AsNoTracking()
                .FirstOrDefaultAsync();

            if (snapshot == null) return;

            if (snapshot.TimeRemainingRounded().TotalSeconds <= 0 && snapshot is { IsPaused: false, IsLocked: false }) {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE SubathonDatas SET IsLocked = 1 WHERE IsActive = 1 AND IsPaused = 0 AND Id = {0}",
                    snapshot.Id);
                snapshot.IsLocked = true;
            }
            else if (snapshot.CapDateTime != null && DateTime.Now >= snapshot.CapDateTime &&
                     snapshot is { IsPaused: false } or { IsLocked: false }) {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE SubathonDatas SET IsLocked = 1, IsPaused = 1 WHERE IsActive = 1 AND Id = {0} AND (IsLocked = 0 OR IsPaused = 0)",
                    snapshot.Id);
                snapshot.IsLocked = true;
                snapshot.IsPaused = true;
            }

            SubathonEvents.RaiseSubathonDataUpdate(snapshot, DateTime.Now);
        }
        catch (Exception e) {
            logger?.LogError(e, "Failed to tick down timer");
        }
    }

    private record SubathonTickState(
        Guid Id,
        bool IsPaused,
        bool IsReversed,
        double MultiplierValue,
        DateTime? MultiplierExpiry,
        DateTime? Cap);
}