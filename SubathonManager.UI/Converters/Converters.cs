using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Globalization;
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
            return b ?  Visibility.Collapsed : Visibility.Visible;
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
                return type == SubathonCommandType.None || type == SubathonCommandType.Unknown 
                    ? Visibility.Visible : Visibility.Collapsed;
            }

            return Visibility.Visible;
        }
        
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
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
}