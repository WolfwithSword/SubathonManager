using System.Globalization;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;

namespace SubathonManager.Core;

public static class ScheduleCsv {
    private const string DateFormat = "yyyy-MM-dd";

    public static readonly string[] Columns = [
        nameof(ScheduleItem.Date),
        nameof(ScheduleItem.Kind),
        nameof(ScheduleItem.StartMinute),
        nameof(ScheduleItem.EndMinute),
        nameof(ScheduleItem.Title),
        nameof(ScheduleItem.Description),
        nameof(ScheduleItem.IsDone)
    ];

    public static string Write(IEnumerable<ScheduleItem> items) {
        return CsvUtils.Write([Columns], items, item => [
            item.Date.ToString(DateFormat, CultureInfo.InvariantCulture),
            item.Kind,
            item.StartMinute is { } s ? ScheduleItem.FormatMinute(s) : "",
            item.EndMinute is { } e ? ScheduleItem.FormatMinute(e) : "",
            item.Title,
            item.Description,
            item.IsDone ? "true" : "false"
        ]);
    }

    public static ParseResult Parse(string csv) {
        var items = new List<ScheduleItem>();
        var errors = new List<string>();

        List<(int Line, string[] Fields)> records = CsvUtils.ReadRecords(csv);
        if (records.Count == 0) {
            errors.Add("File is empty.");
            return new ParseResult(items, errors);
        }

        Dictionary<string, int> index = records[0].Fields
            .Select((name, i) => (name: name.Trim(), i))
            .GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);

        foreach (string required in new[] { nameof(ScheduleItem.Date), nameof(ScheduleItem.Title) })
            if (!index.ContainsKey(required))
                errors.Add($"Missing required column \"{required}\".");
        if (errors.Count > 0) return new ParseResult(items, errors);

        string Field(string[] fields, string column) {
            return index.TryGetValue(column, out int i) && i < fields.Length ? fields[i] : "";
        }

        foreach ((int line, string[] fields) in records.Skip(1)) {
            if (fields.All(string.IsNullOrWhiteSpace)) continue;

            string dateRaw = Field(fields, nameof(ScheduleItem.Date)).Trim();
            if (!DateTime.TryParseExact(dateRaw, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None,
                    out DateTime date) &&
                !DateTime.TryParse(dateRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) {
                errors.Add($"Line {line}: invalid date \"{dateRaw}\".");
                continue;
            }

            string title = Field(fields, nameof(ScheduleItem.Title)).Trim();
            if (title.Length == 0) {
                errors.Add($"Line {line}: missing title.");
                continue;
            }

            string kindRaw = Field(fields, nameof(ScheduleItem.Kind)).Trim();
            var kind = ScheduleItemKind.Event;
            if (kindRaw.Length > 0 && (!Enum.TryParse(kindRaw, true, out kind) || !Enum.IsDefined(kind))) {
                errors.Add($"Line {line}: unknown kind \"{kindRaw}\".");
                continue;
            }

            string startRaw = Field(fields, nameof(ScheduleItem.StartMinute));
            if (!ScheduleItem.TryParseTime(startRaw, out int? start)) {
                errors.Add($"Line {line}: invalid start time \"{startRaw.Trim()}\".");
                continue;
            }

            string endRaw = Field(fields, nameof(ScheduleItem.EndMinute));
            if (!ScheduleItem.TryParseTime(endRaw, out int? end)) {
                errors.Add($"Line {line}: invalid end time \"{endRaw.Trim()}\".");
                continue;
            }

            if (start == null || end == start) end = null;

            string doneRaw = Field(fields, nameof(ScheduleItem.IsDone)).Trim();
            bool done = doneRaw.Equals("true", StringComparison.OrdinalIgnoreCase) || doneRaw is "1" ||
                        doneRaw.Equals("yes", StringComparison.OrdinalIgnoreCase);

            items.Add(new ScheduleItem {
                Date = date.Date,
                Kind = kind,
                StartMinute = start,
                EndMinute = end,
                Title = title,
                Description = Field(fields, nameof(ScheduleItem.Description)).TrimEnd(),
                IsDone = done
            });
        }

        return new ParseResult(items, errors);
    }

    public static ImportPlan PlanImport(IEnumerable<ScheduleItem> existing, IEnumerable<ScheduleItem> incoming) {
        List<ScheduleItem> known = existing.ToList();
        var toAdd = new List<ScheduleItem>();
        var toUpdate = new List<ScheduleItem>();
        var unchanged = 0;

        Dictionary<DateTime, int> nextOrder = known
            .GroupBy(i => i.Date.Date)
            .ToDictionary(g => g.Key, g => g.Max(i => i.SortOrder) + 1);

        foreach (ScheduleItem item in incoming) {
            ScheduleItem? match = known.FirstOrDefault(k => IsSameSlot(k, item));
            if (match != null) {
                if (match.Description == item.Description) {
                    unchanged++;
                    continue;
                }

                match.Description = item.Description;
                if (!toAdd.Contains(match) && !toUpdate.Contains(match)) toUpdate.Add(match);
                continue;
            }

            DateTime day = item.Date.Date;
            int order = nextOrder.GetValueOrDefault(day, 0);
            nextOrder[day] = order + 1;
            item.SortOrder = order;
            toAdd.Add(item);
            known.Add(item);
        }

        return new ImportPlan(toAdd, toUpdate, unchanged);
    }

    private static bool IsSameSlot(ScheduleItem a, ScheduleItem b) {
        return a.Date.Date == b.Date.Date && a.Title == b.Title &&
               a.StartMinute == b.StartMinute && a.EndMinute == b.EndMinute;
    }

    public sealed record ParseResult(List<ScheduleItem> Items, List<string> Errors);

    public sealed record ImportPlan(List<ScheduleItem> ToAdd, List<ScheduleItem> ToUpdate, int Unchanged);
}