using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;

namespace SubathonManager.UI.Converters;

public class BoolToProcessedTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as bool? ?? false) ? "Processed" : "Not Processed";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToProcessedColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as bool? ?? false) ? Brushes.LimeGreen : Brushes.OrangeRed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class CommandDeletableToBoolVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not SubathonCommandType type || !type.IsControlTypeCommand();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !(value as bool? ?? false);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class IsNotMetaCommandConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not SubathonCommandType type
           || type is SubathonCommandType.None or SubathonCommandType.Unknown;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class AmountFormatConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 1 || values[0] is not int amount) return string.Empty;
        if (values.Count > 1 && values[1] is SubathonEventType eventType && ((SubathonEventType?)eventType).IsOrder())
            return $"(x{amount} items)";
        return $"x{amount}";
    }
}

public class GreaterThanOneToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch { double d => d > 1, int i => i > 1, _ => false };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class GreaterThanZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch { double d => d > 0, int i => i > 0, _ => false };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class IsNotZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch { double d => d != 0, int i => i != 0, _ => false };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class EventTypeUserBindingConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count == 0) return null;
        if (values.Count < 4) return values[0];

        var user = values[0]?.ToString();
        var item = values[2]?.ToString();
        var currency = values[3]?.ToString();
        var eventTypeMeta = values.Count > 4 ? values[4]?.ToString() : null;

        var text = $"User: {user}";

        if (values[1] is SubathonEventType eventType)
        {
            if (eventType is SubathonEventType.GoAffProOrder && GoAffProOrderHelper.TryGetStore(eventTypeMeta, out var store))
                return $"New {store.StoreName} Order";
            if (eventType is SubathonEventType.JuniperMerchSale)
                return $"New {user} Sale";
            if (eventType is SubathonEventType.ThroneCrowdGiftComplete or SubathonEventType.MakeShipSale or SubathonEventType.MakeShipPledge)
                return item;
            if (eventType is SubathonEventType.ThroneGiftContribution or SubathonEventType.ThroneGiftPurchase)
            {
                if ((currency != "item" && eventType == SubathonEventType.ThroneGiftPurchase)
                    || eventType == SubathonEventType.ThroneGiftContribution)
                    text = $"{text} | {item}";
            }
        }
        return text.Trim();
    }
}

public class EventTypeValueConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count == 0) return null;
        if (values.Count < 3) return values[0];

        var val = values[0]?.ToString();
        var type = "";
        var curr = values[2]?.ToString() ?? "";
        if (values[1] is SubathonEventType eventType)
        {
            if (eventType == SubathonEventType.TwitchRaid)
                type = "viewer";
            else if (eventType == SubathonEventType.JuniperMerchSale)
            {
                type = "";
                val = (values.Count > 4 ? values[4]?.ToString() : null)
                      ?? (values.Count > 3 ? values[3]?.ToString() : null) ?? val;
            }
            else if (curr == "item" && eventType.GetSource() is SubathonEventSource.Throne or SubathonEventSource.TreatStream)
                type = "";
            else
                type = curr;
        }

        if (curr == "sub")
        {
            val = val switch
            {
                "1000" => "Tier 1",
                "2000" => "Tier 2",
                "3000" => "Tier 3",
                _ => val
            };
        }

        return string.IsNullOrEmpty(type.Trim()) ? val! : $"{val} {type}";
    }
}

public partial class CssColorStringToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string str || string.IsNullOrWhiteSpace(str))
            return Colors.White;
        try
        {
            var rgbaMatch = IsRgbaColourParseRegex().Match(str.Trim());
            if (rgbaMatch.Success)
            {
                byte r = byte.Parse(rgbaMatch.Groups[1].Value);
                byte g = byte.Parse(rgbaMatch.Groups[2].Value);
                byte b = byte.Parse(rgbaMatch.Groups[3].Value);
                byte a = rgbaMatch.Groups[4].Success
                    ? (byte)(double.Parse(rgbaMatch.Groups[4].Value, CultureInfo.InvariantCulture) * 255)
                    : (byte)255;
                return Color.FromArgb(a, r, g, b);
            }

            return Color.Parse(str);
        }
        catch
        {
            return Colors.White;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Color c)
        {
            if (c.A == 255)
                return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            return $"rgba({c.R},{c.G},{c.B},{c.A / 255.0:F2})";
        }

        return string.Empty;
    }

    [GeneratedRegex(@"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)(?:\s*,\s*([\d.]+))?\s*\)", RegexOptions.IgnoreCase, "en-CA")]
    private static partial Regex IsRgbaColourParseRegex();
}

public class CssVariableTypeOptionsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is WidgetCssVariableType type ? type.GetOptions() : Array.Empty<string>();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public partial class CssSizeValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var str = value as string ?? "";
        var match = IsNumberRegex().Match(str);
        return match.Success ? match.Value : str;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();

    [GeneratedRegex(@"^-?[\d.]+")]
    private static partial Regex IsNumberRegex();
}

public partial class CssSizeUnitConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var str = value as string ?? "";
        var match = SizeUnitRegex().Match(str);
        return match.Success ? match.Value : "px";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();

    [GeneratedRegex(@"[a-zA-Z%]+$")]
    private static partial Regex SizeUnitRegex();
}

public class NullOrEmptyToNullConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? null : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class StringToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => bool.TryParse(value as string, out var b) && b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool ? value.ToString() : "False") ?? "False";
}

public class EnumDescriptionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Enum e)
            return EnumMetaCache.Get<EnumMetaAttribute>(e)?.Description ?? e.ToString();
        return value?.ToString() ?? "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class VarTooltipConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        switch (value)
        {
            case CssVariable e:
            {
                var type = e.Type == WidgetCssVariableType.Default ? WidgetCssVariableType.String : e.Type;
                var description = string.IsNullOrWhiteSpace(e.Description) ? "" : $"\n{e.Description}";
                return $"{e.Name}\nType: {type}{description}";
            }
            case JsVariable r:
                var jsDescription = string.IsNullOrWhiteSpace(r.Description) ? "" : $"\n{r.Description}";
                return $"{r.Name}\nType: {r.Type}{jsDescription}";
            default:
                return value?.ToString() ?? "";
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class WidgetAssetTypeLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            WidgetType.Image => "Image",
            WidgetType.Video => "Video",
            _ => "Widget"
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class PromptRunStatusColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is SubathonPromptRunStatus status)
        {
            return status switch
            {
                SubathonPromptRunStatus.Active => new SolidColorBrush(Color.FromRgb(80, 180, 255)),
                SubathonPromptRunStatus.Completed => new SolidColorBrush(Color.FromRgb(80, 220, 120)),
                SubathonPromptRunStatus.Expired => new SolidColorBrush(Color.FromRgb(200, 100, 60)),
                SubathonPromptRunStatus.Cancelled => new SolidColorBrush(Color.FromRgb(140, 140, 140)),
                _ => new SolidColorBrush(Colors.White)
            };
        }
        return new SolidColorBrush(Colors.White);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
