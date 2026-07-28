using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Interfaces;

namespace SubathonManager.UI;

public partial class MainWindow
{
    private async Task MaybeShowTelemetryPromptAsync()
    {
        try
        {
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            var installId = config.Get("Telemetry", "InstallId", "");
            if (!string.IsNullOrWhiteSpace(installId)) return;

            var panel = new StackPanel { Orientation = Orientation.Vertical, Width = 340 };
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 4, 4, 12),
                Text = "Would you like to send anonymous usage data to help guide development?\n\n" +
                       "Only information on which integrations are active is collected - no usernames, keys, or personal information of any kind."
            });

            var checkBox = new CheckBox
            {
                Content = "Enable anonymous data collection",
                IsChecked = true,
                Margin = new Thickness(4, 0, 4, 4)
            };
            panel.Children.Add(checkBox);

            var dialog = new FAContentDialog
            {
                Title = "Help Improve Subathon Manager",
                PrimaryButtonText = "Confirm",
                CloseButtonText = "No Thanks",
                Content = panel
            };

            var result = await dialog.ShowAsync();
            bool enabled = result == FAContentDialogResult.Primary && (checkBox.IsChecked ?? false);

            config.SetBool("Telemetry", "Enabled", enabled);
            config.Set("Telemetry", "InstallId", Guid.NewGuid().ToString());
            config.Save();
        }
        catch { /**/ }
    }
}
