using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using SubathonManager.UI.Views;

namespace SubathonManager.UI.UiUtils;

public static class DirtySaveGuard {
    private static readonly ConditionalWeakTable<AvaloniaObject, Baseline> Baselines = new();

    public static bool TryGetValue(object? control, out object? value) {
        switch (control) {
            case TextBox tb:
                value = tb.Text ?? "";
                return true;
            case AutoCompleteBox acb:
                value = acb.Text ?? "";
                return true;
            case ToggleButton toggle:
                value = toggle.IsChecked;
                return true;
            case ComboBox cb:
                value = cb.SelectedItem ?? cb.SelectedIndex;
                return true;
            case NumericUpDown nud:
                value = nud.Value;
                return true;
            case RangeBase range:
                value = range.Value;
                return true;
            case CssColorPicker picker:
                value = picker.CssColor ?? "";
                return true;
            default:
                value = null;
                return false;
        }
    }

    public static void Rebase(object? control) {
        if (control is not AvaloniaObject obj || !TryGetValue(control, out object? value)) return;
        if (Baselines.TryGetValue(obj, out Baseline? baseline)) baseline.Value = value;
        else Baselines.Add(obj, new Baseline { Value = value });
    }

    public static void RebaseAll(Visual? root) {
        if (root == null) return;
        Rebase(root);
        foreach (Visual child in root.GetVisualChildren())
            RebaseAll(child);
    }

    public static bool Consume(object? control) {
        if (control is not AvaloniaObject obj || !TryGetValue(control, out object? current)) return true;

        if (!Baselines.TryGetValue(obj, out Baseline? baseline)) {
            Baselines.Add(obj, new Baseline { Value = current });
            return true;
        }

        if (Equals(baseline.Value, current)) return false;

        baseline.Value = current;
        return true;
    }

    private sealed class Baseline {
        public object? Value;
    }
}