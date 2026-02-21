using System.Windows.Controls;
using System.Windows;
using SubathonManager.Integration;

namespace SubathonManager.UI.Views.SettingsViews;

public partial class YouTubeSettings : UserControl
{
   
    private void TestYTSuperChat_Click(object sender, RoutedEventArgs e)
    {
        var value = SimulateSCAmt.Text;
        var currency = CurrencyBox.Text;
        YouTubeService.SimulateSuperChat(value, currency);
    }
    
    private void TestYTMembership_Click(object sender, RoutedEventArgs e)
    {
        string selectedTier = (SimTierSelection.SelectedItem is ComboBoxItem item) 
            ? item.Content?.ToString() ?? "DEFAULT" 
            : "DEFAULT";     
        YouTubeService.SimulateMembership(selectedTier);
    }

    private void TestYTGiftMembership_Click(object sender, RoutedEventArgs e)
    {
        int amount = int.TryParse(SimGiftMembershipAmtInput.Text, out var parsedAmountInt) ? parsedAmountInt : 0;
        if (amount > 0)
            YouTubeService.SimulateGiftMemberships(amount);
    }
}