using System.Windows;
using System.Diagnostics;
using System.Windows.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging; 
using SubathonManager.Core.Events;
using SubathonManager.Core;
using SubathonManager.Data;

namespace SubathonManager.UI.Views;

public partial class SettingsView
{
    private DateTime? _lastUpdatedTimerAt;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<SettingsView>>();
    public SettingsView()
    {
        _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        TwitchEvents.TwitchConnected += UpdateTwitchStatus;
        YouTubeEvents.YouTubeConnectionUpdated += UpdateYoutubeStatus;
        StreamElementsEvents.StreamElementsConnectionChanged += UpdateSEStatus;
        StreamLabsEvents.StreamLabsConnectionChanged += UpdateSLStatus;
        InitializeComponent();
        
        SEJWTTokenBox.Text = Config.Data["StreamElements"]["JWT"];
        SLTokenBox.Text = Config.Data["StreamLabs"]["SocketToken"];

        if (App.AppStreamElementsService != null)
            UpdateConnectionStatus(App.AppStreamElementsService.Connected, SEStatusText, ConnectSEBtn);
        if (App.AppStreamLabsService != null)
            UpdateConnectionStatus(App.AppStreamLabsService.Connected, SLStatusText, ConnectSLBtn);
        
        ServerPortTextBox.Text = Config.Data["Server"]["Port"];
        LoadValues();
        InitWebhookSettings();
        InitTwitchAutoSettings();
        InitCommandSettings();
        InitYoutubeSettings();
        
        SubathonEvents.SubathonDataUpdate += UpdateTimerValue;
        WebServerEvents.WebServerStatusChanged += UpdateServerStatus;
        UpdateServerStatus(App.AppWebServer?.Running ?? false);

        InitCurrencySelects();
    }

    private void GoToHelp_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/WolfwithSword/SubathonManager/wiki",
            UseShellExecute = true
        });
    }

    private async void Updater_Click(object sender, RoutedEventArgs e)
    {
        (bool hasUpdate, string? newVersion, string? url) = await AppServices.CheckForUpdate(_logger);
        if (hasUpdate && !string.IsNullOrEmpty(newVersion))
        {
            var msgBox = new Wpf.Ui.Controls.MessageBox();
            msgBox.Title = "Updater";
            
            var textBlock = new System.Windows.Controls.TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Width = 320
            };

            textBlock.Inlines.Add("Update available!");
            textBlock.Inlines.Add(newVersion);

            if (!string.IsNullOrEmpty(url))
            {
                var link = new Hyperlink(new Run("Latest Version"))
                {
                    NavigateUri = new Uri(url)
                };

                link.RequestNavigate += (_, ea) =>
                {
                    Process.Start(new ProcessStartInfo(ea.Uri.AbsoluteUri)
                    {
                        UseShellExecute = true
                    });
                    ea.Handled = true;
                };

                textBlock.Inlines.Add(link);
            }

            textBlock.Inlines.Add("Download and install now?");

            msgBox.Content = textBlock;
            msgBox.CloseButtonText = "Cancel";
            msgBox.Owner = Application.Current.Windows
                .OfType<Window>()
                .FirstOrDefault(w => w.IsActive);
            msgBox.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            msgBox.PrimaryButtonText = "Update";
            var result = await msgBox.ShowDialogAsync();
            bool confirm = result == Wpf.Ui.Controls.MessageBoxResult.Primary;
            if (!confirm) return;
            
            await AppServices.DownloadAndInstall(_logger);
        }
        else
        {
            await Dispatcher.InvokeAsync(() => 
                { 
                    UpdateBtn.Content = "No Updates Found";
                } 
            );
            await Task.Delay(5000);
            await Dispatcher.InvokeAsync(() => 
                { 
                    UpdateBtn.Content = "Check for Updates";
                } 
            );
            
        }
    }
}