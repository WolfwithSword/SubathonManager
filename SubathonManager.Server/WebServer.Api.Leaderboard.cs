using System.Collections.Specialized;
using System.Globalization;
using System.Text.Json;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Server.Interfaces;
using SubathonManager.Services;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Server;

internal enum LeaderboardMethod {
    ByPoints,
    ByCount,
    ByValue,
    ByAmount,
    ByOrder,
    ByItems
}

internal enum LeaderboardCategory {
    Subscription,
    Token,
    Donation,
    Order,
    Other
}

public partial class WebServer {
    private const int DefaultLeaderboardSize = 10;
    private const int MaxLeaderboardSize = 10000;

    private static readonly HashSet<string> NonCurrencyMarkers = new(StringComparer.OrdinalIgnoreCase) {
        "bits", "sub", "beets", "kudos", "order", "items", "member", "viewers", "???", "jewels", "gems"
    };

    internal async Task HandleLeaderboardRequestAsync(IHttpContext ctx) {
        NameValueCollection query = HttpUtility.ParseQueryString(ctx.QueryString);

        (List<SubathonEventType>? types, string? typeError) =
            ParseLeaderboardTypes(ParseCsvParam(query, "type", "types", "event", "eventtype", "eventtypes"));
        if (types == null) {
            await ReturnError(ctx, typeError!);
            return;
        }

        // more than one event type - normalize users, sum final points only
        bool combined = types.Count > 1;
        SubathonEventType? primary = combined ? null : types[0];
        LeaderboardCategory category = combined ? LeaderboardCategory.Other : GetLeaderboardCategory(primary);

        (LeaderboardMethod? resolved, string? methodError) = combined
            ? ResolveCombinedMethod(FirstParam(query, "method", "mode"))
            : ResolveLeaderboardMethod(FirstParam(query, "method", "mode"), category);
        if (resolved == null) {
            await ReturnError(ctx, methodError!);
            return;
        }

        LeaderboardMethod method = resolved.Value;

        int top = DefaultLeaderboardSize;
        string? topParam = FirstParam(query, "top", "n", "limit");
        if (!string.IsNullOrWhiteSpace(topParam)) {
            if (topParam.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) {
                top = 0;
            }
            else if (!int.TryParse(topParam.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out top)) {
                await ReturnError(ctx, "Invalid 'top' parameter, expected a whole number or 'all'");
                return;
            }

            top = Math.Clamp(top, 0, MaxLeaderboardSize);
        }

        HashSet<string> requestedMetas = ParseCsvParam(query, "meta", "metas", "tier", "tiers")
            .Select(NormalizeLeaderboardMeta)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        
        // ignore meta filters when multiple event types, complex
        HashSet<string> metas = combined ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : requestedMetas;
        HashSet<string> blacklist = ParseCsvParam(query, "blacklist", "exclude", "ignore")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool includeUnprocessed = Utils.IsTruthy(FirstParam(query, "includeunprocessed", "unprocessed"));

        string targetCurrency = (FirstParam(query, "currency", "target") ??
                                 _config.Get("Currency", "Primary", "USD")!).ToUpperInvariant().Trim();
        if (!IsCurrencyCode(targetCurrency)) {
            await ReturnError(ctx, $"Invalid 'currency' parameter: {targetCurrency}");
            return;
        }

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        SubathonData? subathon = await db.SubathonDatas.AsNoTracking().FirstOrDefaultAsync(s => s.IsActive);
        if (subathon == null) {
            await ReturnError(ctx, "No active subathon");
            return;
        }

        List<SubathonEventType?> queryTypes = types.Select(t => (SubathonEventType?)t).ToList();
        List<SubathonEvent> events = await db.SubathonEvents.AsNoTracking()
            .Where(e => e.SubathonId == subathon.Id && queryTypes.Contains(e.EventType))
            .Where(e => includeUnprocessed || e.ProcessedToSubathon)
            .ToListAsync();

        Dictionary<string, LeaderboardEntry> entries = BuildLeaderboardEntries(events, metas, blacklist);

        var unconverted = new List<string>();
        bool isMoney = IsMoneyMethod(method, category);
        Dictionary<string, double> rates = isMoney
            ? await BuildRateMapAsync(entries.Values.SelectMany(e => e.Money.Keys), targetCurrency, unconverted)
            : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        List<(LeaderboardEntry entry, double value)> ranked = entries.Values
            .Select(e => (entry: e, value: GetLeaderboardValue(e, method, isMoney, rates)))
            .OrderByDescending(r => r.value)
            .ThenBy(r => r.entry.User, StringComparer.OrdinalIgnoreCase)
            .ToList();

        IEnumerable<(LeaderboardEntry entry, double value)> page = top > 0 ? ranked.Take(top) : ranked;

        object response = new {
            event_type = combined ? null : types[0].ToString(),
            event_types = types.Select(t => t.ToString()).ToArray(),
            source = combined ? null : primary.GetSource().ToString(),
            sources = types.Select(t => ((SubathonEventType?)t).GetSource().ToString()).Distinct().ToArray(),
            label = combined ? null : primary.GetLabel(),
            combined,
            method = method.ToString(),
            unit = MethodUnit(method, isMoney, targetCurrency),
            currency = isMoney ? targetCurrency : null,
            meta = metas.Count == 0 ? null : metas.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToArray(),
            meta_ignored = combined && requestedMetas.Count > 0,
            blacklist = blacklist.OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToArray(),
            include_unprocessed = includeUnprocessed,
            top,
            user_count = ranked.Count,
            event_count = ranked.Sum(r => r.entry.Events),
            total = Math.Round(ranked.Sum(r => r.value), 2),
            unconverted_currencies = unconverted.Count == 0 ? null : unconverted.ToArray(),
            results = page.Select((r, i) => new {
                rank = i + 1,
                user = r.entry.User,
                value = Math.Round(r.value, 2),
                events = r.entry.Events,
                count = r.entry.Items
            }).ToArray()
        };

        string json = JsonSerializer.Serialize(response, new JsonSerializerOptions {
            WriteIndented = true
        });
        await ctx.WriteResponse(200, json, true, "application/json");
    }

