using Avalonia.Controls;
using Avalonia.Interactivity;
using SubathonManager.Integration;

namespace SubathonManager.UI.Views.SettingsViews.Streaming;

public partial class TwitchSettings
{
    private void TestTwitchCharityDonation_Click(object? sender, RoutedEventArgs e)
    {
        TwitchService.SimulateCharityDonation(SimulateTwitchCharAmt.Text ?? "", CurrencyBox.Text ?? "");
    }

    private void TestTwitchHypeTrain_Click(object? sender, RoutedEventArgs e)
    {
        string selectedEvent = (HypeTrainTestSelection.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
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

    private void TestTwitchFollow_Click(object? sender, RoutedEventArgs e)
    {
        TwitchService.SimulateFollow();
    }

    private void TestTwitchRaid_Click(object? sender, RoutedEventArgs e)
    {
        if (int.TryParse(SimulateRaidAmt.Text, out var parsedAmount) && parsedAmount >= 0)
            TwitchService.SimulateRaid(parsedAmount);
    }

    private void TestTwitchSub_Click(object? sender, RoutedEventArgs e)
    {
        string selectedTier = (SimSubTierSelection.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        string tier = selectedTier switch
        {
            "Tier 1" => "1000",
            "Tier 2" => "2000",
            "Tier 3" => "3000",
            _ => ""
        };
        if (!string.IsNullOrEmpty(tier))
            TwitchService.SimulateSubscription(tier);
    }

    private void TestTwitchGiftSub_Click(object? sender, RoutedEventArgs e)
    {
        string selectedTier = (SimGiftSubTierSelection.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        int amount = int.TryParse(SimGiftSubAmtInput.Text, out var parsedAmountInt) ? parsedAmountInt : 0;
        string tier = selectedTier switch
        {
            "Tier 1" => "1000",
            "Tier 2" => "2000",
            "Tier 3" => "3000",
            _ => ""
        };
        if (!string.IsNullOrEmpty(tier) && amount > 0)
            TwitchService.SimulateGiftSubscriptions(tier, amount);
    }

    private void TestTwitchCheer_Click(object? sender, RoutedEventArgs e)
    {
        if (int.TryParse(SimulateCheerAmt.Text, out var parsedAmount) && parsedAmount >= 0)
            TwitchService.SimulateCheer(parsedAmount);
    }
}
