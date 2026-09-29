using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SubathonManager.Core.Enums;

namespace SubathonManager.Core.Models;

public class ScheduleItem {
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Date { get; set; } = DateTime.Today;

    public ScheduleItemKind Kind { get; set; } = ScheduleItemKind.Event;
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int? StartMinute { get; set; }
    public int? EndMinute { get; set; }

    public bool IsDone { get; set; }
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [NotMapped] public bool IsAllDay => StartMinute == null;

    public static string FormatMinute(int minute) {
        minute = (minute % 1440 + 1440) % 1440;
        return $"{minute / 60:00}:{minute % 60:00}";
    }

    public static bool TryParseTime(string? text, out int? minute) {
        minute = null;
        string raw = (text ?? "").Trim().Replace('.', ':');
        if (raw.Length == 0) return true;

        int hour, min;
        if (raw.Contains(':')) {
            string[] parts = raw.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out hour)) return false;
            if (parts[1].Length == 0) min = 0;
            else if (!int.TryParse(parts[1], out min)) return false;
        }
        else {
            if (!int.TryParse(raw, out int digits)) return false;
            if (raw.Length <= 2) {
                hour = digits;
                min = 0;
            }
            else if (raw.Length <= 4) {
                hour = digits / 100;
                min = digits % 100;
            }
            else {
                return false;
            }
        }

        if (hour is < 0 or > 23 || min is < 0 or > 59) return false;
        minute = hour * 60 + min;
        return true;
    }

    public string TimeLabel() {
        if (StartMinute == null) return "All day";
        string start = FormatMinute(StartMinute.Value);
        if (EndMinute == null) return start;
        string end = FormatMinute(EndMinute.Value);
        return EndMinute < StartMinute ? $"{start} - {end} (+1)" : $"{start} - {end}";
    }
}