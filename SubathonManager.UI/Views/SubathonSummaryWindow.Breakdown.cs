using System.Globalization;
using Avalonia.Interactivity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Services;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views;

public partial class SubathonSummaryWindow {
    private const int BreakdownChunk = 2000;

    private static readonly HashSet<string> NonCurrencyMarkers = new(StringComparer.OrdinalIgnoreCase) {
        "bits", "sub", "beets", "kudos", "order", "items", "member", "viewers", "???", "jewels", "gems"
    };

    private async void Breakdown_Click(object? sender, RoutedEventArgs e) {
        BreakdownPane.IsVisible = true;
        await RebuildBreakdownAsync();
    }

    private void CloseBreakdown_Click(object? sender, RoutedEventArgs e) {
        BreakdownPane.IsVisible = false;
    }

    private async Task RebuildBreakdownAsync() {
        _breakdown.Clear();

        if (_matched.Count == 0) {
            _breakdown.Add(new BreakdownRow { Label = "No events matched the current filters", IsHeader = true });
            return;
        }

        _breakdown.Add(new BreakdownRow { Label = $"Aggregating {_matched.Count:N0} events...", IsHeader = true });

        var agg = new BreakdownAggregate();
        try {
            await using AppDbContext db = await _factory.CreateDbContextAsync();

            for (var offset = 0; offset < _matched.Count; offset += BreakdownChunk) {
                List<EventKey> keys = _matched.GetRange(offset, Math.Min(BreakdownChunk, _matched.Count - offset));
                HashSet<EventKey> wanted = keys.ToHashSet();
                List<Guid> ids = keys.Select(k => k.Id).Distinct().ToList();

                await foreach (SubathonEvent ev in db.SubathonEvents.AsNoTracking()
                                   .Where(e => ids.Contains(e.Id))
                                   .AsAsyncEnumerable()) {
                    if (!wanted.Contains(new EventKey(ev.Id, ev.Source))) continue;
                    Accumulate(agg, ev);
                }
            }
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "[Summary] Breakdown failed");
            _breakdown.Clear();
            _breakdown.Add(new BreakdownRow { Label = "Breakdown failed, check the logs", IsHeader = true });
            return;
        }

