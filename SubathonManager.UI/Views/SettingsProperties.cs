using Avalonia;

namespace SubathonManager.UI.Views;

public static class SettingsProperties
{
    public static readonly AttachedProperty<bool> ExcludeFromUnsavedProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, bool>("ExcludeFromUnsaved", typeof(SettingsProperties));

    public static void SetExcludeFromUnsaved(AvaloniaObject element, bool value)
        => element.SetValue(ExcludeFromUnsavedProperty, value);

    public static bool GetExcludeFromUnsaved(AvaloniaObject element)
        => element.GetValue(ExcludeFromUnsavedProperty);
}
