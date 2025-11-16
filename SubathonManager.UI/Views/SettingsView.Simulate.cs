using System.Windows;
using SubathonManager.Integration;

namespace SubathonManager.UI.Views
{
    public partial class SettingsView
    {
        private void TestSETip_Click(object sender, RoutedEventArgs e)
        {
            var value = SimulateSETipAmountBox.Text;
            var currency = SimulateSECurrencyBox.Text;
            StreamElementsService.SimulateTip(value, currency);
        }
        private void TestYTSuperChat_Click(object sender, RoutedEventArgs e)
        {
            var value = SimulateSCAmt.Text;
            var currency = SimulateSCCurrencyBox.Text;
            YouTubeService.SimulateSuperChat(value, currency);
        }

        private void TestSLTip_Click(object sender, RoutedEventArgs e)
        {
            var value = SimulateSLTipAmountBox.Text;
            var currency = SimulateSLCurrencyBox.Text;
            StreamLabsService.SimulateTip(value, currency);
        }

        private void TestTwitchFollow_Click(object sender, RoutedEventArgs e)
        {
            TwitchService.SimulateFollow();
        }

        private void TestTwitchRaid_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(SimulateRaidAmt.Text, out var parsedAmount))
            {
                if (parsedAmount >= 0)
                {
                    TwitchService.SimulateRaid(parsedAmount);
                }
            }
        }

        private void TestTwitchSub_Click(object sender, RoutedEventArgs e)
        {
            string tier = "";
            string selectedTier = (SimSubTierSelection.SelectedItem is System.Windows.Controls.ComboBoxItem item) 
                ? item.Content?.ToString() ?? "" 
                : "";
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
        
        private void TestYTMembership_Click(object sender, RoutedEventArgs e)
        {
            YouTubeService.SimulateMembership();
        }

        private void TestYTGiftMembership_Click(object sender, RoutedEventArgs e)
        {
            int amount = int.TryParse(SimGiftMembershipAmtInput.Text, out var parsedAmountInt) ? parsedAmountInt : 0;
            if (amount > 0)
                YouTubeService.SimulateGiftMemberships(amount);
        }
        
        private void TestTwitchGiftSub_Click(object sender, RoutedEventArgs e)
        {
            string tier = "";
            string selectedTier = (SimGiftSubTierSelection.SelectedItem is System.Windows.Controls.ComboBoxItem item) 
                ? item.Content?.ToString() ?? "" 
                : "";
            int amount = int.TryParse(SimGiftSubAmtInput.Text, out var parsedAmountInt) ? parsedAmountInt : 0;
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

        private void TestTwitchCheer_Click(object sender, RoutedEventArgs e)
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