        Render(agg);
        await ConvertMoneyTotalAsync(agg);
    }

    private void Accumulate(BreakdownAggregate agg, SubathonEvent ev) {
        agg.Total++;

        string trueSource = ev.EventType.GetTypeTrueSource(ev.EventTypeMeta) ?? ev.Source.ToString();
        agg.Sources.TryGetValue(trueSource, out int sourceCount);
        agg.Sources[trueSource] = sourceCount + 1;

        SubathonEventType type = ev.EventType ?? SubathonEventType.Unknown;
        long amount = Math.Max(ev.Amount, 0);

        agg.Types.TryGetValue(type, out Tally typeTally);
        agg.Types[type] = typeTally.Add(amount);

        string meta = MetaLabel(ev);
        if (meta.Length > 0) {
            (SubathonEventType? type, string meta) key = (type, meta);
            agg.Metas.TryGetValue(key, out Tally metaTally);
            agg.Metas[key] = metaTally.Add(amount);
        }

        (double value, string currency)? money = ExtractMoney(ev);
        if (money != null) {
            agg.Currencies.TryGetValue(money.Value.currency, out double sum);
            agg.Currencies[money.Value.currency] = sum + money.Value.value;
        }

        bool reversed = ev is { WasReversed: true, Command: SubathonCommandType.None };
        agg.Seconds += reversed ? -ev.GetFinalSecondsValueRaw() : ev.GetFinalSecondsValueRaw();
        agg.Points += reversed ? -ev.GetFinalPointsValue() : ev.GetFinalPointsValue();
    }

    private void Render(BreakdownAggregate agg) {
        _breakdown.Clear();

        Add("Sources", $"{agg.Total:N0} events", 0, true);
        foreach ((string source, int count) in agg.Sources.OrderByDescending(s => s.Value))
            Add(source, count.ToString("N0"), 1);

        Add("Event Types", "", 0, true);
        foreach ((SubathonEventType? type, Tally tally) in agg.Types.OrderByDescending(t => t.Value.Count)) {
            Add($"{type.GetSource()} {type.GetLabel()}", $"{tally.Count:N0}  ({tally.Amount:N0} amt)", 1);

            List<KeyValuePair<(SubathonEventType?, string), Tally>> metas = agg.Metas
                .Where(m => Equals(m.Key.Item1, type))
                .OrderByDescending(m => m.Value.Count)
                .ToList();

            if (metas.Count <= 1) continue;
            foreach (((SubathonEventType?, string) key, Tally tally2) in metas)
                Add(key.Item2, $"{tally2.Count:N0}  ({tally2.Amount:N0} amt)", 2);
        }
        if (agg.Currencies.Count > 0) {
            Add("Currencies", "", 0, true);
            foreach ((string currency, double sum) in agg.Currencies.OrderByDescending(c => c.Value))
                Add(currency, sum.ToString("N2", CultureInfo.CurrentCulture), 1);
        }

        Add("Totals", "", 0, true);
        Add("Points added", agg.Points.ToString("N0", CultureInfo.CurrentCulture), 1);
        Add("Time added", FormatDuration(agg.Seconds), 1);
        Add("Money donated", "converting...", 1);
    }

    private async Task ConvertMoneyTotalAsync(BreakdownAggregate agg) {
        string target = _config.Get("Currency", "Primary", "USD")!.ToUpperInvariant().Trim();

        var total = 0.0;
        List<string> unconverted = [];
        var service = AppServices.Provider?.GetService<CurrencyService>();

        foreach ((string currency, double sum) in agg.Currencies) {
            if (string.Equals(currency, target, StringComparison.OrdinalIgnoreCase)) {
                total += sum;
                continue;
            }

            double rate = service == null ? 0 : await service.ConvertAsync(1.0, currency, target);
            if (rate <= 0) {
                unconverted.Add(currency);
                continue;
            }

            total += sum * rate;
        }

        BreakdownRow? row = _breakdown.FirstOrDefault(r => r.Label == "Money donated");
        if (row == null) return;

        string suffix = unconverted.Count == 0 ? "" : $"  (+{string.Join(", ", unconverted)} unconverted)";
        _breakdown[_breakdown.IndexOf(row)] = new BreakdownRow {
            Label = "Money donated",
            Value = $"{total.ToString("N2", CultureInfo.CurrentCulture)} {target}{suffix}",
            Level = 1
        };
    }

    private (double amount, string currency)? ExtractMoney(SubathonEvent ev) {
        if (Utils.IsCommissionAsDonation(_config, ev) &&
            TrySplitSecondaryValue(ev, out double commission, out string commissionCurrency))
            return (commission, commissionCurrency);

        if (IsCurrencyCode(ev.Currency) &&
            double.TryParse(ev.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double value))
            return (value, ev.Currency!.ToUpperInvariant().Trim());

        if (TrySplitSecondaryValue(ev, out double secondary, out string secondaryCurrency))
            return (secondary, secondaryCurrency);

        return null;
    }

    private static bool TrySplitSecondaryValue(SubathonEvent ev, out double amount, out string currency) {
        amount = 0;
        currency = string.Empty;
        if (string.IsNullOrWhiteSpace(ev.SecondaryValue) || !ev.SecondaryValue.Contains('|')) return false;

        string[] parts = ev.SecondaryValue.Split('|');
        if (parts.Length < 2 || !IsCurrencyCode(parts[1])) return false;
        if (!double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out amount)) return false;

        currency = parts[1].ToUpperInvariant().Trim();
        return true;
    }

    private static string MetaLabel(SubathonEvent ev) {
        if (ev.EventType is SubathonEventType.TwitchSub or SubathonEventType.TwitchGiftSub)
            return ev.Value switch {
                "1000" => "T1",
                "2000" => "T2",
                "3000" => "T3",
                _ => ev.Value
            };

        if (ev.EventType == SubathonEventType.GoAffProOrder &&
            GoAffProOrderHelper.TryGetStore(ev.EventTypeMeta, out GoAffProStore? store))
            return store.InternalName;

        return string.IsNullOrWhiteSpace(ev.EventTypeMeta) ? string.Empty : ev.EventTypeMeta!;
    }
    
    private static bool IsCurrencyCode(string? currency) {
        if (string.IsNullOrWhiteSpace(currency)) return false;
        string trimmed = currency.Trim();
        return trimmed.Length == 3 && trimmed.All(char.IsLetter) && !NonCurrencyMarkers.Contains(trimmed);
    }
    
    private static string FormatDuration(double seconds) {
        TimeSpan span = TimeSpan.FromSeconds(Math.Abs(seconds));
        string sign = seconds < 0 ? "-" : "";
        return span.TotalDays >= 1
            ? $"{sign}{(int)span.TotalDays}d {span.Hours:00}h {span.Minutes:00}m {span.Seconds:00}s"
            : $"{sign}{(int)span.TotalHours:00}h {span.Minutes:00}m {span.Seconds:00}s";
    }

    private void Add(string label, string value, int level, bool header = false) {
        _breakdown.Add(new BreakdownRow { Label = label, Value = value, Level = level, IsHeader = header });
    }

    private readonly record struct Tally(long Count, long Amount) {
        public Tally Add(long amount) {
            return new Tally(Count + 1, Amount + amount);
        }
    }

    private sealed class BreakdownAggregate {
        public readonly Dictionary<string, double> Currencies = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<(SubathonEventType?, string), Tally> Metas = new();
        public readonly Dictionary<string, int> Sources = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<SubathonEventType, Tally> Types = new();
        public double Points;
        public double Seconds;
        public int Total;
    }
}