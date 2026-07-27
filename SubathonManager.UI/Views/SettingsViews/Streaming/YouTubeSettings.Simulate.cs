using Avalonia.Controls;
using Avalonia.Interactivity;
using SubathonManager.Integration;

namespace SubathonManager.UI.Views.SettingsViews.Streaming;

public partial class YouTubeSettings
{
    private void TestYTSuperChat_Click(object? sender, RoutedEventArgs e)
    {
        YouTubeService.SimulateSuperChat(SimulateSCAmt.Text ?? "", CurrencyBox.Text ?? "");
    }

    private void TestYTMembership_Click(object? sender, RoutedEventArgs e)
    {
        string selectedTier = (SimTierSelection.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "DEFAULT";
        YouTubeService.SimulateMembership(selectedTier);
    }

    private void TestYTRaid_Click(object? sender, RoutedEventArgs e)
    {
        YouTubeService.SimulateRaid();
    }

    private void TestYTGiftMembership_Click(object? sender, RoutedEventArgs e)
    {
        int amount = int.TryParse(SimGiftMembershipAmtInput.Text, out var parsedAmountInt) ? parsedAmountInt : 0;
        if (amount > 0)
            YouTubeService.SimulateGiftMemberships(amount);
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected)
    {
        CurrencyBox.ItemsSource = currencies;
        CurrencyBox.SelectedItem = selected;
    }
}
