using System.Globalization;
using System.Text.Json;
using Avalonia.Interactivity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.UI.UiUtils;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views;

public partial class SubathonSummaryWindow {
    private const int ExportChunk = 2000;
    private List<string> _lastFilterCells = [];
    private async void ExportEvents_Click(object? sender, RoutedEventArgs e) {
        if (_matched.Count == 0) {
            ResultStatus.Text = "Nothing to export, no events matched";
            return;
        }

        SubathonOption? subathon = Selected;
        ResultStatus.Text = $"Exporting {_matched.Count:N0} events...";

        List<string> metaRow = BuildMetaRow(subathon, _lastFilterCells
            .Append($"Matched Events: {_matched.Count.ToString("N0", CultureInfo.InvariantCulture)}"));
        var events = new List<SubathonEvent>(_matched.Count);

        try {
            await using AppDbContext db = await _factory.CreateDbContextAsync();
            // chunk
            for (var offset = 0; offset < _matched.Count; offset += ExportChunk) {
                List<EventKey> keys = _matched.GetRange(offset, Math.Min(ExportChunk, _matched.Count - offset));
                var wanted = keys.ToHashSet();
                List<Guid> ids = keys.Select(k => k.Id).Distinct().ToList();

                Dictionary<EventKey, SubathonEvent> byKey = (await db.SubathonEvents.AsNoTracking()
                        .Where(x => ids.Contains(x.Id))
                        .ToListAsync())
                    .Where(x => wanted.Contains(new EventKey(x.Id, x.Source)))
                    .GroupBy(x => new EventKey(x.Id, x.Source))
                    .ToDictionary(g => g.Key, g => g.First());

                events.AddRange(keys.Where(byKey.ContainsKey).Select(key => byKey[key]));
            }
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "[Summary] Event export failed");
            ResultStatus.Text = "Export failed, check the logs";
            return;
        }

        string? path = await WriteExportAsync("filtered_events", subathon?.Id,
            [
                metaRow, [],
                [
                    "Id", "Timestamp", "True Source", "Source", "Event Type", "Type Label", "Event Meta", "Command",
                    "User", "Value", "Display Value", "Currency", "Amount", "Seconds Value", "Points Value",
                    "Multiplier Seconds", "Multiplier Points", "Final Seconds Added", "Final Points Added",
                    "Processed", "Was Reversed", "Secondary Value", "Tertiary Value"
                ]
            ],
            events, EventToCsvRow);
        ResultStatus.Text = path == null
            ? "Could not write the export, check the logs"
            : $"Exported {_matched.Count:N0} events to {Path.GetFileName(path)}";
    }

    private static IEnumerable<object?> EventToCsvRow(SubathonEvent ev) {
        string trueSource = ev.EventType.GetTypeTrueSource(ev.EventTypeMeta) ?? ev.Source.ToString();
        var row = new SummaryEventRow(ev, trueSource);

        return [
            ev.Id, ev.EventTimestamp, trueSource, ev.Source, ev.EventType, ev.EventType.GetLabel(), ev.EventTypeMeta,
            ev.Command, ev.User, ev.Value, row.DisplayValue, ev.Currency, ev.Amount, ev.SecondsValue,
            ev.PointsValue, ev.MultiplierSeconds, ev.MultiplierPoints, ev.GetFinalSecondsValueRaw(),
            ev.GetFinalPointsValue(), ev.ProcessedToSubathon, ev.WasReversed, ev.SecondaryValue, ev.TertiaryValue
        ];
    }

    private async void ExportLeaderboard_Click(object? sender, RoutedEventArgs e) {
        if (_lbRows.Count == 0) {
            LbStatus.Text = "Nothing to export, run Visualize first.";
            return;
        }

        List<string> meta = ["Query URL: " + _lbUrl];
        try {
            using JsonDocument doc = JsonDocument.Parse(LbRawBox.Text ?? "{}");
            JsonElement root = doc.RootElement;

            AddJsonMeta(meta, root, "event_types", "Event Types");
            AddJsonMeta(meta, root, "combined", "Combined");
            AddJsonMeta(meta, root, "method", "Method");
            AddJsonMeta(meta, root, "unit", "Unit");
            AddJsonMeta(meta, root, "currency", "Currency");
            AddJsonMeta(meta, root, "meta", "Meta Filter");
            AddJsonMeta(meta, root, "blacklist", "Blacklist");
            AddJsonMeta(meta, root, "aliases", "Aliases");
            AddJsonMeta(meta, root, "top", "Top N");
            AddJsonMeta(meta, root, "user_count", "Total Users");
            AddJsonMeta(meta, root, "event_count", "Total Events");
            AddJsonMeta(meta, root, "total", "Total Value");
            AddJsonMeta(meta, root, "unconverted_currencies", "Unconverted Currencies");
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[Summary] Could not read leaderboard meta for export");
        }

        string? path = await WriteExportAsync("filtered_leaderboard", Selected?.Id,
            [BuildMetaRow(Selected, meta), [], ["Rank", "User", "Value", "Events", "Count"]],
            _lbRows, row => [row.Rank, row.User, row.Value, row.Events, row.Count]);
        LbStatus.Text = path == null
            ? "Could not write the export, check the logs"
            : $"Exported {_lbRows.Count:N0} rows to {Path.GetFileName(path)}";
    }

    private static void AddJsonMeta(List<string> into, JsonElement root, string property, string label) {
        if (!root.TryGetProperty(property, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return;

        string text = value.ValueKind switch {
            JsonValueKind.Array => string.Join(" | ", value.EnumerateArray().Select(v => v.ToString())),
            JsonValueKind.Object => string.Join(" | ", value.EnumerateObject()
                .Select(p => $"{p.Name}={string.Join('+', p.Value.EnumerateArray().Select(v => v.ToString()))}")),
            _ => value.ToString()
        };

        if (text.Length > 0) into.Add($"{label}: {text}");
    }

    private static List<string> BuildMetaRow(SubathonOption? subathon, IEnumerable<string> cells) {
        List<string> all = [
            $"Exported: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}",
            $"Subathon: {subathon?.Name ?? "unknown"}",
            $"Subathon Id: {subathon?.Id.ToString() ?? "unknown"}",
            $"Active: {(subathon?.IsActive == true ? "Yes" : "No")}"
        ];
        all.AddRange(cells);
        return all;
    }

    private async Task<string?> WriteExportAsync<T>(string prefix, Guid? subathonId,
        IEnumerable<IEnumerable<object?>> headerRows, IEnumerable<T> rows, Func<T, IEnumerable<object?>> toCells) {
        try {
            string path = await CsvUtils.ExportAsync(subathonId == null ? prefix : $"{prefix}-{subathonId}",
                headerRows, rows, toCells);
            UiHelpers.OpenFolder(Path.GetDirectoryName(path));
            return path;
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "[Summary] Failed to write export");
            return null;
        }
    }
}