    private Dictionary<string, LeaderboardEntry> BuildLeaderboardEntries(List<SubathonEvent> events,
        HashSet<string> metas, HashSet<string> blacklist) {
        Dictionary<string, LeaderboardEntry> entries = new(StringComparer.OrdinalIgnoreCase);

        foreach (SubathonEvent ev in events) {
            string user = NormalizeUser(ev.User);
            if (user.Length == 0) continue;
            if (IsBlacklistedUser(user, blacklist)) continue;
            if (!MatchesLeaderboardMeta(ev, metas)) continue;

            if (!entries.TryGetValue(user, out LeaderboardEntry? entry)) {
                entry = new LeaderboardEntry { User = user };
                entries[user] = entry;
            }

            int multiplier = ev.EventType.IsOrder() ? 1 : Math.Max(ev.Amount, 0);
            entry.Events++;
            entry.Items += Math.Max(ev.Amount, 0);
            entry.Points += ev.GetFinalPointsValue();

            if (double.TryParse(ev.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double tokenValue))
                entry.Tokens += tokenValue * multiplier;

            (double amount, string currency)? money = ExtractMoney(ev);
            if (money == null) continue;
            entry.Money.TryGetValue(money.Value.currency, out double existing);
            entry.Money[money.Value.currency] = existing + money.Value.amount;
        }

        return entries;
    }

    private static (List<SubathonEventType>?, string?) ParseLeaderboardTypes(List<string> raw) {
        if (raw.Count == 0) return (null, "Missing or invalid 'type' parameter");

        var types = new List<SubathonEventType>();
        foreach (string entry in raw) {
            if (entry.All(char.IsDigit) || !Enum.TryParse(entry, true, out SubathonEventType parsed)
                                        || parsed is SubathonEventType.Unknown or SubathonEventType.Command)
                return (null, $"Missing or invalid 'type' parameter: {entry}");
            if (!types.Contains(parsed)) types.Add(parsed);
        }

        return (types, null);
    }

