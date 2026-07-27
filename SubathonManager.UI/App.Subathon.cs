using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;

namespace SubathonManager.UI;

public partial class App
{
    private record SubathonTickState(Guid Id, bool IsPaused, bool IsReversed, double MultiplierValue, DateTime? MultiplierExpiry, DateTime? Cap);
    private SubathonTickState? _cachedTickState;

    public static async void InitSubathonTimer()
    {
        var factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
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
            data.Multiplier.Duration == null ? null : data.Multiplier.Started + data.Multiplier.Duration,
            data.CapDateTime
        );
    }

    private async void UpdateSubathonTimers(TimeSpan time)
    {
        try
        {
            var state = _cachedTickState;
            if (state is null) return;
            if (state is { IsPaused: true, MultiplierValue: 1, Cap: null }) return;

            await using var db = await _factory!.CreateDbContextAsync();

            if (state is { IsPaused: false })
            {
                int ran;
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
            else if (snapshot.CapDateTime != null && DateTime.Now >= snapshot.CapDateTime && snapshot is { IsPaused: false })
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE SubathonDatas SET IsLocked = 1 WHERE IsActive = 1 AND IsPaused = 1 AND Id = {0}",
                    snapshot.Id);
                snapshot.IsLocked = true;
                snapshot.IsPaused = true;
            }

            SubathonEvents.RaiseSubathonDataUpdate(snapshot, DateTime.Now);
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Failed to tick down timer");
        }
    }

    private void WireRegistryPersistence()
    {
        GoAffProStoreRegistry.StoreDiscovered += store =>
        {
            Task.Run(async () =>
            {
                try
                {
                    await using var db2 = await _factory!.CreateDbContextAsync();
                    if (!db2.GoAffProStores.Any(s => s.SiteId == store.SiteId))
                        db2.GoAffProStores.Add(store);
                    var meta = store.SiteId.ToString();
                    if (!db2.SubathonValues.Any(sv => sv.EventType == SubathonEventType.GoAffProOrder && sv.Meta == meta))
                        db2.SubathonValues.Add(new SubathonValue
                            { EventType = SubathonEventType.GoAffProOrder, Seconds = 12, Meta = meta });
                    await db2.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to persist discovered GoAffPro store {SiteId}", store.SiteId);
                }
                var cfg = AppServices.Provider.GetRequiredService<IConfig>();
                Utils.DonationSettings[store.InternalName] =
                    cfg.GetBool("GoAffPro", $"{store.InternalName}.CommissionAsDonation", false);
            });
        };

        MakeShipTrackingRegistry.TrackingUpdated += tracking =>
        {
            Task.Run(async () =>
            {
                try
                {
                    await using var db2 = await _factory!.CreateDbContextAsync();
                    var row = db2.MakeShipTrackings.FirstOrDefault(t => t.Id == tracking.Id);
                    if (row == null) return;
                    string oldName = row.Name;
                    row.Name = tracking.Name;
                    row.ShopifyProductId = tracking.ShopifyProductId;
                    row.ProductType = tracking.ProductType;
                    row.Sales = tracking.Sales;
                    row.Orders = tracking.Orders;
                    if (!string.IsNullOrWhiteSpace(oldName) && oldName != "DEFAULT" &&
                        !string.Equals(oldName, tracking.Name, StringComparison.Ordinal))
                    {
                        var overrides = db2.SubathonValues.Where(sv =>
                            (sv.EventType == SubathonEventType.MakeShipPledge ||
                             sv.EventType == SubathonEventType.MakeShipSale)
                            && sv.Meta == oldName).ToList();
                        foreach (var sv in overrides)
                            sv.Meta = tracking.Name;

                        var triggers = db2.WheelSpinTriggers.Where(t =>
                            (t.EventType == SubathonEventType.MakeShipPledge ||
                             t.EventType == SubathonEventType.MakeShipSale)
                            && t.TierValue == oldName).ToList();
                        foreach (var t in triggers)
                            t.TierValue = tracking.Name;

                        var prompts = db2.SubathonPrompts.Where(p =>
                            (p.FilterEventType == SubathonEventType.MakeShipPledge ||
                             p.FilterEventType == SubathonEventType.MakeShipSale)
                            && p.FilterMeta == oldName).ToList();
                        foreach (var p in prompts)
                            p.FilterMeta = tracking.Name;
                    }
                    await db2.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to persist MakeShip tracking {Id}", tracking.Id);
                }
            });
        };

        JuniperStoreRegistry.StoreUpdated += store =>
        {
            Task.Run(async () =>
            {
                try
                {
                    await using var db2 = await _factory!.CreateDbContextAsync();
                    var row = db2.JuniperStores.FirstOrDefault(s => s.RowId == store.RowId);
                    if (row == null) return;
                    row.Enabled = store.Enabled;
                    row.LastFetched = store.LastFetched;
                    await db2.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to persist Juniper store {Name}", store.StoreName);
                }
            });
        };

        JuniperStoreRegistry.ProductUpdated += product =>
        {
            Task.Run(async () =>
            {
                try
                {
                    await using var db2 = await _factory!.CreateDbContextAsync();
                    var row = db2.JuniperProducts.FirstOrDefault(p => p.ProductId == product.ProductId);
                    if (row == null) return;
                    row.ProductName = product.ProductName;
                    row.Valid = product.Valid;
                    row.LastFetched = product.LastFetched;
                    await db2.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to persist Juniper product {Id}", product.ProductId);
                }
            });
        };
    }
}
