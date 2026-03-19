using System.Windows;

namespace SubathonManager.UI.Views;

public static class SettingsProperties
{
    public static readonly DependencyProperty ExcludeFromUnsavedProperty =
        DependencyProperty.RegisterAttached(
            "ExcludeFromUnsaved",
            typeof(bool),
            typeof(SettingsProperties),
            new PropertyMetadata(false));

    public static void SetExcludeFromUnsaved(DependencyObject element, bool value)
        => element.SetValue(ExcludeFromUnsavedProperty, value);

    public static bool GetExcludeFromUnsaved(DependencyObject element)
        => (bool)element.GetValue(ExcludeFromUnsavedProperty);
}