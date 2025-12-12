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
        InitializeComponent();
        
        TwitchSettingsControl.Init(this);
        YouTubeSettingsControl.Init(this);
        WebhookLogSettingsControl.Init(this);
        StreamLabsSettingsControl.Init(this);
        StreamElementsSettingsControl.Init(this);
        KoFiSettingsControl.Init(this);
        ExternalSettingsControl.Init(this);
        CommandsSettingsControl.Init(this);
        
        ServerPortTextBox.Text = App.AppConfig!.Get("Server", "Port", string.Empty)!;
        LoadValues();
        InitCurrencySelects();
        
        SubathonEvents.SubathonDataUpdate += UpdateTimerValue;
        WebServerEvents.WebServerStatusChanged += UpdateServerStatus;
        UpdateServerStatus(App.AppWebServer?.Running ?? false);
        Task.Run(() => CheckForUpdateOnBoot());

    }

    private async void CheckForUpdateOnBoot()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            _logger?.LogDebug("Checking for updates on boot...");
            (bool hasUpdate, string? newVersion, string? url) = await AppServices.CheckForUpdate(_logger);
            if (hasUpdate && !string.IsNullOrEmpty(newVersion))
            {
                await Dispatcher.InvokeAsync(() => { UpdateBtn.Content = "Update Available!"; }
                );
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error checking for updates on boot");
        }
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
            textBlock.Inlines.Add(new LineBreak());
            textBlock.Inlines.Add("v"+newVersion);
            textBlock.Inlines.Add(new LineBreak());

            if (!string.IsNullOrEmpty(url))
            {
                var link = new Hyperlink(new Run("Latest Version"))
                {
                    NavigateUri = new Uri(url.Replace("/"+newVersion, "/v"+newVersion))
                };

                link.RequestNavigate += (_, ea) =>
                {
                    Process.Start(new ProcessStartInfo(ea.Uri.AbsoluteUri)
                    {
                        UseShellExecute = true
                    });
                    ea.Handled = true;
                };

                textBlock.Inlines.Add(new LineBreak());
                textBlock.Inlines.Add(link);
            }

            textBlock.Inlines.Add(new LineBreak());
            textBlock.Inlines.Add(new LineBreak());
            textBlock.Inlines.Add("Download and install now?");
            textBlock.Inlines.Add(new LineBreak());
            textBlock.Inlines.Add("You will need to start the app manually once finished.");

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