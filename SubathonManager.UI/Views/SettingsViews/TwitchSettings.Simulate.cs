using System.Windows;
using SubathonManager.Integration;
namespace SubathonManager.UI.Views.SettingsViews;

public partial class TwitchSettings
{
    
    private void TestTwitchCharityDonation_Click(object sender, RoutedEventArgs e)
    {
        var value = SimulateTwitchCharAmt.Text;
        var currency = CurrencyBox.Text;
        TwitchService.SimulateCharityDonation(value, currency);
    }

    private void TestTwitchHypeTrain_Click(object sender, RoutedEventArgs e)
    {
        string selectedEvent = (HypeTrainTestSelection.SelectedItem is System.Windows.Controls.ComboBoxItem item) 
            ? item.Content?.ToString() ?? "" 
            : "";
        var level = HypeTrainLevel.Text;
        switch (selectedEvent)
        {
            case "Start":
                TwitchService.SimulateHypeTrainStart();
                break;
            case "End":
                TwitchService.SimulateHypeTrainEnd(string.IsNullOrWhiteSpace(level) ? 7 : int.Parse(level));
                break;
            case "Progress":
                TwitchService.SimulateHypeTrainProgress(string.IsNullOrWhiteSpace(level) ? 3 : int.Parse(level));
                break;
        }
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