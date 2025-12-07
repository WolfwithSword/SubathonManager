using System.Windows.Input;
using System.Globalization;
using Wpf.Ui.Controls;

namespace SubathonManager.UI
{
    public partial class MainWindow
    {
        private void NumberOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !int.TryParse(e.Text, out _);
        }
        
        private void DecimalOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (sender is System.Windows.Controls.TextBox tb)
            {
                e.Handled = !double.TryParse(
                    (tb).Text.Insert((tb).SelectionStart, e.Text),
                    NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out _);
                return;
            }
            e.Handled = !double.TryParse(
                ((TextBox)sender).Text.Insert(((TextBox)sender).SelectionStart, e.Text),
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out _);
        }
    }
}