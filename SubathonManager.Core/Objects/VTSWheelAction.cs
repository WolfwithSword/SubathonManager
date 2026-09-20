using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using SubathonManager.Core.Enums;

namespace SubathonManager.Core.Objects;

[ExcludeFromCodeCoverage]
public sealed class VTSWheelAction {
    public const string ParameterSeparator = "|";

    public VtsTargetKind Kind { get; set; } = VtsTargetKind.Expression;
    public string Target { get; set; } = "";
    public VtsToggleAction ToggleAction { get; set; } = VtsToggleAction.On;
    public double Value { get; set; }
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;

    public VtsToggleAction AfterToggle { get; set; } = VtsToggleAction.DoNothing;
    public VtsParameterAfterAction AfterParameter { get; set; } = VtsParameterAfterAction.DoNothing;
    public VtsHotkeyAfterAction AfterHotkey { get; set; } = VtsHotkeyAfterAction.DoNothing;

    public double AfterValue { get; set; }

    public bool HasRevert => Duration > TimeSpan.Zero && Kind switch {
        VtsTargetKind.Expression => AfterToggle != VtsToggleAction.DoNothing,
        VtsTargetKind.Parameter => true,
        VtsTargetKind.Hotkey => AfterHotkey != VtsHotkeyAfterAction.DoNothing,
        _ => false
    };

    public string TimerKey => $"vts-wheel-{Kind}-{Target}".ToLowerInvariant();

    public static bool TryParse(string? parameter, [NotNullWhen(true)] out VTSWheelAction? action) {
        action = null;
        if (string.IsNullOrWhiteSpace(parameter)) return false;

        string[] parts = parameter.Split(ParameterSeparator);
        if (parts.Length < 5) return false;
        if (!Enum.TryParse(parts[0], true, out VtsTargetKind kind)) return false;

        var parsed = new VTSWheelAction {
            Kind = kind,
            Target = parts[1].Trim()
        };

        switch (kind) {
            case VtsTargetKind.Expression:
                if (!Enum.TryParse(parts[2], true, out VtsToggleAction toggle)) return false;
                parsed.ToggleAction = toggle;
                break;
            case VtsTargetKind.Parameter:
                if (!TryParseNumber(parts[2], out double value)) return false;
                parsed.Value = value;
                break;
            case VtsTargetKind.Hotkey:
                break;
            default:
                return false;
        }

        parsed.Duration = int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
                          && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;

        switch (kind) {
            case VtsTargetKind.Expression:
                parsed.AfterToggle = Enum.TryParse(parts[4], true, out VtsToggleAction afterToggle)
                    ? afterToggle
                    : VtsToggleAction.DoNothing;
                break;
            case VtsTargetKind.Parameter:
                parsed.AfterParameter = Enum.TryParse(parts[4], true, out VtsParameterAfterAction afterParam)
                    ? afterParam
                    : VtsParameterAfterAction.DoNothing;
                break;
            case VtsTargetKind.Hotkey:
                parsed.AfterHotkey = Enum.TryParse(parts[4], true, out VtsHotkeyAfterAction afterHotkey)
                    ? afterHotkey
                    : VtsHotkeyAfterAction.DoNothing;
                break;
        }

        if (parts.Length >= 6 && TryParseNumber(parts[5], out double afterValue))
            parsed.AfterValue = afterValue;

        action = parsed;
        return true;
    }

    public string ToParameterString() {
        string initial = Kind switch {
            VtsTargetKind.Expression => ToggleAction.ToString(),
            VtsTargetKind.Parameter => Value.ToString(CultureInfo.InvariantCulture),
            _ => "Trigger"
        };

        string after = Kind switch {
            VtsTargetKind.Expression => AfterToggle.ToString(),
            VtsTargetKind.Parameter => AfterParameter.ToString(),
            _ => AfterHotkey.ToString()
        };

        string afterValue = Kind == VtsTargetKind.Parameter && AfterParameter == VtsParameterAfterAction.SetNewValue
            ? AfterValue.ToString(CultureInfo.InvariantCulture)
            : "";

        return string.Join(ParameterSeparator,
            Kind.ToString(),
            Target.Trim(),
            initial,
            ((int)Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            after,
            afterValue);
    }

    public bool IsValid(out string error) {
        if (string.IsNullOrWhiteSpace(Target)) {
            error = Kind switch {
                VtsTargetKind.Expression => "Pick an expression file",
                VtsTargetKind.Parameter => "Pick a parameter name",
                _ => "Pick a hotkey"
            };
            return false;
        }

        switch (Kind) {
            // if not do nothing but no duration is set
            case VtsTargetKind.Expression when ToggleAction == VtsToggleAction.DoNothing:
                error = "The rolled action cannot be 'Do Nothing' - it would never do anything";
                return false;
            case VtsTargetKind.Parameter when AfterParameter == VtsParameterAfterAction.SetNewValue
                                              && Duration <= TimeSpan.Zero:
                error = "A duration is required to set a value after the timer";
                return false;
            case VtsTargetKind.Expression when AfterToggle != VtsToggleAction.DoNothing
                                               && Duration <= TimeSpan.Zero:
                error = "A duration is required for the action after the timer";
                return false;
            case VtsTargetKind.Hotkey when AfterHotkey != VtsHotkeyAfterAction.DoNothing
                                           && Duration <= TimeSpan.Zero:
                error = "A duration is required to trigger the hotkey again";
                return false;
            default:
                error = "";
                return true;
        }
    }

    public string Describe() {
        string initial = Kind switch {
            VtsTargetKind.Expression => ToggleAction.ToString(),
            VtsTargetKind.Parameter => $"= {Value.ToString(CultureInfo.InvariantCulture)}",
            _ => "trigger"
        };

        if (!HasRevert) return $"{Target} {initial}";

        string after = Kind switch {
            VtsTargetKind.Expression => AfterToggle.ToString(),
            VtsTargetKind.Parameter => AfterParameter == VtsParameterAfterAction.SetNewValue
                ? $"= {AfterValue.ToString(CultureInfo.InvariantCulture)}"
                : AfterParameter.ToString(),
            _ => AfterHotkey.ToString()
        };

        return $"{Target} {initial} for {FormatDuration(Duration)} then {after}";
    }

    private static string FormatDuration(TimeSpan duration) {
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours}h{duration.Minutes:00}m";
        if (duration.TotalMinutes >= 1) return $"{(int)duration.TotalMinutes}m{duration.Seconds:00}s";
        return $"{(int)duration.TotalSeconds}s";
    }

    private static bool TryParseNumber(string text, out double value) {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}