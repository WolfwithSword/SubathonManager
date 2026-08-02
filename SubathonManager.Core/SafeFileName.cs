using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace SubathonManager.Core;

public static class SafeFileName
{
    public const string DefaultReplacement = "_";
    public const int MaxLength = 255;

    private static readonly HashSet<char> InvalidChars = BuildInvalidChars();

    private static HashSet<char> BuildInvalidChars()
    {
        var chars = new HashSet<char>("\"<>|:*?\\/");
        for (char c = (char)0; c <= (char)31; c++) chars.Add(c);
        chars.Add((char)127);
        foreach (char c in Path.GetInvalidFileNameChars()) chars.Add(c);

        return chars;
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static bool IsInvalidChar(char value) => InvalidChars.Contains(value);

    public static bool IsSafe([NotNullWhen(true)] string? value)
        => !string.IsNullOrWhiteSpace(value) && string.Equals(Sanitize(value), value, StringComparison.Ordinal);

    public static string Sanitize(string? value, string replacement = DefaultReplacement,
        string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        var sb = new StringBuilder(value.Length);
        foreach (char c in value.Trim())
        {
            if (IsInvalidChar(c)) sb.Append(replacement);
            else sb.Append(c);
        }

        string result = sb.ToString().TrimEnd(' ', '.');

        if (result.Length > MaxLength) result = result[..MaxLength].TrimEnd(' ', '.');
        if (result.Length == 0) return fallback;

        return IsReserved(result) ? DefaultReplacement + result : result;
    }

    private static bool IsReserved(string name)
    {
        int dot = name.IndexOf('.');
        return ReservedNames.Contains(dot < 0 ? name : name[..dot]);
    }
}
