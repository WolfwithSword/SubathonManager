using System.Windows;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging; 
using SubathonManager.Core.Events;
using SubathonManager.Core;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.UI.Services;

namespace SubathonManager.UI.Views;

public partial class SettingsView : SettingsControl
{
    private DateTime? _lastUpdatedTimerAt;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<SettingsView>>();
    

    public SettingsView()
    {
        _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        InitializeComponent();     
        
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        
        SubathonEvents.SubathonDataUpdate += UpdateTimerValue; // needed outside of loaded to actually capture first fire
        Loaded += (_, _) =>
        {
            //SubathonEvents.SubathonDataUpdate += UpdateTimerValue;
            WebServerEvents.WebServerStatusChanged += UpdateServerStatus;
            UpdateServerStatus(ServiceManager.Server?.Running ?? false);
            SubathonEvents.SubathonValueConfigUpdatedRemote += RefreshSubathonValues;
            SettingsEvents.SettingsUnsavedChanges += UpdateSaveButtonBorder;
            RegisterUnsavedChangeHandlers();
        };

        StreamingSettingsControl.Init(this);
        WebhookLogSettingsControl.Init(this);
        ExternalServiceSettingsControl.Init(this);
        CommandsSettingsControl.Init(this);
        ExtensionSettingsControl.Init(this);
        
        ServerPortTextBox.Text = config.Get("Server", "Port", string.Empty) ?? string.Empty;
        LoadValues();
        InitCurrencySelects();
        
        Unloaded += (_, _) =>
        {
            //SubathonEvents.SubathonDataUpdate -= UpdateTimerValue;
            WebServerEvents.WebServerStatusChanged -= UpdateServerStatus;
            SubathonEvents.SubathonValueConfigUpdatedRemote -= RefreshSubathonValues;
        };
        
        Task.Run(CheckForUpdateOnBoot);
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
            FileName = "https://docs.subathonmanager.app",
            UseShellExecute = true
        });
    }

    private async void Updater_Click(object sender, RoutedEventArgs e)
    {
        (bool hasUpdate, string? newVersion, string? url) = await AppServices.CheckForUpdate(_logger);
        if (hasUpdate && !string.IsNullOrEmpty(newVersion))
        {
            var msgBox = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Updater"
            };

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

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        return;
    }

    public override bool UpdateValueSettings(AppDbContext db)
    {
        return false;
    }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val)
    {
        throw new NotImplementedException();
    }
    
    private async void ShowTelemetryPromptAsync(object sender, RoutedEventArgs routedEventArgs)
        {
            try
            {
                var config = AppServices.Provider.GetRequiredService<IConfig>();

                var msgBox = new Wpf.Ui.Controls.MessageBox
                {
                    Title = "Help Improve Subathon Manager",
                    PrimaryButtonText = "Confirm",
                    Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                var panel = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Width = 340
                };

                var textBlock = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 4, 4, 12),
                };
                textBlock.Inlines.Add(new Run("Would you like to send anonymous usage data to help guide development?"));
                textBlock.Inlines.Add(new LineBreak());
                textBlock.Inlines.Add(new LineBreak());
                textBlock.Inlines.Add(new Run("Only information on which integrations are active is collected - no usernames, keys, or personal information of any kind."));


                var checkBox = new CheckBox
                {
                    Content = "Enable anonymous data collection",
                    IsChecked = config.GetBool("Telemetry", "Enabled", false),
                    Margin = new Thickness(4, 0, 4, 4)
                };

                panel.Children.Add(textBlock);
                panel.Children.Add(checkBox);
                msgBox.Content = panel;

                var result = await msgBox.ShowDialogAsync();
                if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
                bool enabled = (checkBox.IsChecked ?? false);
                if (config.SetBool("Telemetry", "Enabled", enabled))
                    config.Save();
            }
            catch (Exception ex)
            {
                /****/
            }
        }
}