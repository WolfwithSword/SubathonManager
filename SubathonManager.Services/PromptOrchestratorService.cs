using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;

namespace SubathonManager.Services;

public class PromptOrchestratorService(
    IDbContextFactory<AppDbContext> factory,
    ITimerService timerService,
    ILogger<PromptOrchestratorService>? logger = null)
    : IDisposable, IAppService
{
    private readonly ILogger? _logger = logger;
 
    private const string KeyInterval = "prompt-interval";
    private const string KeyDuration = "prompt-duration";
    private const string KeyCooldown = "prompt-cooldown";
 
    private readonly Random _rng = new();
    private bool _subathonRunning = false;

    public Task StartAsync(CancellationToken ct = default)
    {
        SubathonEvents.SubathonDataUpdate += OnSubathonDataUpdate;
        SubathonEvents.SubathonEventProcessed += SubathonEventProcessed;
        SubathonEvents.PromptRunCancelRequested += OnCancelRequested;
        SubathonEvents.PromptRunNowRequested += OnRunNowRequested;
        SubathonEvents.PromptSetEnabledChanged += OnPromptSetEnabledChanged;
        return Task.CompletedTask;
    }
 
    public Task StopAsync(CancellationToken ct = default)
    {
        Dispose();
        return Task.CompletedTask;
    }
 
    public void Dispose()
    {
        SubathonEvents.SubathonDataUpdate -= OnSubathonDataUpdate;
        SubathonEvents.SubathonEventProcessed -= SubathonEventProcessed;
        SubathonEvents.PromptRunCancelRequested -= OnCancelRequested;
        SubathonEvents.PromptRunNowRequested -= OnRunNowRequested;
        SubathonEvents.PromptSetEnabledChanged -= OnPromptSetEnabledChanged;
        UnregisterAll();
    }
 
    private void UnregisterAll()
    {
        timerService.Unregister(KeyInterval);
        timerService.Unregister(KeyDuration);
        timerService.Unregister(KeyCooldown);
    }
    
    private async void OnPromptSetEnabledChanged(bool enabled)
    {
        if (enabled && _subathonRunning)
        {
            await TryStartSchedulerAsync();
            return;
        }

        UnregisterAll();

        if (!enabled)
        {
            await using var db = await factory.CreateDbContextAsync();
            var run = await db.SubathonPromptRuns
                .Include(r => r.LinkedPrompt)
                .FirstOrDefaultAsync(r => r.Status == SubathonPromptRunStatus.Active);

            if (run != null)
            {
                run.Status = SubathonPromptRunStatus.Cancelled;
                run.EndedAt = DateTime.Now;
                db.SubathonPromptRuns.Update(run);
                await db.SaveChangesAsync();
                SubathonEvents.RaisePromptRunUpdate(run, run.LinkedPrompt);
            }
        }
    }
    private void OnSubathonDataUpdate(SubathonData data, DateTime _)
    {
        bool shouldRun = data is { IsActive: true, IsPaused: false, IsLocked: false };
 
        if (shouldRun && !_subathonRunning)
        {
            _subathonRunning = true;
            Task.Run(async () => await TryStartSchedulerAsync());
        }
        else if (!shouldRun && _subathonRunning)
        {
            _subathonRunning = false;
            timerService.Unregister(KeyInterval);
        }
    }
    
    private async Task TryStartSchedulerAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
 
        var set = await db.SubathonPromptSets
            .Include(s => s.Prompts)
            .FirstOrDefaultAsync(s => s.IsActive);
 
        if (set is not { Enabled: true }) return;
 
        var activeRun = await db.SubathonPromptRuns
            .Include(r => r.LinkedPrompt)
            .FirstOrDefaultAsync(r => r.Status == SubathonPromptRunStatus.Active);
 
        if (activeRun != null)
        {
            if (activeRun.IsExpired)
            {
                ExpireRunAsync(db, activeRun, set);
                await db.SaveChangesAsync();
                StartCooldownOrInterval(set);
            }
            else
            {
                var remaining = activeRun.TimeRemaining();
                timerService.Unregister(KeyInterval);
                timerService.Unregister(KeyCooldown);
                timerService.Register(KeyDuration, remaining, async _ => await OnDurationElapsedAsync(activeRun.Id));
                _logger?.LogInformation("[PromptOrchestrator] Resumed active prompt run {Id}, {Remaining} remaining", activeRun.Id, remaining);
            }
            return;
        }
 
        StartIntervalTimer(set);
    }
    
    private void StartIntervalTimer(SubathonPromptSet set)
    {
        timerService.Unregister(KeyInterval);
        timerService.Unregister(KeyCooldown);
 
        var interval = BuildInterval(set);
        _logger?.LogInformation("[PromptOrchestrator] Prompt interval scheduled in {Interval}", interval);
        timerService.Register(KeyInterval, interval, async _ => await OnIntervalElapsedAsync());
    }
 
    private TimeSpan BuildInterval(SubathonPromptSet set)
    {
        if (set.RandomOffset == TimeSpan.Zero) return set.Interval;
        double offsetSeconds = _rng.NextDouble() * set.RandomOffset.TotalSeconds * 2 - set.RandomOffset.TotalSeconds;
        var result = set.Interval + TimeSpan.FromSeconds(offsetSeconds);
        return result < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : result;
    }
 
    private async Task OnIntervalElapsedAsync()
    {
        timerService.Unregister(KeyInterval);
 
        if (!_subathonRunning) return;
 
        await using var db = await factory.CreateDbContextAsync();
        var set = await db.SubathonPromptSets.Include(s => s.Prompts)
            .FirstOrDefaultAsync(s => s.IsActive);
 
        if (set == null || !set.Enabled) return;
 
        var pickable = set.PickablePrompts().ToList();
        if (pickable.Count == 0)
        {
            _logger?.LogInformation("[PromptOrchestrator] No pickable prompts available, rescheduling interval");
            StartIntervalTimer(set);
            return;
        }
 
        var chosen = pickable[_rng.Next(pickable.Count)];
        await StartRunAsync(db, set, chosen);
        await db.SaveChangesAsync();
    }
    
    private async Task StartRunAsync(AppDbContext db, SubathonPromptSet set, SubathonPrompt prompt)
    {
        timerService.Unregister(KeyInterval);
        timerService.Unregister(KeyDuration);
        timerService.Unregister(KeyCooldown);
 
        long baseline = await ComputeBaselineAsync(db, prompt);
 
        var run = new SubathonPromptRun
        {
            PromptId = prompt.Id,
            SetId = set.Id,
            StartedAt = DateTime.Now,
            ExpiresAt = DateTime.Now + prompt.CompletionDuration,
            Status = SubathonPromptRunStatus.Active,
            SnapshotTargetValue = prompt.Value,
            BaselineCount = baseline
        };
 
        db.SubathonPromptRuns.Add(run);
        _logger?.LogInformation("[PromptOrchestrator] Starting prompt run: {Text} (expires {Expires})", prompt.Text, run.ExpiresAt);
 
        timerService.Register(KeyDuration, prompt.CompletionDuration, async _ => await OnDurationElapsedAsync(run.Id));
 
        SubathonEvents.RaisePromptRunStarted(run, prompt);
    }
    
    private async Task<long> ComputeBaselineAsync(AppDbContext db, SubathonPrompt prompt)
    {
        var subathon = await db.SubathonDatas.AsNoTracking().FirstOrDefaultAsync(s => s.IsActive);
        if (subathon == null) return 0;
 
        return prompt.Type switch
        {
            SubathonPromptType.Points => subathon.Points,
            SubathonPromptType.Money => subathon.GetRoundedMoneySum(),
            SubathonPromptType.Follows => await CountEventsAsync(db, SubathonEventSubTypeHelper.FollowEventTypes),
            SubathonPromptType.Tokens => await CountTokensAsync(db),
            SubathonPromptType.Orders => await CountOrdersAsync(db, prompt),
            SubathonPromptType.Subs => await CountSubsAsync(db, prompt),
            SubathonPromptType.Event => await CountEventTypeAsync(db, prompt),
            _ => 0
        };
    }
    
    private async void SubathonEventProcessed(SubathonEvent ev, bool processed)
    {
        try
        {
            if (!processed) return;
            await using var db = await factory.CreateDbContextAsync();
 
            var run = await db.SubathonPromptRuns
                .Include(r => r.LinkedPrompt)
                .FirstOrDefaultAsync(r => r.Status == SubathonPromptRunStatus.Active);

            if (run is null) return;
            if (run?.LinkedPrompt == null) return;
            if (run.IsExpired) return;
 
            var prompt = run.LinkedPrompt;
            if (!EventMatchesPrompt(ev, prompt)) return;

            if (prompt.Type != SubathonPromptType.Money)
            {
                long delta = GetEventDelta(ev, prompt);
                if (delta == 0) return;
            }

            await using var db2 = await factory.CreateDbContextAsync();
            long current = await GetCurrentCountAsync(db2, prompt);
            long progress = current - run.BaselineCount;

            if (progress < run.SnapshotTargetValue)
            {
                SubathonEvents.RaisePromptRunProgressUpdated(run, progress);
                return;
            }
 
            timerService.Unregister(KeyDuration);

            run.Status = SubathonPromptRunStatus.Completed;
            run.EndedAt = DateTime.Now;
            db.SubathonPromptRuns.Update(run);
            await db.SaveChangesAsync();

            if (prompt is { IsInfinite: false, Quantity: > 0 })
            {
                await using var dbQty = await factory.CreateDbContextAsync();
                var trackedPrompt = await dbQty.SubathonPrompts.FindAsync(prompt.Id);
                if (trackedPrompt is { IsInfinite: false, Quantity: > 0 })
                {
                    trackedPrompt.Quantity--;
                    await dbQty.SaveChangesAsync();
                    prompt.Quantity = trackedPrompt.Quantity;
                }
            }
 
            _logger?.LogInformation("[PromptOrchestrator] Prompt run completed: {Text}", prompt.Text);
            SubathonEvents.RaisePromptRunUpdate(run, prompt);
 
            var set = await db.SubathonPromptSets.FindAsync(run.SetId);
            if (set != null) StartCooldownOrInterval(set);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[PromptOrchestrator] Error processing event for prompt run");
        }
    }
    
    private async Task OnDurationElapsedAsync(Guid runId)
    {
        timerService.Unregister(KeyDuration);
 
        await using var db = await factory.CreateDbContextAsync();
 
        var run = await db.SubathonPromptRuns
            .Include(r => r.LinkedPrompt)
            .FirstOrDefaultAsync(r => r.Id == runId);
 
        if (run == null || run.Status != SubathonPromptRunStatus.Active) return;
 
        var set = await db.SubathonPromptSets.FindAsync(run.SetId);
 
        ExpireRunAsync(db, run, set);
        await db.SaveChangesAsync();
 
        StartCooldownOrInterval(set);
    }
 
    private static void ExpireRunAsync(AppDbContext db, SubathonPromptRun run, SubathonPromptSet? set)
    {
        run.Status = SubathonPromptRunStatus.Expired;
        run.EndedAt = DateTime.Now;
        db.SubathonPromptRuns.Update(run);
        SubathonEvents.RaisePromptRunUpdate(run, run.LinkedPrompt);
    }
    
     
    private void StartCooldownOrInterval(SubathonPromptSet? set)
    {
        if (set is not { Enabled: true } || !_subathonRunning) return;
 
        if (set.Cooldown > TimeSpan.Zero)
        {
            _logger?.LogInformation("[PromptOrchestrator] Prompt cooldown: {Cooldown}", set.Cooldown);
            timerService.Register(KeyCooldown, set.Cooldown, async ct =>
            {
                timerService.Unregister(KeyCooldown);
                await using var db = await factory.CreateDbContextAsync(ct);
                var refreshedSet = await db.SubathonPromptSets.Include(s => s.Prompts)
                    .FirstOrDefaultAsync(s => s.Id == set.Id, cancellationToken: ct);
                if (refreshedSet is { Enabled: true } && _subathonRunning)
                    StartIntervalTimer(refreshedSet);
            });
        }
        else
        {
            StartIntervalTimer(set);
        }
    }
    
    private async void OnCancelRequested()
    {
        try
        {
            timerService.Unregister(KeyDuration);
 
            await using var db = await factory.CreateDbContextAsync();
            var run = await db.SubathonPromptRuns
                .Include(r => r.LinkedPrompt)
                .FirstOrDefaultAsync(r => r.Status == SubathonPromptRunStatus.Active);
 
            if (run == null) return;
 
            run.Status = SubathonPromptRunStatus.Cancelled;
            run.EndedAt = DateTime.Now;
            db.SubathonPromptRuns.Update(run);
            await db.SaveChangesAsync();
 
            _logger?.LogInformation("[PromptOrchestrator] Prompt run cancelled: {Id}", run.Id);
            SubathonEvents.RaisePromptRunUpdate(run, run.LinkedPrompt);
 
            var set = await db.SubathonPromptSets.FindAsync(run.SetId);
            StartCooldownOrInterval(set);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[PromptOrchestrator] Error cancelling prompt run");
        }
    }
 
    private async void OnRunNowRequested(Guid promptId)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();
 
            var existing = await db.SubathonPromptRuns
                .FirstOrDefaultAsync(r => r.Status == SubathonPromptRunStatus.Active);
            if (existing != null)
            {
                timerService.Unregister(KeyDuration);
                existing.Status = SubathonPromptRunStatus.Cancelled;
                existing.EndedAt = DateTime.Now;
                db.SubathonPromptRuns.Update(existing);
                SubathonEvents.RaisePromptRunUpdate(existing, null);
            }
 
            var prompt = await db.SubathonPrompts.FindAsync(promptId);
            var set = await db.SubathonPromptSets.Include(s => s.Prompts)
                .FirstOrDefaultAsync(s => s.IsActive);
 
            if (prompt == null || set == null) return;
 
            timerService.Unregister(KeyInterval);
            timerService.Unregister(KeyCooldown);
 
            await StartRunAsync(db, set, prompt);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[PromptOrchestrator] Error in RunNow for prompt {Id}", promptId);
        }
    }
     private static bool EventMatchesPrompt(SubathonEvent ev, SubathonPrompt prompt)
    {
        if (ev.EventType == null) return false;
 
        return prompt.Type switch
        {
            SubathonPromptType.Points => ev.PointsValue > 0,
            SubathonPromptType.Money => ev.EventType.IsCurrencyDonation(),
            SubathonPromptType.Follows => ev.EventType.IsFollow(),
            SubathonPromptType.Tokens => ev.EventType.IsToken(),
            SubathonPromptType.Orders => ev.EventType.IsOrder(),
            SubathonPromptType.Subs => prompt.SubType switch
            {
                SubathonPromptSubType.NormalSubs => ev.EventType.IsSubscription() && !ev.EventType.IsGift(),
                SubathonPromptSubType.GiftSubs => ev.EventType.IsGift(),
                _ => ev.EventType.IsSubscription()
            },
            SubathonPromptType.Event => MatchesEventType(ev, prompt),
            _ => false
        };
    }
 
    private static bool MatchesEventType(SubathonEvent ev, SubathonPrompt prompt)
    {
        if (ev.EventType != prompt.FilterEventType) return false;

        if (prompt.FilterEventType is SubathonEventType.GoAffProOrder or SubathonEventType.JuniperMerchSale
            or SubathonEventType.MakeShipPledge or SubathonEventType.MakeShipSale)
            return OrderMetaFilter.Matches(ev.EventType, ev.EventTypeMeta, prompt.FilterMeta);

        if (prompt.SubType == SubathonPromptSubType.ByTier && !string.IsNullOrEmpty(prompt.FilterMeta))
            return ev.Value == prompt.FilterMeta;

        return true;
    }
 
    private static long GetEventDelta(SubathonEvent ev, SubathonPrompt prompt)
    {
        return prompt.Type switch
        {
            SubathonPromptType.Points => ev.PointsValue ?? 0,
            SubathonPromptType.Money => 0,
            SubathonPromptType.Follows => 1,
            SubathonPromptType.Tokens => long.TryParse(ev.Value, out var t) ? t : 0,
            SubathonPromptType.Orders => prompt.SubType == SubathonPromptSubType.Items ? ev.Amount : 1,
            SubathonPromptType.Subs => ev.Amount,
            SubathonPromptType.Event => prompt.SubType switch
            {
                SubathonPromptSubType.Items => ev.Amount,
                SubathonPromptSubType.ByTier => ev.Amount,
                _ => 1
            },
            _ => 0
        };
    }
    
    private static async Task<long> CountEventsAsync(AppDbContext db, IEnumerable<SubathonEventType> types)
    {
        var typeList = types.Cast<SubathonEventType?>().ToList();
        return await db.SubathonEvents.AsNoTracking()
            .Where(e => e.ProcessedToSubathon && typeList.Contains(e.EventType))
            .CountAsync();
    }
 
    private static async Task<long> CountTokensAsync(AppDbContext db)
    {
        var types = SubathonEventSubTypeHelper.TokenEventTypes.Cast<SubathonEventType?>().ToList();
        var rows = await db.SubathonEvents.AsNoTracking()
            .Where(e => e.ProcessedToSubathon && types.Contains(e.EventType))
            .Select(e => e.Value)
            .ToListAsync();
        return rows.Sum(v => long.TryParse(v, out var n) ? n : 0);
    }
 
    private static async Task<long> CountOrdersAsync(AppDbContext db, SubathonPrompt prompt)
    {
        var types = SubathonEventSubTypeHelper.OrderEventTypes.Cast<SubathonEventType?>().ToList();
        if (prompt.SubType == SubathonPromptSubType.Items)
        {
            return await db.SubathonEvents.AsNoTracking()
                .Where(e => e.ProcessedToSubathon && types.Contains(e.EventType))
                .SumAsync(e => (long)e.Amount);
        }
        return await db.SubathonEvents.AsNoTracking()
            .Where(e => e.ProcessedToSubathon && types.Contains(e.EventType))
            .CountAsync();
    }
 
    private static async Task<long> CountSubsAsync(AppDbContext db, SubathonPrompt prompt)
    {
        var subTypes = SubathonEventSubTypeHelper.SubEventTypes.Cast<SubathonEventType?>()
            .Where(e => prompt.SubType == SubathonPromptSubType.GiftSubs ? e.IsGift() : 
                (prompt.SubType != SubathonPromptSubType.NormalSubs || !e.IsGift())).ToList();
        var query = db.SubathonEvents.AsNoTracking();
        query = query.Where(e => subTypes.Contains(e.EventType));

        return await query.SumAsync(e => (long)e.Amount);
    }
 
    private static async Task<long> CountEventTypeAsync(AppDbContext db, SubathonPrompt prompt)
    {
        if (prompt.FilterEventType == null) return 0;
 
        var filterType = prompt.FilterEventType;
        var query = db.SubathonEvents.AsNoTracking()
            .Where(e => e.ProcessedToSubathon && e.EventType == filterType);

        if (filterType is SubathonEventType.GoAffProOrder or SubathonEventType.JuniperMerchSale
                or SubathonEventType.MakeShipPledge or SubathonEventType.MakeShipSale
            && !string.IsNullOrEmpty(prompt.FilterMeta))
        {
            if (filterType == SubathonEventType.JuniperMerchSale && Guid.TryParse(prompt.FilterMeta, out var storeId))
            {
                List<string> productMetas = JuniperStoreRegistry.TryGetStore(storeId, out var store)
                    ? store.Products.Select(p => p.ProductId.ToString()).ToList()
                    : [];
                query = query.Where(e => e.EventTypeMeta != null && productMetas.Contains(e.EventTypeMeta));
            }
            else
            {
                query = query.Where(e => e.EventTypeMeta == prompt.FilterMeta);
            }
        }
        else if (prompt.SubType == SubathonPromptSubType.ByTier && !string.IsNullOrEmpty(prompt.FilterMeta))
            query = query.Where(e => e.Value == prompt.FilterMeta);
 
        if (prompt.SubType == SubathonPromptSubType.Items)
            return await query.SumAsync(e => (long)e.Amount);
 
        return await query.SumAsync(e => (long)e.Amount);
    }
 
    public static async Task<long> GetCurrentCountAsync(AppDbContext db, SubathonPrompt prompt)
    {
        var subathon = await db.SubathonDatas.AsNoTracking().FirstOrDefaultAsync(s => s.IsActive);
        if (subathon == null) return 0;
 
        return prompt.Type switch
        {
            SubathonPromptType.Points => subathon.Points,
            SubathonPromptType.Money => subathon.GetRoundedMoneySum(),
            SubathonPromptType.Follows => await CountEventsAsync(db, SubathonEventSubTypeHelper.FollowEventTypes),
            SubathonPromptType.Tokens => await CountTokensAsync(db),
            SubathonPromptType.Orders => await CountOrdersAsync(db, prompt),
            SubathonPromptType.Subs => await CountSubsAsync(db, prompt),
            SubathonPromptType.Event => await CountEventTypeAsync(db, prompt),
            _ => 0
        };
    }
}