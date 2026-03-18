using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SubathonManager.UI.Views;

public partial class CssColorPicker : UserControl
{
    public static readonly DependencyProperty CssColorProperty =
        DependencyProperty.Register(
            nameof(CssColor), typeof(string), typeof(CssColorPicker),
            new FrameworkPropertyMetadata(string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnCssColorPropertyChanged));
    
    public static readonly RoutedEvent ColorChangedEvent =
        EventManager.RegisterRoutedEvent(
            nameof(ColorChanged),
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(CssColorPicker));

    public event RoutedEventHandler ColorChanged
    {
        add => AddHandler(ColorChangedEvent, value);
        remove => RemoveHandler(ColorChangedEvent, value);
    }
    
    [GeneratedRegex(@"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)(?:\s*,\s*([\d.]+))?\s*\)", RegexOptions.IgnoreCase, "en-CA")]
    private static partial Regex IsRgbaColourParseRegex();
    
    public string CssColor
    {
        get => (string)GetValue(CssColorProperty);
        set => SetValue(CssColorProperty, value);
    }

    private static void OnCssColorPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CssColorPicker picker)
        {
            if (!picker._updatingFromInternal) picker.ApplyCssString((string)e.NewValue);
            picker.RaiseEvent(new RoutedEventArgs(ColorChangedEvent));
        }
    }

    private double _hue = 0;
    private double _saturation = 1;
    private double _value = 1;
    private double _alpha = 1;

    private bool _updatingFromInternal = false;
    private bool _draggingSv = false;
    private bool _draggingSlider = false;

    public CssColorPicker()
    {
        InitializeComponent();      
        Loaded += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(CssColor))
                ApplyCssString(CssColor);
            else
                UpdateSwatchFromCurrentHsva();
        };
    }

    private void ColorSlider_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        _draggingSlider = true;
        el.CaptureMouse();
        SetSliderValueFromMouse(el, e.GetPosition(el));
        e.Handled = true;
    }

    private void ColorSlider_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingSlider || sender is not FrameworkElement el) return;
        SetSliderValueFromMouse(el, e.GetPosition(el));
    }

    private void ColorSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        _draggingSlider = false;
        el.ReleaseMouseCapture();
    }

    private static void SetSliderValueFromMouse(FrameworkElement el, Point pos)
    {
        var slider = el.TemplatedParent as Slider ?? (el.Parent as FrameworkElement)?.TemplatedParent as Slider;
        if (slider == null) return;
        double ratio = Math.Clamp(pos.X / el.ActualWidth, 0, 1);
        slider.Value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
    }

    private void SwatchButton_Click(object sender, RoutedEventArgs e)
    {
        PickerPopup.IsOpen = !PickerPopup.IsOpen;
        if (PickerPopup.IsOpen)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                PlaceSvThumb);
        }
    }

    private void CssValueBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingFromInternal) return;
        ApplyCssString(CssValueBox.Text);
        SyncDpToCurrentColor();
    }

    private void CssValueBox_LostFocus(object sender, RoutedEventArgs e)
    {
        SyncDpToCurrentColor();
    }

    private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingFromInternal) return;
        _hue = HueSlider.Value;
        UpdateHueBackground();
        UpdateAlphaGradient();
        SyncUiFromHsva();
    }

    private void AlphaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingFromInternal) return;
        _alpha = AlphaSlider.Value;
        SyncUiFromHsva();
    }

    private void SvCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingSv = true;
        ((UIElement)sender).CaptureMouse();
        UpdateSvFromMouse(e.GetPosition(SvThumbCanvas));
    }

    private void SvCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingSv) return;
        UpdateSvFromMouse(e.GetPosition(SvThumbCanvas));
    }

    private void SvCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _draggingSv = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void UpdateSvFromMouse(Point pos)
    {
        double w = SvThumbCanvas.ActualWidth;
        double h = SvThumbCanvas.ActualHeight;
        _saturation = Math.Clamp(pos.X / w, 0, 1);
        _value = Math.Clamp(1.0 - (pos.Y / h), 0, 1);
        PlaceSvThumb();
        SyncUiFromHsva();
    }

    private void HexInputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingFromInternal) return;
        var hex = HexInputBox.Text.Trim();
        if (!hex.StartsWith('#')) hex = '#' + hex;
        if (TryParseColor(hex, out var c))
        {
            ColorToHsva(c, out _hue, out _saturation, out _value, out _alpha);
            RefreshAllControls();
            SyncDpToCurrentColor();
        }
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        SyncDpToCurrentColor();
        PickerPopup.IsOpen = false;
    }

    private void ApplyCssString(string? css)
    {
        if (string.IsNullOrWhiteSpace(css)) return;
        
        if (!TryParseColor(css, out var color)) return;
        
        ColorToHsva(color, out _hue, out _saturation, out _value, out _alpha);
        RefreshAllControls();
        UpdateSwatchFill(color);
        _updatingFromInternal = true;
        try { CssValueBox.Text = css; }
        finally { _updatingFromInternal = false; }
    }

    private void SyncUiFromHsva()
    {
        var color = HsvaToColor(_hue, _saturation, _value, _alpha);
        RefreshAllControls();
        UpdateSwatchFill(color);
        SyncDpToCurrentColor();
    }

    private void SyncDpToCurrentColor()
    {
        var color = HsvaToColor(_hue, _saturation, _value, _alpha);
        var cssStr = ColorToCssString(color);
        _updatingFromInternal = true;
        try
        {
            CssColor = cssStr;
            if (CssValueBox.Text != cssStr) CssValueBox.Text = cssStr;
        }
        finally
        {
            _updatingFromInternal = false;
        }
    }

    private void UpdateSwatchFromCurrentHsva()
    {
        var color = HsvaToColor(_hue, _saturation, _value, _alpha);
        UpdateSwatchFill(color);
    }

    private void RefreshAllControls()
    {
        _updatingFromInternal = true;
        try
        {
            var color = HsvaToColor(_hue, _saturation, _value, _alpha);

            HueSlider.Value = _hue;
            AlphaSlider.Value = _alpha;
            PlaceSvThumb();
            UpdateHueBackground();
            UpdateAlphaGradient();

            PreviewSwatch.Background = new SolidColorBrush(color);

            var hex = _alpha < 1
                ? $"#{(byte)(_alpha * 255):X2}{color.R:X2}{color.G:X2}{color.B:X2}"
                : $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            if (HexInputBox.Text != hex) HexInputBox.Text = hex;

            UpdateSwatchFill(color);
        }
        finally
        {
            _updatingFromInternal = false;
        }
    }

    private void UpdateSwatchFill(Color color)
    {
        if (SwatchButton.Template?.FindName("SwatchFill", SwatchButton) is System.Windows.Controls.Border fill)
            fill.Background = new SolidColorBrush(color);
    }

    private void PlaceSvThumb()
    {
        double w = SvThumbCanvas.ActualWidth;
        double h = SvThumbCanvas.ActualHeight;
        if (w == 0 || h == 0) return;
        double x = _saturation * w - SvThumb.Width / 2;
        double y = (1 - _value) * h - SvThumb.Height / 2;
        Canvas.SetLeft(SvThumb, x);
        Canvas.SetTop(SvThumb, y);
    }

    private void UpdateHueBackground()
    {
        var hueColor = HsvaToColor(_hue, 1, 1, 1);
        HueBackground.Background = new SolidColorBrush(hueColor);
    }

    private void UpdateAlphaGradient()
    {
        var opaque = HsvaToColor(_hue, _saturation, _value, 1);
        var transparent = Color.FromArgb(0, opaque.R, opaque.G, opaque.B);
        AlphaGradientBorder.Background = new LinearGradientBrush(transparent, opaque, 0);
    }

    private static Color HsvaToColor(double h, double s, double v, double a)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;
        double r, g, b;
        if (h < 60)      { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else              { r = c; g = 0; b = x; }
        return Color.FromArgb(
            (byte)(a * 255),
            (byte)((r + m) * 255),
            (byte)((g + m) * 255),
            (byte)((b + m) * 255));
    }

    private static void ColorToHsva(Color c, out double h, out double s, out double v, out double a)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        v = max;
        s = max == 0 ? 0 : delta / max;
        if (delta == 0) h = 0;
        else if (max.Equals(r)) h = 60 * (((g - b) / delta) % 6);
        else if (max.Equals(g)) h = 60 * (((b - r) / delta) + 2);
        else               h = 60 * (((r - g) / delta) + 4);
        if (h < 0) h += 360;
        a = c.A / 255.0;
    }

    private static string ColorToCssString(Color c)
    {
        if (c.A == 255)
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        return $"rgba({c.R},{c.G},{c.B},{c.A / 255.0:F2})";
    }

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

        try
        {
            color = (Color)ColorConverter.ConvertFromString(css);
            return true;
        }
        catch { return false; }
    }

}