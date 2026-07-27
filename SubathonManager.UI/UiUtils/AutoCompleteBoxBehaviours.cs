using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace SubathonManager.UI.UiUtils;

public static class AutoCompleteBoxBehaviours
{
    public static readonly AttachedProperty<bool> OpenOnFocusProperty =
        AvaloniaProperty.RegisterAttached<AutoCompleteBox, bool>("OpenOnFocus", typeof(AutoCompleteBoxBehaviours));

    public static void SetOpenOnFocus(AutoCompleteBox box, bool value) => box.SetValue(OpenOnFocusProperty, value);
    public static bool GetOpenOnFocus(AutoCompleteBox box) => box.GetValue(OpenOnFocusProperty);

    static AutoCompleteBoxBehaviours()
    {
        OpenOnFocusProperty.Changed.AddClassHandler<AutoCompleteBox>((box, e) =>
        {
            if (e.OldValue is true || e.NewValue is not true) return;
            box.GotFocus += (_, _) => box.IsDropDownOpen = true;
            box.TemplateApplied += OnTemplateApplied;
        });
    }

    private static void OnTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (sender is not AutoCompleteBox box) return;

        var brush = box.TryFindResource("TextFillColorSecondaryBrush", box.ActualThemeVariant, out var b) && b is IBrush ib
            ? ib
            : Brushes.Gray;

        if (e.NameScope.Find<Popup>("PART_Popup")?.Child is Border popupBorder)
        {
            popupBorder.BorderBrush = brush;
            popupBorder.BorderThickness = new Thickness(0.8);
            return;
        }

        if (e.NameScope.Find<TemplatedControl>("PART_SelectingItemsControl") is { } list)
        {
            list.BorderBrush = brush;
            list.BorderThickness = new Thickness(0.8);
        }
    }
}
