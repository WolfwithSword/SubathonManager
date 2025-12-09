using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SubathonManager.UI.Validation
{
    public static class InputValidationBehavior
    {
        public static readonly DependencyProperty IsNumberOnlyProperty =
            DependencyProperty.RegisterAttached(
                "IsNumberOnly",
                typeof(bool),
                typeof(InputValidationBehavior),
                new PropertyMetadata(false, OnIsNumberOnlyChanged));

        public static void SetIsNumberOnly(UIElement element, bool value) =>
            element.SetValue(IsNumberOnlyProperty, value);

        public static bool GetIsNumberOnly(UIElement element) =>
            (bool)element.GetValue(IsNumberOnlyProperty);

        private static void OnIsNumberOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBoxBase || d is Wpf.Ui.Controls.TextBox)
            {
                if ((bool)e.NewValue)
                    ((UIElement)d).PreviewTextInput += NumberOnlyHandler;
                else
                    ((UIElement)d).PreviewTextInput -= NumberOnlyHandler;
            }
        }

        private static void NumberOnlyHandler(object sender, TextCompositionEventArgs e)
        {
            string text = "";

            switch (sender)
            {
                case TextBox tb:
                    text = tb.Text.Insert(tb.SelectionStart, e.Text);
                    break;
                default:
                    e.Handled = true;
                    return;
            }

            e.Handled = !int.TryParse(text, out _);
        }

        public static readonly DependencyProperty IsDecimalOnlyProperty =
            DependencyProperty.RegisterAttached(
                "IsDecimalOnly",
                typeof(bool),
                typeof(InputValidationBehavior),
                new PropertyMetadata(false, OnIsDecimalOnlyChanged));

        public static void SetIsDecimalOnly(UIElement element, bool value) =>
            element.SetValue(IsDecimalOnlyProperty, value);

        public static bool GetIsDecimalOnly(UIElement element) =>
            (bool)element.GetValue(IsDecimalOnlyProperty);

        private static void OnIsDecimalOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBoxBase || d is Wpf.Ui.Controls.TextBox)
            {
                if ((bool)e.NewValue)
                    ((UIElement)d).PreviewTextInput += DecimalOnlyHandler;
                else
                    ((UIElement)d).PreviewTextInput -= DecimalOnlyHandler;
            }
        }

        private static void DecimalOnlyHandler(object sender, TextCompositionEventArgs e)
        {
            string newText = "";

            switch (sender)
            {
                case TextBox tb:
                    newText = tb.Text.Insert(tb.SelectionStart, e.Text);
                    break;
                default:
                    e.Handled = true;
                    return;
            }

            e.Handled = !double.TryParse(
                newText,
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out _);
        }
    }
}
