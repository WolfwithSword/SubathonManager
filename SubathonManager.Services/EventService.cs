using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
namespace SubathonManager.Services;

// We don't need one of these for Subathon updates, because those fire every second anyways
public class EventService: IDisposable
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CancellationTokenSource _cts = new();
    private readonly PriorityQueue<SubathonEvent, DateTime> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Lock _lock = new();
    private Task? _processingTask;
    private readonly CurrencyService _currencyService;
    private readonly ILogger<EventService>? _logger;
    private readonly IConfig _config;

    public EventService(IDbContextFactory<AppDbContext> factory, ILogger<EventService>? logger, IConfig config,
        CurrencyService currencyService)
    {
        _factory = factory;
        _logger = logger;
        _config = config;
        _currencyService = currencyService;
        
        Core.Events.SubathonEvents.SubathonEventCreated += AddSubathonEvent;
        _processingTask = Task.Run(LoopAsync);
        _processingTask.ContinueWith(t => 
                _logger?.LogError($"Event loop crashed: {t.Exception}", t.Exception), 
            TaskContinuationOptions.OnlyOnFaulted);
        Task.Run(() =>_currencyService.StartAsync());
        _logger?.LogInformation("EventService started");
    }

    public List<string> ValidEventCurrencies()
    {
        return Task.Run(() => _currencyService.GetValidCurrenciesAsync()).Result;
    }

    public void ReInitCurrencyService()
    {
        Task.Run(() => _currencyService.StartAsync());
    }

    private void AddSubathonEvent(SubathonEvent subathonEvent)
    {
        lock (_lock)
        {
            _queue.Enqueue(subathonEvent, subathonEvent.EventTimestamp);
        }

        _signal.Release();
    }
    
    public async Task LoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                await _signal.WaitAsync(_cts.Token);
                SubathonEvent? next = null;
                lock (_lock)
                {
                    if (_queue.TryDequeue(out var item, out _))
                    {
                        next = item;
                    }
                }

                if (next == null) continue;
                (bool wasEffective, bool dupeUneeded) = await ProcessSubathonEvent(next);
                if (!dupeUneeded)
                    SubathonEvents.RaiseSubathonEventProcessed(next, wasEffective);
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger?.LogError($"Error in Event Loop: {ex.Message}", ex);
        }
    }
    
    public async Task<(bool, bool)> ProcessSubathonEvent(SubathonEvent ev) // effective, dupe-was-processed
    {
        await using var db = await _factory.CreateDbContextAsync();
        SubathonData? subathon = await db.SubathonDatas.Include(s => s.Multiplier)
            .FirstOrDefaultAsync(s => s.IsActive);
        SubathonEvent? dupeCheck = await db.SubathonEvents.AsNoTracking().SingleOrDefaultAsync(s => s.Id == ev.Id 
            && s.Source == ev.Source);
        if (dupeCheck != null && dupeCheck.ProcessedToSubathon) return (false, true);

        int initialPoints = subathon?.Points ?? 0;
        if (subathon != null && ev.EventType == SubathonEventType.Command && ev.Command != SubathonCommandType.None)
        {
            // we allow commands to add even if locked
            int ranCmd = int.MinValue;
            switch (ev.Command)
            {
                case SubathonCommandType.SetPoints:
                    if (ev.PointsValue < 0) return (false, false);
                    ranCmd = await db.Database.ExecuteSqlRawAsync(
                        "UPDATE SubathonDatas SET Points = {1} WHERE IsActive = 1 AND Id = {0}", 
                        subathon.Id, ev.PointsValue!);
                    break;
                case SubathonCommandType.AddPoints:
                    if (ev.PointsValue < 1) return (false, false);
                    ranCmd = await db.Database.ExecuteSqlRawAsync(
                        "UPDATE SubathonDatas SET Points = Points + {1} WHERE IsActive = 1 AND Id = {0}", 
                        subathon.Id, ev.PointsValue!);
                    break;
                case SubathonCommandType.SubtractPoints:
                    if (ev.PointsValue < 1) return (false, false);
                    ranCmd = await db.Database.ExecuteSqlRawAsync(
                        "UPDATE SubathonDatas SET Points = Points - {1} WHERE IsActive = 1 AND Id = {0}", 
                        subathon.Id, ev.PointsValue!);
                    break;
                case SubathonCommandType.SetTime:
                    ranCmd = await db.Database.ExecuteSqlRawAsync(
                        "UPDATE SubathonDatas SET MillisecondsCumulative = MillisecondsElapsed + {1} WHERE IsActive = 1 AND Id = {0}", 
                        subathon.Id, ev.SecondsValue! * 1000);
                    break;
                case SubathonCommandType.AddTime:
                    ranCmd = await db.Database.ExecuteSqlRawAsync(
                        "UPDATE SubathonDatas SET MillisecondsCumulative = MillisecondsCumulative + {1} WHERE IsActive = 1 AND Id = {0}", 
                        subathon.Id, ev.SecondsValue! * 1000);
                    break;
                case SubathonCommandType.SubtractTime:
                    ranCmd = await db.Database.ExecuteSqlRawAsync(
                        "UPDATE SubathonDatas SET MillisecondsCumulative = MillisecondsCumulative - {1} WHERE IsActive = 1 AND Id = {0}",
                        subathon.Id, ev.SecondsValue! * 1000);
                    break;
                case SubathonCommandType.Unlock:
                    ranCmd = await db.Database.ExecuteSqlRawAsync(
                        "UPDATE SubathonDatas SET IsLocked = 0 WHERE IsActive = 1 AND MillisecondsCumulative - MillisecondsElapsed > 0 AND Id = {0}", 
                        subathon.Id);
                    break;
                case SubathonCommandType.Lock:
                    ranCmd = await db.Database.ExecuteSqlRawAsync(
                        "UPDATE SubathonDatas SET IsLocked = 1 WHERE IsActive = 1 AND MillisecondsCumulative - MillisecondsElapsed > 0 AND Id = {0}", 
                        subathon.Id);
                    break;
                case SubathonCommandType.Resume:
                    ranCmd = await db.Database.ExecuteSqlRawAsync(
                        "UPDATE SubathonDatas SET IsPaused = 0 WHERE IsActive = 1 AND MillisecondsCumulative - MillisecondsElapsed > 0 AND Id = {0}", 
                        subathon.Id);
                    break;
                case SubathonCommandType.Pause:
                    ranCmd = await db.Database.ExecuteSqlRawAsync(
                        "UPDATE SubathonDatas SET IsPaused = 1 WHERE IsActive = 1 AND MillisecondsCumulative - MillisecondsElapsed > 0 AND Id = {0}", 
                        subathon.Id);
                    break;
                case SubathonCommandType.StopMultiplier:
                    ranCmd = await db.Database.ExecuteSqlRawAsync("UPDATE MultiplierDatas SET Multiplier = 1, Duration = null, ApplyToSeconds = {1}, ApplyToPoints = {2}, FromHypeTrain = {3} WHERE  SubathonId = {0}",
                        subathon.Id, false, false, false);
                    
                    if (string.IsNullOrEmpty(ev.Value)) 
                        ev.Value = $"{ev.Command}";
                    break;
                case SubathonCommandType.SetMultiplier:
                    // string dataStr = $"{parsedAmt}|{durationStr}s|{applyPts}|{applyTime}";
                    string[] data = ev.Value.Split("|");
                    if (!double.TryParse(data[0], out var parsedAmt))
                        return (false, false);
                    TimeSpan? duration;
                    if (data[1] == "xs")
                        duration = null;
                    else
                        duration = Utils.ParseDurationString(data[1]);
                    if (!bool.TryParse(data[2], out var applyPts))
                        return (false, false);
                    if (!bool.TryParse(data[3], out var applyTime))
                        return (false, false);

                    ev.Value = $"{ev.Command} {ev.Value}";
                    ranCmd = await db.Database.ExecuteSqlRawAsync("UPDATE MultiplierDatas SET Multiplier = {0}, Duration = {1}, Started = {2}, ApplyToSeconds = {3}, ApplyToPoints = {4}, FromHypeTrain = {6} WHERE SubathonId = {5}",
                        parsedAmt, duration!, DateTime.Now, applyTime, applyPts, subathon.Id, false);
                    break;
            }

            if (ranCmd >= 0)
            {
                await db.Entry(subathon.Multiplier).ReloadAsync();
                await db.Entry(subathon).ReloadAsync();
                db.Entry(subathon).State = EntityState.Detached;
                db.Entry(subathon.Multiplier).State = EntityState.Detached;
                SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
                ev.ProcessedToSubathon = true;
                ev.SubathonId = subathon.Id;   
                ev.CurrentTime = (int)subathon.TimeRemaining().TotalSeconds;
                ev.CurrentPoints = subathon.Points;

                db.Add(ev);
                await db.SaveChangesAsync();
                await CheckForGoalChange(db, subathon.Points, initialPoints);
                return (true, false);
            }
        }
        
        if (subathon == null || subathon.IsLocked) // we only do math if it's unlocked, otherwise, only link
        {
            ev.ProcessedToSubathon = false;
            if (subathon != null)
            {
                ev.SubathonId = subathon.Id;
                ev.CurrentTime = (int)subathon.TimeRemaining().TotalSeconds;
                ev.CurrentPoints = subathon.Points;
            }
            db.Add(ev);
            await db.SaveChangesAsync();
            
            return (false, false);
        }
        
        ev.SubathonId = subathon.Id;
        ev.CurrentTime = (int)subathon.TimeRemaining().TotalSeconds;
        ev.CurrentPoints = subathon.Points;
        
        if (ev.EventType == SubathonEventType.TwitchHypeTrain)
        {
            if (bool.TryParse(_config.Get("Twitch", "HypeTrainMultiplier.Enabled", "false"),
                    out var doHypeTrainMult) && doHypeTrainMult && 
                (!subathon.Multiplier.IsRunning() || subathon.Multiplier.FromHypeTrain))
            {
                if (ev.Value == "start" || ev.Value == "progress")
                {
                    TimeSpan? duration = null;
                    if (!(subathon.Multiplier.IsRunning()
                          || !double.TryParse(_config.Get("Twitch", "HypeTrainMultiplier.Multiplier", "1"),
                              out var parsedAmt)
                          || parsedAmt.Equals(1)
                          || !bool.TryParse(_config.Get("Twitch", "HypeTrainMultiplier.Points", "false"),
                              out var applyPts)
                          || !bool.TryParse(_config.Get("Twitch", "HypeTrainMultiplier.Time", "false"),
                              out var applyTime)))
                    {
                        if (applyTime || applyPts)
                        {

                            ev.Value = $"start | x{parsedAmt}" + (applyPts ? " Points" : "") +
                                       (applyTime ? " Time" : "");

                            await db.Database.ExecuteSqlRawAsync(
                                "UPDATE MultiplierDatas SET Multiplier = {0}, Duration = {1}, Started = {2}, ApplyToSeconds = {3}, ApplyToPoints = {4}, FromHypeTrain = {6} WHERE SubathonId = {5}",
                                parsedAmt, duration!, DateTime.Now, applyTime, applyPts, subathon.Id, true);
                        }
                    }
                }
                else if (ev.Value == "end" && subathon.Multiplier.FromHypeTrain && subathon.Multiplier.IsRunning())
                {
                    // do multiplier end 
                    await db.Database.ExecuteSqlRawAsync(
                        "UPDATE MultiplierDatas SET Multiplier = 1, Duration = null, ApplyToSeconds = {1}, ApplyToPoints = {2}, FromHypeTrain = {3} WHERE  SubathonId = {0}",
                        subathon.Id, false, false, false);
                    await db.Entry(subathon.Multiplier).ReloadAsync();
                    await db.Entry(subathon).ReloadAsync();
                    db.Entry(subathon).State = EntityState.Detached;
                    db.Entry(subathon.Multiplier).State = EntityState.Detached;
                    SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
                }
            }
            ev.ProcessedToSubathon = true;
            db.Add(ev);
            await db.SaveChangesAsync();
            return (ev.ProcessedToSubathon, false);
        }
        
        ev.MultiplierSeconds = subathon.Multiplier.ApplyToSeconds ? subathon.Multiplier.Multiplier : 1;
        ev.MultiplierPoints = subathon.Multiplier.ApplyToPoints ? subathon.Multiplier.Multiplier : 1;

        SubathonValue? subathonValue = null;
        if (ev.EventType != SubathonEventType.Command && ev.EventType != SubathonEventType.ExternalSub)
        {
            subathonValue = await db.SubathonValues.FirstOrDefaultAsync(v =>
                v.EventType == ev.EventType && (v.Meta == ev.Value || v.Meta == string.Empty));

            subathonValue ??= await db.SubathonValues.FirstOrDefaultAsync(v =>
                    v.EventType == ev.EventType && (v.Meta == "DEFAULT"));

            if (subathonValue == null)
                return (false, false);

        }

        if (ev.EventType != SubathonEventType.Command && ev.EventType != SubathonEventType.ExternalSub)
        {
            ev.PointsValue = subathonValue!.Points;
            if (double.TryParse(ev.Value, out var parsedValue) && ev.Currency != "sub" && ev.Currency != "member"
                && !_currencyService.IsValidCurrency(ev.Currency)
                && !string.IsNullOrEmpty(ev.Value.Trim()))
            {
                ev.SecondsValue = parsedValue * subathonValue.Seconds;
            }
            else if (!string.IsNullOrEmpty(ev.Currency) && "sub,member,viewers,bits".Split(",").Contains(ev.Currency))
            {
                ev.SecondsValue = subathonValue.Seconds;
            }
            else if (!string.IsNullOrEmpty(ev.Currency) && _currencyService.IsValidCurrency(ev.Currency)
                     && ev.EventType.IsCurrencyDonation())
            {
                double rate = Task.Run(() =>
                    _currencyService.ConvertAsync(double.Parse(ev.Value), ev.Currency)).Result;
                ev.SecondsValue = Math.Round(subathonValue.Seconds * rate, 2);
                ev.PointsValue = (int) Math.Floor((double) ev.PointsValue! * rate);
            }
            else if (ev.EventType.IsCurrencyDonation() && (string.IsNullOrEmpty(ev.Currency) ||
                                                           !_currencyService.IsValidCurrency(ev.Currency)))
            {
                ev.PointsValue = 0;
                ev.SecondsValue = 0;
                ev.ProcessedToSubathon = false;
                if (string.IsNullOrEmpty(ev.Currency))
                    ev.Currency = "???";
            }
            else
                ev.SecondsValue = subathonValue.Seconds;
        }
        
        int affected = 0;
        if (ev.SecondsValue != 0)
            affected += await db.Database.ExecuteSqlRawAsync(
                "UPDATE SubathonDatas SET MillisecondsCumulative = MillisecondsCumulative + {0} WHERE IsActive = 1 AND IsLocked = 0 AND Id = {1} AND MillisecondsCumulative - MillisecondsElapsed > 0", 
               (int) TimeSpan.FromSeconds(ev.GetFinalSecondsValueRaw()).TotalMilliseconds, subathon.Id);
        
        if (ev.PointsValue != 0)
            affected += await db.Database.ExecuteSqlRawAsync(
                "UPDATE SubathonDatas SET Points = Points + {0} WHERE IsActive = 1 AND IsLocked = 0 AND Id = {1} AND MillisecondsCumulative - MillisecondsElapsed > 0", 
                (int) ev.GetFinalPointsValue(), subathon.Id);

        if (ev.PointsValue == 0 && ev.SecondsValue == 0 && ev.Currency != "???")
        {
            ev.ProcessedToSubathon = true;
        }
        
        if (affected > 0)
        {
            ev.ProcessedToSubathon = true;
            await db.Entry(subathon.Multiplier).ReloadAsync();
            await db.Entry(subathon).ReloadAsync();
            await CheckForGoalChange(db, subathon.Points, initialPoints);
            db.Entry(subathon).State = EntityState.Detached;
            db.Entry(subathon.Multiplier).State = EntityState.Detached;
            SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
        }

        if (dupeCheck != null)
            db.Update(ev);
        else 
            db.Add(ev);
        
        await db.SaveChangesAsync();
        return (affected > 0 || (ev.ProcessedToSubathon), false);
    }
    
    private static async Task CheckForGoalChange(AppDbContext db, int newPoints, int initialPoints)
    {
        if (newPoints != initialPoints)
        {
            // look for last completed goal according to initial, and then reg points. If diff, push either completed update, or list update if undo
            SubathonGoalSet? goalSet = await db.SubathonGoalSets.AsNoTracking()
                .Include(g=> g.Goals).FirstOrDefaultAsync(g => g.IsActive);
            if (goalSet != null)
            {
                SubathonGoal? prevCompletedGoal1 = goalSet.Goals
                    .OrderByDescending(g => g.Points)
                    .FirstOrDefault(g => g.Points <= initialPoints);
                
                SubathonGoal? prevCompletedGoal2 = goalSet.Goals
                    .OrderByDescending(g => g.Points)
                    .FirstOrDefault(g => g.Points <= newPoints);
                
                if (prevCompletedGoal2 != null &&
                    prevCompletedGoal1?.Id != prevCompletedGoal2.Id)
                {
                    if (prevCompletedGoal1 == null || prevCompletedGoal1.Points <= prevCompletedGoal2.Points)
                        SubathonEvents.RaiseSubathonGoalCompleted(prevCompletedGoal2, newPoints);
                    else
                        SubathonEvents.RaiseSubathonGoalListUpdated(goalSet.Goals, newPoints);
                }
                else if (prevCompletedGoal2 == null && prevCompletedGoal1 == null || newPoints < initialPoints)
                {
                    SubathonEvents.RaiseSubathonGoalListUpdated(goalSet.Goals, newPoints);
                }
            }
        }
        await Task.CompletedTask;
    }

    public async void DeleteSubathonEvent(AppDbContext db, SubathonEvent ev)
    {
        if (ev.SubathonId == null) return;
        
        SubathonData? subathon = await db.SubathonDatas.FirstOrDefaultAsync(s => s.IsActive);
        if (ev.SubathonId != subathon?.Id) return;
        
        int initialPoints = subathon?.Points ?? 0;
        
        long msToRemove = (long) Math.Ceiling(ev.GetFinalSecondsValueRaw() * 1000); 
        int pointsToRemove = (int) ev.GetFinalPointsValue();

        if (ev.Command == SubathonCommandType.SubtractPoints)
            pointsToRemove = -pointsToRemove;

        if (ev.Command == SubathonCommandType.SubtractTime)
            msToRemove = -msToRemove;

        if (ev.Command == SubathonCommandType.SetPoints || ev.Command == SubathonCommandType.SetTime)
        {
            string msg = "Cannot delete SetTime or SetPoints events. Retaining event...";
            ErrorMessageEvents.RaiseErrorEvent("WARN", $"Delete {ev.Command} Event", 
                msg, DateTime.Now);
            _logger?.LogWarning(msg);
            return;
        }
        
        int affected = 0;
        if (msToRemove != 0)
            affected += await db.Database.ExecuteSqlRawAsync(
                "UPDATE SubathonDatas SET MillisecondsCumulative = MillisecondsCumulative - {0} WHERE IsActive = 1 AND Id = {1} " 
                , msToRemove, ev.SubathonId);
        
        if (pointsToRemove != 0)
            affected += await db.Database.ExecuteSqlRawAsync(
                "UPDATE SubathonDatas SET Points = Points - {0} WHERE IsActive = 1 AND Id = {1} ", 
                pointsToRemove, ev.SubathonId);

        await db.Entry(ev).ReloadAsync();
        db.Remove(ev);
        await db.SaveChangesAsync();
        
        var events = new List<SubathonEvent>();
        events.Add(ev);
        SubathonEvents.RaiseSubathonEventsDeleted(events);
        
        if (affected > 0)
        {
            await db.Entry(subathon!).ReloadAsync();
            db.Entry(subathon!).State = EntityState.Detached;
            SubathonEvents.RaiseSubathonDataUpdate(subathon!, DateTime.Now);
            if (subathon!.Points != initialPoints)
            {
                SubathonGoalSet? goalSet = await db.SubathonGoalSets.Include(g=> g.Goals).FirstOrDefaultAsync(g => g.IsActive);
                if (goalSet != null)
                {
                    SubathonGoal? prevCompletedGoal1 = goalSet.Goals
                        .OrderByDescending(g => g.Points)
                        .FirstOrDefault(g => g.Points <= initialPoints);
                    
                    SubathonGoal? prevCompletedGoal2 = goalSet.Goals
                        .OrderByDescending(g => g.Points)
                        .FirstOrDefault(g => g.Points <= subathon.Points);

                    if (prevCompletedGoal1 != null && prevCompletedGoal2 != null &&
                        prevCompletedGoal1.Id != prevCompletedGoal2.Id)
                    {
                        // the else is for if we undid stuff
                        if (prevCompletedGoal1.Points <= prevCompletedGoal2.Points)
                            SubathonEvents.RaiseSubathonGoalCompleted(prevCompletedGoal2, subathon.Points);
                        else
                            SubathonEvents.RaiseSubathonGoalListUpdated(goalSet.Goals, subathon.Points);
                    }
                }
            }
        }
    }

    public async void UndoSimulatedEvents(AppDbContext db, List<SubathonEvent> events, bool doAll = false)
    {
        // Remove simulated events from the active subathon only
        // idea - recent events in main page can have "remove" option each that invoke this with a list of 1 
        
        int pointsToRemove = 0;
        long msToRemove = 0;

        SubathonData? subathon = await db.SubathonDatas.FirstOrDefaultAsync(s => s.IsActive);
        if (subathon == null) return;
        
        int initialPoints = subathon.Points; 
        
        if (doAll)
        {
            // Do All for current subathon
            events = await db.SubathonEvents.Where(e => e.SubathonId == subathon.Id 
                                                        && e.Source == SubathonEventSource.Simulated)
                .ToListAsync();
            
        }

        foreach (SubathonEvent ev in events)
        {
            if (ev.SubathonId != subathon.Id) continue;
            msToRemove += (long) Math.Ceiling(ev.GetFinalSecondsValueRaw() * 1000);
            pointsToRemove +=  (int) ev.GetFinalPointsValue();
        }
        
        int affected = 0;
        if (msToRemove != 0)
            affected += await db.Database.ExecuteSqlRawAsync(
                "UPDATE SubathonDatas SET MillisecondsCumulative = MillisecondsCumulative - {0} WHERE IsActive = 1 AND Id = {1} " 
                , msToRemove, subathon.Id);
        
        if (pointsToRemove != 0)
            affected += await db.Database.ExecuteSqlRawAsync(
                "UPDATE SubathonDatas SET Points = Points - {0} WHERE IsActive = 1 AND Id = {1} ", 
                pointsToRemove, subathon.Id);

        db.RemoveRange(events);
        await db.SaveChangesAsync();
        SubathonEvents.RaiseSubathonEventsDeleted(events);
        
        if (affected > 0)
        {
            await db.Entry(subathon).ReloadAsync();
            db.Entry(subathon).State = EntityState.Detached;
            SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
            if (subathon.Points != initialPoints)
            {
                SubathonGoalSet? goalSet = await db.SubathonGoalSets.Include(g=> g.Goals).FirstOrDefaultAsync(g => g.IsActive);
                if (goalSet != null)
                {
                    SubathonGoal? prevCompletedGoal1 = goalSet.Goals
                        .OrderByDescending(g => g.Points)
                        .FirstOrDefault(g => g.Points <= initialPoints);
                    
                    SubathonGoal? prevCompletedGoal2 = goalSet.Goals
                        .OrderByDescending(g => g.Points)
                        .FirstOrDefault(g => g.Points <= subathon.Points);

                    if (prevCompletedGoal1 != null && prevCompletedGoal2 != null &&
                        prevCompletedGoal1.Id != prevCompletedGoal2.Id)
                    {

                        // the else is for if we undid stuff
                        if (prevCompletedGoal1.Points <= prevCompletedGoal2.Points)
                            SubathonEvents.RaiseSubathonGoalCompleted(prevCompletedGoal2, subathon.Points);
                        else
                            SubathonEvents.RaiseSubathonGoalListUpdated(goalSet.Goals, subathon.Points);
                    }
                }
            }
        }
        db.Entry(subathon).State = EntityState.Detached;
    }
    
    public async Task StopAsync()
    {
        _cts.Cancel();
        _signal.Release();
        if (_processingTask != null)
            await _processingTask;
    }
    
    public void Dispose()
    {
        _cts.Cancel();
        _signal.Dispose();
        _cts.Dispose();
    }
}