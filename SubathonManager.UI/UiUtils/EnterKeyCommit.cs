using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SubathonManager.UI.UiUtils;

public static class EnterKeyCommit {
    public static void Attach(InputElement root, Action commit) {
        Attach(root, _ => commit());
    }

    public static void Attach(InputElement root, Action<object?> commit) {
        root.AddHandler(InputElement.KeyDownEvent, (_, e) => {
            if (e.Key != Key.Enter) return;
            if (!ShouldCommit(e.Source)) return;

            e.Handled = true;
            commit(e.Source);
        }, RoutingStrategies.Bubble);
    }

    private static bool ShouldCommit(object? source) {
        switch (source) {
            case TextBox { AcceptsReturn: true }:
            case ComboBox { IsDropDownOpen: true }:
            case AutoCompleteBox { IsDropDownOpen: true }:
            case Button:
            case MenuItem:
                return false;
            case Visual v:
                return !HasExcludedAncestor(v);
            default:
                return false;
        }
    }

    private static bool HasExcludedAncestor(Visual? v) {
        while (v != null) {
            switch (v) {
                case TextBox { AcceptsReturn: true }:
                case ComboBox { IsDropDownOpen: true }:
                case AutoCompleteBox { IsDropDownOpen: true }:
                case Button:
                case MenuItem:
                    return true;
            }

            v = v.GetVisualParent();
        }

        return false;
    }
}