    private static (LeaderboardMethod?, string?) ResolveCombinedMethod(string? raw) {
        if (string.IsNullOrWhiteSpace(raw)) return (LeaderboardMethod.ByPoints, null);

        string trimmed = raw.Trim();
        if (trimmed.All(char.IsDigit) || !Enum.TryParse(trimmed, true, out LeaderboardMethod method))
            return (null, $"Unknown 'method' parameter: {trimmed}");

        if (method is LeaderboardMethod.ByPoints) return (LeaderboardMethod.ByPoints, null);
        return (null,
            $"Method '{method}' is not valid with more than one 'type', only {LeaderboardMethod.ByPoints} is");
    }

    private static string NormalizeUser(string? user) {
        return (user ?? string.Empty).Trim().TrimStart('@').Trim();
    }

    private static LeaderboardCategory GetLeaderboardCategory(SubathonEventType? type) {
        if (type.IsOrder()) return LeaderboardCategory.Order;
        if (type.IsSubscription() || type.IsGift()) return LeaderboardCategory.Subscription;
        if (type.IsToken()) return LeaderboardCategory.Token;
        if (type.IsCurrencyDonation()) return LeaderboardCategory.Donation;
        return LeaderboardCategory.Other;
    }

    private static (LeaderboardMethod?, string?) ResolveLeaderboardMethod(string? raw, LeaderboardCategory category) {
        LeaderboardMethod fallback = category switch {
            LeaderboardCategory.Subscription => LeaderboardMethod.ByPoints,
            LeaderboardCategory.Token => LeaderboardMethod.ByValue,
            LeaderboardCategory.Donation => LeaderboardMethod.ByAmount,
            LeaderboardCategory.Order => LeaderboardMethod.ByValue,
            _ => LeaderboardMethod.ByCount
        };

        if (string.IsNullOrWhiteSpace(raw)) return (fallback, null);

        string trimmed = raw.Trim();
        if (trimmed.All(char.IsDigit) || !Enum.TryParse(trimmed, true, out LeaderboardMethod method))
            return (null, $"Unknown 'method' parameter: {trimmed}");

        LeaderboardMethod? canonical = category switch {
            LeaderboardCategory.Subscription => method switch {
                LeaderboardMethod.ByPoints => LeaderboardMethod.ByPoints,
                LeaderboardMethod.ByCount => LeaderboardMethod.ByCount,
                _ => null
            },
            LeaderboardCategory.Token => method switch {
                LeaderboardMethod.ByValue or LeaderboardMethod.ByAmount => LeaderboardMethod.ByValue,
                LeaderboardMethod.ByPoints => LeaderboardMethod.ByPoints,
                LeaderboardMethod.ByCount => LeaderboardMethod.ByCount,
                _ => null
            },
            LeaderboardCategory.Donation => method switch {
                LeaderboardMethod.ByAmount or LeaderboardMethod.ByValue => LeaderboardMethod.ByAmount,
                LeaderboardMethod.ByPoints => LeaderboardMethod.ByPoints,
                LeaderboardMethod.ByCount => LeaderboardMethod.ByCount,
                _ => null
            },
            LeaderboardCategory.Order => method switch {
                LeaderboardMethod.ByValue or LeaderboardMethod.ByAmount => LeaderboardMethod.ByValue,
                LeaderboardMethod.ByOrder or LeaderboardMethod.ByCount => LeaderboardMethod.ByOrder,
                LeaderboardMethod.ByItems => LeaderboardMethod.ByItems,
                LeaderboardMethod.ByPoints => LeaderboardMethod.ByPoints,
                _ => null
            },
            _ => method switch {
                LeaderboardMethod.ByCount => LeaderboardMethod.ByCount,
                LeaderboardMethod.ByPoints => LeaderboardMethod.ByPoints,
                _ => null
            }
        };

        return canonical == null
            ? (null, $"Method '{method}' is not valid for {category.ToString().ToLowerInvariant()} events")
            : (canonical, null);
    }

