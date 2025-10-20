using System.Windows;
using SubathonManager.Integration;

namespace SubathonManager.UI.Views
{
    public partial class SettingsView
    {
        private async void TestSETip_Click(object sender, RoutedEventArgs e)
        {
            var value = SimulateSETipAmountBox.Text;
            StreamElementsService.SimulateTip(value);
        }
        
        private async void TestTwitchFollow_Click(object sender, RoutedEventArgs e)
        {
            TwitchService.SimulateFollow();
        }

        private async void TestTwitchRaid_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(SimulateRaidAmt.Text, out var parsedAmount))
            {
                if (parsedAmount >= 0)
                {
                    TwitchService.SimulateRaid(parsedAmount);
                }
            }
        }

        private async void TestTwitchSub_Click(object sender, RoutedEventArgs e)
        {
            string tier = "";
            string selectedTier =
                (SimSubTierSelection.SelectedItem as System.Windows.Controls.ComboBoxItem).Content?.ToString() ?? "";
            switch (selectedTier)
            {
                case "Tier 1":
                    tier = "1000";
                    break;
                case "Tier 2":
                    tier = "2000";
                    break;
                case "Tier 3":
                    tier = "3000";
                    break;
            }
            if (!string.IsNullOrEmpty(tier))
                TwitchService.SimulateSubscription(tier);
        }

        private async void TestTwitchGiftSub_Click(object sender, RoutedEventArgs e)
        {
            string tier = "";
            int amount = 0;
            
            string selectedTier =
                (SimGiftSubTierSelection.SelectedItem as System.Windows.Controls.ComboBoxItem).Content?.ToString() ?? "";
            amount = int.TryParse(SimGiftSubAmtInput.Text, out var parsedAmountInt) ? parsedAmountInt : 0;
            switch (selectedTier)
            {
                case "Tier 1":
                    tier = "1000";
                    break;
                case "Tier 2":
                    tier = "2000";
                    break;
                case "Tier 3":
                    tier = "3000";
                    break;
            }
            if (!string.IsNullOrEmpty(tier) && amount > 0)
                TwitchService.SimulateGiftSubscriptions(tier, amount);
        }

        private async void TestTwitchCheer_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(SimulateCheerAmt.Text, out var parsedAmount))
            {
                if (parsedAmount >= 0)
                {
                    TwitchService.SimulateCheer(parsedAmount);
                }
            }
        }
    }
}
