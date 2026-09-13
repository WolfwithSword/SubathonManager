using Avalonia;

namespace SubathonManager.UI.Views;

public static class SettingsProperties {
    public static readonly AttachedProperty<bool> ExcludeFromUnsavedProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, bool>("ExcludeFromUnsaved", typeof(SettingsProperties));

    public static readonly AttachedProperty<bool> UnsavedHandlerAttachedProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, bool>("UnsavedHandlerAttached", typeof(SettingsProperties));

    public static void SetExcludeFromUnsaved(AvaloniaObject element, bool value) {
        element.SetValue(ExcludeFromUnsavedProperty, value);
    }

    public static bool GetExcludeFromUnsaved(AvaloniaObject element) {
        return element.GetValue(ExcludeFromUnsavedProperty);
    }

    public static void SetUnsavedHandlerAttached(AvaloniaObject element, bool value) {
        element.SetValue(UnsavedHandlerAttachedProperty, value);
    }

    public static bool GetUnsavedHandlerAttached(AvaloniaObject element) {
        return element.GetValue(UnsavedHandlerAttachedProperty);
    }
}