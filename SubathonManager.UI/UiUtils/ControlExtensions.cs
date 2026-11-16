using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace SubathonManager.UI.UiUtils;

// trying to emulate WPF visual behaviours 

public enum Appearance {
    Transparent,
    Secondary,
    Primary,
    Danger
}

public static class ControlExtensions {
    private static readonly IBrush DangerBrush = new ImmutableSolidColorBrush(Color.Parse("#C42B1C"));

    public static void SubscribeWhileAttached(this Control control, Action subscribe, Action unsubscribe) {
        var subscribed = false;

        void Attach() {
            if (subscribed) return;
            subscribed = true;
            subscribe();
        }

        void Detach() {
            if (!subscribed) return;
            subscribed = false;
            unsubscribe();
        }

        control.AttachedToVisualTree += (_, _) => Attach();
        control.DetachedFromVisualTree += (_, _) => Detach();
        if (TopLevel.GetTopLevel(control) != null) Attach();
    }

    public static void SetDynamicResource(this Control control, AvaloniaProperty property, object resourceKey) {
        control.Bind(property, control.GetResourceObservable(resourceKey));
    }

    public static void StyleAsTab(this Button button, bool topRounded = true) {
        button.CornerRadius = topRounded ? new CornerRadius(6, 6, 0, 0) : new CornerRadius(6);
        button.BorderThickness = topRounded ? new Thickness(1, 1, 1, 0) : new Thickness(1);
        button.Padding = new Thickness(10, 6, 10, 6);
        if (!button.Classes.Contains("tab")) button.Classes.Add("tab");
    }

    public static void SetTabActive(this Button button, bool active) {
        if (active) {
            button.SetDynamicResource(TemplatedControl.BackgroundProperty, "OpaqueSecondaryBackground");
            button.SetDynamicResource(TemplatedControl.BorderBrushProperty, "AccentFillColorDefaultBrush");
        }
        else {
            button.SetDynamicResource(TemplatedControl.BackgroundProperty, "ControlFillColorDefaultBrush");
            button.SetDynamicResource(TemplatedControl.BorderBrushProperty, "ControlElevationBorderBrush");
        }
    }

    public static Button ApplyAppearance(this Button button, Appearance appearance) {
        switch (appearance) {
            case Appearance.Transparent:
                button.Background = Brushes.Transparent;
                button.BorderBrush = Brushes.Transparent;
                break;
            case Appearance.Secondary:
                button.SetDynamicResource(TemplatedControl.BackgroundProperty, "OpaqueSecondaryBackground");
                button.SetDynamicResource(TemplatedControl.BorderBrushProperty, "AccentFillColorDefaultBrush");
                break;
            case Appearance.Primary:
                if (!button.Classes.Contains("accent")) button.Classes.Add("accent");
                break;
            case Appearance.Danger:
                button.Background = DangerBrush;
                button.Foreground = Brushes.White;
                break;
        }

        return button;
    }
}