using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace SubathonManager.UI.Views;

public partial class CssColorPicker : UserControl
{
    public static readonly StyledProperty<string> CssColorProperty =
        AvaloniaProperty.Register<CssColorPicker, string>(
            nameof(CssColor), string.Empty, defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);

    public string CssColor
    {
        get => GetValue(CssColorProperty);
        set => SetValue(CssColorProperty, value);
    }
    
    public event EventHandler<RoutedEventArgs>? ColorChanged;
    private bool _updatingFromInternal;

    public CssColorPicker()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(CssColor))
                ApplyCssString(CssColor);
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CssColorProperty && !_updatingFromInternal)
            ApplyCssString(change.GetNewValue<string>());
    }

    private void ColorView_ColorChanged(object? sender, ColorChangedEventArgs e)
    {
        if (_updatingFromInternal) return;
        SyncFromColor(e.NewColor);
    }

    private void CssValueBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updatingFromInternal) return;
        ApplyCssString(CssValueBox.Text ?? string.Empty);
    }

    private void CssValueBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (TryParseColor(CssValueBox.Text, out var c)) SyncFromColor(c);
    }

    private void SyncFromColor(Color color)
    {
        var css = ColorToCssString(color);
        bool changed = css != CssColor;
        _updatingFromInternal = true;
        try
        {
            if (ColorViewCtl.Color != color) ColorViewCtl.Color = color;
            SwatchFill.Background = new SolidColorBrush(color);
            if (CssValueBox.Text != css) CssValueBox.Text = css;
            CssColor = css;
        }
        finally
        {
            _updatingFromInternal = false;
        }
        if (changed) ColorChanged?.Invoke(this, new RoutedEventArgs());
    }

    private void ApplyCssString(string? css)
    {
        if (string.IsNullOrWhiteSpace(css) || !TryParseColor(css, out var color)) return;
        _updatingFromInternal = true;
        try
        {
            if (ColorViewCtl.Color != color) ColorViewCtl.Color = color;
            SwatchFill.Background = new SolidColorBrush(color);
            if (CssValueBox.Text != css) CssValueBox.Text = css;
        }
        finally
        {
            _updatingFromInternal = false;
        }
    }

    private static string ColorToCssString(Color c)
        => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"rgba({c.R},{c.G},{c.B},{c.A / 255.0:F2})";

    [GeneratedRegex(@"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)(?:\s*,\s*([\d.]+))?\s*\)", RegexOptions.IgnoreCase, "en-CA")]
    private static partial Regex IsRgbaColourParseRegex();

    private static bool TryParseColor(string? css, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(css)) return false;
        css = css.Trim();

        var m = IsRgbaColourParseRegex().Match(css);
        if (m.Success)
        {
            byte r = byte.Parse(m.Groups[1].Value);
            byte g = byte.Parse(m.Groups[2].Value);
            byte b = byte.Parse(m.Groups[3].Value);
            byte a = m.Groups[4].Success
                ? (byte)(double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) * 255)
                : (byte)255;
            color = Color.FromArgb(a, r, g, b);
            return true;
        }

        return Color.TryParse(css, out color);
    }
}
