using System.Globalization;
using System.Text;

namespace SubathonManager.Core;

public static class CsvUtils {
    public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

    public static string ExportFolder => Path.Combine(Config.DataFolder, "exports");

    public static string Escape(object? value) {
        string? text = value switch {
            null => null,
            string s => s,
            DateTime dt => dt.ToString(TimestampFormat, CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString(TimestampFormat, CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };

        if (string.IsNullOrWhiteSpace(text)) return "";
        if (text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r'))
            return $"\"{text.Replace("\"", "\"\"")}\"";
        return text;
    }

    public static string FormatRow(IEnumerable<object?> cells) {
        return string.Join(',', cells.Select(Escape));
    }

    public static string Write<T>(IEnumerable<IEnumerable<object?>> headerRows, IEnumerable<T> rows,
        Func<T, IEnumerable<object?>> toCells) {
        var sb = new StringBuilder();
        foreach (IEnumerable<object?> header in headerRows) sb.AppendLine(FormatRow(header));
        foreach (T row in rows) sb.AppendLine(FormatRow(toCells(row)));
        return sb.ToString();
    }

    public static async Task<string> ExportAsync<T>(string baseName, IEnumerable<IEnumerable<object?>> headerRows,
        IEnumerable<T> rows, Func<T, IEnumerable<object?>> toCells) {
        Directory.CreateDirectory(ExportFolder);

        string name = SafeFileName.Sanitize(baseName, fallback: "export");
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string path = Path.Combine(ExportFolder, $"{name}-{stamp}.csv");
        await File.WriteAllTextAsync(path, Write(headerRows, rows, toCells), Encoding.UTF8);
        return path;
    }

    public static async Task<List<string[]>?> ReadFileAsync(string path) {
        try {
            string text = await File.ReadAllTextAsync(path, Encoding.UTF8);
            return ReadRecords(text)
                .Select(r => r.Fields)
                .Where(f => !f.All(string.IsNullOrWhiteSpace))
                .ToList();
        }
        catch {
            return null;
        }
    }

    public static List<(int Line, string[] Fields)> ReadRecords(string csv) {
        var records = new List<(int, string[])>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var recordStart = 1;
        var any = false;

        if (csv.Length > 0 && csv[0] == (char)0xFEFF) csv = csv[1..];

        for (var i = 0; i < csv.Length; i++) {
            char c = csv[i];
            if (inQuotes) {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"') {
                    field.Append('"');
                    i++;
                }
                else if (c == '"') {
                    inQuotes = false;
                }
                else {
                    if (c == '\n') line++;
                    if (c != '\r') field.Append(c);
                }

                continue;
            }

            switch (c) {
                case '"':
                    inQuotes = true;
                    any = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    any = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    if (any || fields.Count > 1 || fields[0].Length > 0) records.Add((recordStart, fields.ToArray()));
                    fields.Clear();
                    any = false;
                    line++;
                    recordStart = line;
                    break;
                default:
                    field.Append(c);
                    any = true;
                    break;
            }
        }

        if (any || field.Length > 0) {
            fields.Add(field.ToString());
            records.Add((recordStart, fields.ToArray()));
        }

        return records;
    }
}
