using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;

namespace SubathonManager.Data;

public static class ActionVariableResolver {

    public static async Task<string> FillAsync(IDbContextFactory<AppDbContext> factory, string? template,
        ActionContext? ctx = null, IConfig? config = null) {
        List<ActionVariable> used = ActionStepTypeHelper.FindVariables(template).Distinct().ToList();
        if (used.Count == 0) return template ?? "";
        Dictionary<ActionVariable, string> values = await ResolveAsync(factory, used, ctx, config);
        return ActionStepTypeHelper.ReplaceVariables(template, values);
    }

    public static async Task<Dictionary<ActionVariable, string>> ResolveAsync(IDbContextFactory<AppDbContext> factory,
        IEnumerable<ActionVariable> variables, ActionContext? ctx = null, IConfig? config = null) {
        List<ActionVariable> wanted = variables.Distinct().ToList();
        var values = new Dictionary<ActionVariable, string>();
        if (wanted.Count == 0) return values;

        SubathonData? subathon = null;
        SubathonGoalSet? goals = null;
        if (wanted.Any(v => v.GetGroup() != "Action")) {
            await using AppDbContext db = await factory.CreateDbContextAsync();
            subathon = await db.SubathonDatas.Include(s => s.Multiplier).AsNoTracking()
                .FirstOrDefaultAsync(s => s.IsActive);
            if (wanted.Any(v => v.GetGroup() == "Goals"))
                goals = await db.SubathonGoalSets.Include(g => g.Goals).AsNoTracking()
                    .FirstOrDefaultAsync(g => g.IsActive);
        }

        // could get from subathondata, but sending both for verbosity
        string defaultCurrency = (config?.Get("Currency", "Primary", "USD") ?? "USD").Trim();
        GoalState goal = GoalState.From(goals, subathon);

        foreach (ActionVariable variable in wanted)
            values[variable] = variable switch {
                ActionVariable.User => ctx?.User ?? "",
                ActionVariable.Source => ctx != null ? $"{ctx.Source}" : "",
                ActionVariable.Label => ctx?.Label ?? "",
                ActionVariable.Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ActionVariable.DefaultCurrency => defaultCurrency,
                ActionVariable.GoalText => goal.Current?.Text ?? "",
                ActionVariable.GoalPoints => Number(goal.Current?.Points ?? 0),
                ActionVariable.GoalIndex => Number(goal.CurrentIndex),
                ActionVariable.GoalCount => Number(goal.Count),
                ActionVariable.GoalProgress => Number(goal.Progress),
                ActionVariable.LastGoalText => goal.Last?.Text ?? "",
                ActionVariable.LastGoalPoints => Number(goal.Last?.Points ?? 0),
                ActionVariable.GoalListName => goals?.Name ?? "",
                ActionVariable.GoalType => $"{goals?.Type ?? GoalsType.Points}",
                _ => subathon == null ? "" : FromSubathon(variable, subathon, defaultCurrency)
            };

        return values;
    }

    private static string FromSubathon(ActionVariable variable, SubathonData subathon, string defaultCurrency) {
        MultiplierData multiplier = subathon.Multiplier;
        bool running = multiplier.IsRunning();
        TimeSpan remaining = subathon.TimeRemainingRounded();

        return variable switch {
            ActionVariable.SecondsRemaining => Number((long)remaining.TotalSeconds),
            ActionVariable.TimeRemaining =>
                $"{(long)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}",
            ActionVariable.SecondsElapsed => Number(subathon.MillisecondsElapsed / 1000),
            ActionVariable.TimerPaused => Bool(subathon.IsPaused),
            ActionVariable.TimerLocked => Bool(subathon.IsLocked),
            ActionVariable.TimerReversed => Bool(subathon.IsSubathonReversed()),
            ActionVariable.MultiplierActive => Bool(running),
            ActionVariable.MultiplierAmount => running
                ? multiplier.Multiplier.ToString(CultureInfo.InvariantCulture) : "1",
            ActionVariable.MultiplierSecondsRemaining => Number(running && multiplier is
                { Duration: { } duration, Started: { } started }
                ? Math.Max(0, (long)(started + duration - DateTime.Now).TotalSeconds) : 0),
            ActionVariable.MultiplierPoints => Bool(running && multiplier.ApplyToPoints),
            ActionVariable.MultiplierTime => Bool(running && multiplier.ApplyToSeconds),
            ActionVariable.Points => Number(subathon.Points),
            ActionVariable.Money => subathon.GetRoundedMoneySumWithCents().ToString("0.00", CultureInfo.InvariantCulture),
            ActionVariable.Currency => string.IsNullOrWhiteSpace(subathon.Currency) ? defaultCurrency : subathon.Currency,
            _ => ""
        };
    }

    private static string Number(long value) {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string Bool(bool value) {
        return value ? "true" : "false";
    }

    private readonly record struct GoalState(SubathonGoal? Current, SubathonGoal? Last, int CurrentIndex, 
        int Count, long Progress) {

        public static GoalState From(SubathonGoalSet? set, SubathonData? subathon) {
            if (set == null || subathon == null) return default;

            List<SubathonGoal> ordered = set.Goals.OrderBy(g => g.Points).ToList();
            long progress = set.Type == GoalsType.Money ? subathon.GetRoundedMoneySum() : subathon.Points;
            int next = ordered.FindIndex(g => g.Points > progress);

            SubathonGoal? last = ordered.LastOrDefault(g => g.Points <= progress);
            return new GoalState(next >= 0 ? ordered[next] : null, last, next >= 0 ? next + 1 : 0, ordered.Count,
                progress);
        }
    }
}
