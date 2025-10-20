using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Globalization;

namespace SubathonManager.UI.Converters
{

    public class BoolToProcessedTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => (bool)value ? "Processed" : "Not Processed";

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class BoolToProcessedColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => (bool)value ? Brushes.LimeGreen : Brushes.OrangeRed;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => (bool)value ? Visibility.Collapsed : Visibility.Visible;

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
    
    public class EventTypeValueConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Length == 0) return null;
            if (values.Length < 3) return values[0];
            
            var val = values[0]?.ToString();
            var type = "";
            var curr = values[2]?.ToString() ?? "";
            if (values[1] is Core.Enums.SubathonEventType eventType)
            {
                if (eventType == Core.Enums.SubathonEventType.TwitchRaid)
                {
                    type = "viewers";
                }
                else
                {
                    type = curr;
                }
            }
            
            
            if (curr == "sub")
            {
                switch (val)
                {
                    case "1000":
                        val = "Tier 1";
                        break;
                    case "2000":
                        val = "Tier 2";
                        break;
                    case "3000":
                        val = "Tier 3";
                        break;
                }
            }

            if (string.IsNullOrEmpty(type.Trim()))
                return val;
            return $"{val} {type}";
        }
        public object[] ConvertBack(object value, Type[] targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}