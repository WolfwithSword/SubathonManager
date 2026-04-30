using System.Windows;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using Wpf.Ui.Appearance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging; 
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Models;
using SubathonManager.Core.Events;
using SubathonManager.Data;
using SubathonManager.Core;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Objects;
using SubathonManager.UI.Services;
using SubathonManager.UI.Views;

namespace SubathonManager.UI
{
    public partial class MainWindow
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        
        private DateTime? _lastUpdatedTimerAt;
        private ObservableCollection<Route> Overlays { get; set; } = new();
        private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<MainWindow>>();

        public MainWindow()
        {
            _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            InitializeComponent();
            ApplicationThemeManager.Apply(this);

            Loaded += MainWindow_Loaded;
            TitleBar.Title = $"Subathon Manager - {ServiceManager.AppVersion}";

            SubathonEvents.SubathonDataUpdate += UpdateTimerValue;
            SubathonEvents.SubathonDataUpdate += UpdateMultiplierUi;
            Task.Run(App.InitSubathonTimer);
            InitObsIntegration();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        }

        private IntPtr WndProc(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (msg == Utils.SingleInstanceHelper.WM_SHOWAPP)
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
                Topmost = true;
                Topmost = false;
                Focus();

                handled = true;
            }

            if (msg == Utils.SingleInstanceHelper.WM_COPYDATA)
            {
                var cds = Marshal.PtrToStructure<Utils.SingleInstanceHelper.COPYDATASTRUCT>(lParam);
                var data = Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2);

                if (!string.IsNullOrWhiteSpace(data))
                {      
                    if (data.StartsWith("subathonmanager://oauth/", StringComparison.OrdinalIgnoreCase))
                    {
                        var uri = new Uri(data);
                        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                        Utils.PendingOAuthCallback = new OAuthCallback
                        {
                            Provider = uri.AbsolutePath.TrimStart('/'),
                            Code = query["code"] ?? "",
                            AccessToken = query["access_token"] ?? "",
                            RefreshToken = query["refresh_token"] ?? "",
                            Error = query["error"] ?? ""
                        };
                        handled = true;
                        return IntPtr.Zero;
                    }
                    if ((File.Exists(data) || data.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
                    {
                        Dispatcher.Invoke(async () =>
                        {
                            MainWindowTabs.SelectedItem = OverlayTabItem;
                            await ImportRouteFromFile(data);
                        });
                    }
                    handled = true;
                }
            }

            return IntPtr.Zero;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadRoutes();
            await Task.Run(() =>
            {
                Task.Delay(500);
                OverlayEvents.RaiseOverlayRefreshAllRequested();
            });
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            var currencies = ServiceManager.Events.ValidEventCurrencies().OrderBy(x => x).ToList();
            AdjustCurrencyBox.ItemsSource = currencies;
            AdjustCurrencyBox.SelectedItem = config.Get("Currency", "Primary", "USD")?
                .Trim().ToUpperInvariant() ?? "USD";
            
            await ShowTelemetryPromptAsync();
            
            if (!string.IsNullOrWhiteSpace(Utils.PendingOverlayImportPath))
            {
                MainWindowTabs.SelectedItem = OverlayTabItem;
                await Task.Delay(500); 
                await ImportRouteFromFile(Utils.PendingOverlayImportPath);
                Utils.PendingOverlayImportPath = null;
            }
        }
        
        private void ExportRoute_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not Route route) return;

            var dialog = new ExportOverlayDialog(route)
            {
                Owner = Application.Current.Windows
                    .OfType<Window>()
                    .FirstOrDefault(w => w.IsActive),
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            dialog.ShowDialog();
        }
        
        private async void ImportRoute_Click(object sender, RoutedEventArgs e)
        {
            var openDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import Overlay",
                Filter = "Subathon Manager Overlay (*.smo)|*.smo",
                DefaultExt = "smo"
            };
 
            if (openDialog.ShowDialog() != true) return;
 
            await ImportRouteFromFile(openDialog.FileName);
        }

        private async Task<bool> ImportRouteFromFile(string filePath)
        {
            if (filePath.StartsWith("http"))
            {
                using var client = new HttpClient();
                using var response = await client.GetAsync(filePath);
                response.EnsureSuccessStatusCode();
        
                string fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                                  ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                                  ?? Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(new Uri(filePath).AbsolutePath));
        
                if (string.IsNullOrWhiteSpace(fileName)) fileName = "imported_overlay";
                if (!fileName.EndsWith(".smo", StringComparison.OrdinalIgnoreCase)) fileName += ".smo";

                string tempFile = Path.Combine(Path.GetTempPath(), fileName);
                await File.WriteAllBytesAsync(tempFile, await response.Content.ReadAsByteArrayAsync());
                filePath = tempFile;
            }
            
            string importsDir = Path.GetFullPath($"./imports");
            try
            {
                var result = await OverlayPorter.ImportRouteAsync(filePath, importsDir, _factory);
 
                if (result.Failed)
                {
                    _logger?.LogError("Import failed: {Reason}", result.FailReason);
                    return false;
                }
 
                if (!result.HasAnythingNew)
                {
                    _logger?.LogInformation("Import: everything already exists, nothing to add");
                    return false;
                }
 
                await using var db = await _factory.CreateDbContextAsync();
 
                if (result is { RouteIsNew: true, Route: not null })
                    db.Routes.Add(result.Route);
 
                if (result.NewWidgets.Count > 0)
                    db.Widgets.AddRange(result.NewWidgets);
 
                if (result.NewCssVariables.Count > 0)
                    db.CssVariables.AddRange(result.NewCssVariables);
 
                if (result.NewJsVariables.Count > 0)
                    db.JsVariables.AddRange(result.NewJsVariables);
 
                await db.SaveChangesAsync();
                await Dispatcher.InvokeAsync(LoadRoutes);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Import of overlay failed");
                return false;
            }
        }
        
        private async Task ShowTelemetryPromptAsync()
        {
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            var installId = config.Get("Telemetry", "InstallId", "");
            if (!string.IsNullOrWhiteSpace(installId)) return;

            var msgBox = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Help Improve Subathon Manager",
                PrimaryButtonText = "Confirm",
                CloseButtonText = "No Thanks",
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
                IsChecked = true,
                Margin = new Thickness(4, 0, 4, 4)
            };

            panel.Children.Add(textBlock);
            panel.Children.Add(checkBox);
            msgBox.Content = panel;

            var result = await msgBox.ShowDialogAsync();
            bool enabled = result == Wpf.Ui.Controls.MessageBoxResult.Primary && (checkBox.IsChecked ?? false);
            if (config.SetBool("Telemetry", "Enabled", enabled))
                config.Save();
            if (!enabled && string.IsNullOrWhiteSpace(installId))
            {
                // if enabled, the service will create the id itself
                config.Set("Telemetry", "InstallId", Guid.NewGuid().ToString());
                config.SetBool("Telemetry", "Enabled", enabled);
                config.Save();
            }
        }
    }
}
