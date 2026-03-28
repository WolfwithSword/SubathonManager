using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Globalization;
using System.Text.RegularExpressions;
using SubathonManager.Core.Enums;

namespace SubathonManager.UI.Converters
{

    public class BoolToProcessedTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool b = value as bool? ?? false;
            return b ? "Processed" : "Not Processed";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class BoolToProcessedColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool b = value as bool? ?? false;
            return b ? Brushes.LimeGreen : Brushes.OrangeRed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class CommandDeletableToBoolVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is SubathonCommandType type)
            {
                return type.IsControlTypeCommand() ? Visibility.Hidden : Visibility.Visible;
            }

            return Visibility.Visible;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool b = value as bool? ?? false;
            return b ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class IsNotMetaCommandConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is SubathonCommandType type)
            {
                return type is SubathonCommandType.None or SubathonCommandType.Unknown
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            return Visibility.Visible;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class AmountFormatConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values[0] is not int amount) return string.Empty;
            if (values[1] is SubathonEventType eventType && ((SubathonEventType?)eventType).IsOrder())
                return $"(x{amount} items)";
            return $"x{amount}";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class GreaterThanOneToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is double d) return d > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (value is int i) return i > 1 ? Visibility.Visible : Visibility.Collapsed;
            return Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class GreaterThanZeroToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is double d) return d > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (value is int i) return i > 0 ? Visibility.Visible : Visibility.Collapsed;
            return Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class EventTypeValueConverter : IMultiValueConverter
    {
        public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Length == 0) return null;
            if (values.Length < 3) return values[0];

            var val = values[0]?.ToString();
            var type = "";
            var curr = values[2]?.ToString() ?? "";
            if (values[1] is SubathonEventType eventType)
            {
                type = eventType == SubathonEventType.TwitchRaid ? "viewers" : curr;
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

        public object[] ConvertBack(object value, Type[] targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
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

                return (Color)ColorConverter.ConvertFromString(str);
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

        [GeneratedRegex(@"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)(?:\s*,\s*([\d.]+))?\s*\)", RegexOptions.IgnoreCase,
            "en-CA")]
        private static partial Regex IsRgbaColourParseRegex();
    }

    public class CssVariableTypeOptionsConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is WidgetCssVariableType type)
                return type.GetOptions();
            return Array.Empty<string>();
        }

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
}