    private static double GetLeaderboardValue(LeaderboardEntry entry, LeaderboardMethod method, bool isMoney,
        IReadOnlyDictionary<string, double> rates) {
        if (isMoney) {
            var sum = 0.0;
            foreach ((string currency, double amount) in entry.Money)
                sum += amount * (rates.TryGetValue(currency, out double rate) ? rate : 0);
            return sum;
        }

        return method switch {
            LeaderboardMethod.ByPoints => entry.Points,
            LeaderboardMethod.ByValue => entry.Tokens,
            LeaderboardMethod.ByOrder => entry.Events,
            _ => entry.Items
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

    private static async Task<Dictionary<string, double>> BuildRateMapAsync(IEnumerable<string> currencies,
        string target, List<string> unconverted) {
        var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var service = AppServices.Provider?.GetService<CurrencyService>();

        foreach (string currency in currencies.Distinct(StringComparer.OrdinalIgnoreCase)) {
            if (string.Equals(currency, target, StringComparison.OrdinalIgnoreCase)) {
                rates[currency] = 1.0;
                continue;
            }

            double rate = service == null ? 0 : await service.ConvertAsync(1.0, currency, target);
            rates[currency] = rate;
            if (rate <= 0) unconverted.Add(currency.ToUpperInvariant());
        }

        return rates;
    }

    private static bool IsMoneyMethod(LeaderboardMethod method, LeaderboardCategory category) {
        return method is LeaderboardMethod.ByAmount ||
               (method is LeaderboardMethod.ByValue && category is LeaderboardCategory.Order);
    }

    private static string MethodUnit(LeaderboardMethod method, bool isMoney, string currency) {
        if (isMoney) return currency;
        return method switch {
            LeaderboardMethod.ByPoints => "points",
            LeaderboardMethod.ByItems => "items",
            LeaderboardMethod.ByOrder => "orders",
            LeaderboardMethod.ByValue => "tokens",
            _ => "count"
        };
    }

    private static string NormalizeLeaderboardMeta(string meta) {
        string trimmed = meta.Trim();
        return trimmed.ToLowerInvariant() switch {
            "t1" => "1000",
            "t2" => "2000",
            "t3" => "3000",
            _ => trimmed
        };
    }

    private static bool MatchesLeaderboardMeta(SubathonEvent ev, HashSet<string> metas) {
        if (metas.Count == 0) return true;
        if (!string.IsNullOrWhiteSpace(ev.EventTypeMeta) &&
            metas.Contains(NormalizeLeaderboardMeta(ev.EventTypeMeta))) return true;
        return !string.IsNullOrWhiteSpace(ev.Value) && metas.Contains(NormalizeLeaderboardMeta(ev.Value));
    }

    private static bool IsBlacklistedUser(string user, HashSet<string> blacklist) {
        if (blacklist.Count == 0) return false;
        string trimmed = user.Trim();
        if (blacklist.Contains(trimmed)) return true;
        return blacklist.Where(entry => entry.EndsWith('*'))
            .Any(entry => trimmed.StartsWith(entry[..^1], StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCurrencyCode(string? currency) {
        if (string.IsNullOrWhiteSpace(currency)) return false;
        string trimmed = currency.Trim();
        return trimmed.Length == 3 && trimmed.All(char.IsLetter) && !NonCurrencyMarkers.Contains(trimmed);
    }

    private static string? FirstParam(NameValueCollection query, params string[] keys) {
        foreach (string key in keys) {
            string? value = query.AllKeys
                .Where(k => k != null && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                .Select(k => query[k])
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (value != null) return value;
        }

        return null;
    }

    private static List<string> ParseCsvParam(NameValueCollection query, params string[] keys) {
        var values = new List<string>();
        foreach (string key in keys)
        foreach (string? actual in query.AllKeys.Where(k =>
                     k != null && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))) {
            string[]? raw = query.GetValues(actual);
            if (raw == null) continue;
            values.AddRange(raw.SelectMany(v =>
                v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        }

        return values;
    }

    private static async Task ReturnError(IHttpContext ctx, string message, int code = 400) {
        string json = JsonSerializer.Serialize(new { error = message });
        await ctx.WriteResponse(code, json, true, "application/json");
    }

    private sealed class LeaderboardEntry {
        public readonly Dictionary<string, double> Money = new(StringComparer.OrdinalIgnoreCase);
        public int Events;
        public long Items;
        public double Points;
        public double Tokens;
        public string User = "";
    }
}
