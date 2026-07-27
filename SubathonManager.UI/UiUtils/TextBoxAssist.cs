using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SubathonManager.UI.Controls;

namespace SubathonManager.UI.UiUtils;

public static class TextBoxAssist
{
    public static readonly AttachedProperty<bool> RevealProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Reveal", typeof(TextBoxAssist));

    public static bool GetReveal(TextBox t) => t.GetValue(RevealProperty);
    public static void SetReveal(TextBox t, bool v) => t.SetValue(RevealProperty, v);

    public static readonly AttachedProperty<bool> ClearProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Clear", typeof(TextBoxAssist));

    public static bool GetClear(TextBox t) => t.GetValue(ClearProperty);
    public static void SetClear(TextBox t, bool v) => t.SetValue(ClearProperty, v);

    static TextBoxAssist()
    {
        RevealProperty.Changed.AddClassHandler<TextBox>((t, _) => Rebuild(t));
        ClearProperty.Changed.AddClassHandler<TextBox>((t, _) => Rebuild(t));
    }

    private static void Rebuild(TextBox tb)
    {
        bool reveal = GetReveal(tb);
        bool clear = GetClear(tb);
        if (!reveal && !clear)
        {
            tb.InnerRightContent = null;
            return;
        }

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (clear) panel.Children.Add(BuildClearButton(tb));
        if (reveal) panel.Children.Add(BuildRevealButton(tb));
        tb.InnerRightContent = panel;
    }

    private static Button BuildClearButton(TextBox tb)
    {
        var btn = new Button
        {
            Width = 26,
            Height = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 2, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new SymIcon { Glyph = "Dismiss16" }
        };
        btn.SetDynamicResource(TemplatedControl.ForegroundProperty, "TextFillColorSecondaryBrush");
        ToolTip.SetTip(btn, "Clear");
        btn.Click += (_, _) => { tb.Clear(); tb.Focus(); };

        void Update() => btn.IsVisible = !string.IsNullOrEmpty(tb.Text) && !tb.IsReadOnly;
        tb.TextChanged += (_, _) => Update();
        Update();
        return btn;
    }

    private static ToggleButton BuildRevealButton(TextBox tb)
    {
        var icon = new SymIcon { Glyph = "Eye16" };
        var btn = new ToggleButton
        {
            Width = 26,
            Height = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 2, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            Content = icon
        };
        btn.SetDynamicResource(TemplatedControl.ForegroundProperty, "TextFillColorPrimaryBrush");
        ToolTip.SetTip(btn, "Show / hide");
        btn.IsCheckedChanged += (_, _) =>
        {
            bool on = btn.IsChecked == true;
            tb.RevealPassword = on;
            icon.Glyph = on ? "EyeOff16" : "Eye16";
        };
        return btn;
    }
}
