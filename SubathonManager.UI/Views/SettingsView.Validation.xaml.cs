using System.Windows.Input;
using System.Globalization;
using Wpf.Ui.Controls;

namespace SubathonManager.UI.Views
{
    public partial class SettingsView
    {
        public void NumberOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !int.TryParse(e.Text, out _);
        }
        
        public void DecimalOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !double.TryParse(
                ((TextBox)sender).Text.Insert(((TextBox)sender).SelectionStart, e.Text),
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out _);
        }
    }
}