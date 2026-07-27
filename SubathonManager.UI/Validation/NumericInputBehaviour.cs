using System.Globalization;
using Avalonia;
using Avalonia.Controls;

namespace SubathonManager.UI.Validation;

public static class NumericInputBehaviour
{
    public enum NumericMode
    {
        None,
        Integer,
        SignedInteger,
        Decimal,
        SignedDecimal
    }

    public static readonly AttachedProperty<NumericMode> ModeProperty =
        AvaloniaProperty.RegisterAttached<TextBox, NumericMode>(
            "Mode", typeof(NumericInputBehaviour), NumericMode.None);

    public static void SetMode(TextBox element, NumericMode value) => element.SetValue(ModeProperty, value);
    public static NumericMode GetMode(TextBox element) => element.GetValue(ModeProperty);

    private static readonly Dictionary<TextBox, string> LastValid = new();

    static NumericInputBehaviour()
    {
        ModeProperty.Changed.AddClassHandler<TextBox>(OnModeChanged);
    }

    private static void OnModeChanged(TextBox tb, AvaloniaPropertyChangedEventArgs e)
    {
        tb.TextChanged -= OnTextChanged;
        LastValid.Remove(tb);
        if (GetMode(tb) != NumericMode.None)
        {
            LastValid[tb] = tb.Text ?? string.Empty;
            tb.TextChanged += OnTextChanged;
            tb.DetachedFromVisualTree += (_, _) => LastValid.Remove(tb);
        }
    }

    private static void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var mode = GetMode(tb);
        if (mode == NumericMode.None) return;

        var text = tb.Text ?? string.Empty;
        if (IsAcceptable(text, mode))
        {
            LastValid[tb] = text;
            return;
        }
        var previous = LastValid.TryGetValue(tb, out var v) ? v : string.Empty;
        int caret = Math.Min(tb.CaretIndex, previous.Length);
        tb.Text = previous;
        tb.CaretIndex = caret;
    }

    private static bool IsAcceptable(string text, NumericMode mode)
    {
        if (text.Length == 0) return true;

        bool allowSign = mode is NumericMode.SignedInteger or NumericMode.SignedDecimal;
        bool allowDecimal = mode is NumericMode.Decimal or NumericMode.SignedDecimal;

        if (text == "-") return allowSign;
        if (text == ".") return allowDecimal;
        if (text == "-." ) return allowSign && allowDecimal;

        if (allowDecimal)
        {
            var styles = NumberStyles.AllowDecimalPoint | (allowSign ? NumberStyles.AllowLeadingSign : NumberStyles.None);
            return double.TryParse(text, styles, CultureInfo.InvariantCulture, out _);
        }

        var intStyles = allowSign ? NumberStyles.AllowLeadingSign : NumberStyles.None;
        return int.TryParse(text, intStyles, CultureInfo.InvariantCulture, out _)
               || long.TryParse(text, intStyles, CultureInfo.InvariantCulture, out _);
    }
}
