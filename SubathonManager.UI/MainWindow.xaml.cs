using System.Windows;
using System.Windows.Input;
using System.Collections.ObjectModel;
using Microsoft.EntityFrameworkCore;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using SubathonManager.Core.Models;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Data;
using SubathonManager.Twitch;

// TODO: split up? same for xaml?

namespace SubathonManager.UI
{
    public partial class MainWindow : FluentWindow
    {
        private Route? _selectedRoute;
        private TwitchService _twitchService = App._twitchService;
        public ObservableCollection<Route> Overlays { get; set; } = new();
        
        public MainWindow()
        {
            InitializeComponent();
            if (_twitchService == null)
            {
                _twitchService = App._twitchService;
            }

            ApplicationThemeManager.Apply(this);

            Loaded += MainWindow_Loaded;
            SaveSettingsButton.Click += SaveSettingsButton_Click;
            TitleBar.Title = $"Subathon Manager - {App.AppVersion}";
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadRoutes();
            ServerPortTextBox.Text = Config.Data["Server"]["Port"].ToString();
            
            using var db = new AppDbContext();

            // one minor possible issue
            // TODO UI will show these values even if not saved, so maybe have a textblock *after* each for "Current Value"?
            // reduce confusion for which values are active
            // also TODO: Add a "simulate" button next to each, which will simulate textbox value and type Simulated Event

            var values = db.SubathonValues.ToList();
            foreach (var val in values)
            {
                var v = $"{val.Seconds}";
                var p = $"{val.Points}";
                switch (val.EventType) 
                { 
                    case SubathonEventType.TwitchFollow:
                        FollowTextBox.Text = v;
                        Follow2TextBox.Text = p;
                        break;
                    case SubathonEventType.TwitchCheer:
                        CheerTextBox.Text = $"{Math.Round(val.Seconds * 100)}";
                        Cheer2TextBox.Text =  $"{val.Points}"; // in backend when adding, need to round down when adding for odd bits
                        break;
                    case SubathonEventType.TwitchSub:
                        switch (val.Meta)
                        {
                            case "1000":
                                SubT1TextBox.Text = v;
                                SubT1TextBox2.Text = p;
                                break;
                            case "2000":
                                SubT2TextBox.Text = v;
                                SubT2TextBox2.Text = p;
                                break;
                            case "3000":
                                SubT3TextBox.Text = v;
                                SubT3TextBox2.Text = p;
                                break;
                        }

                        break;
                    case SubathonEventType.TwitchGiftSub:
                        switch (val.Meta)
                        {
                            case "1000":
                                GiftSubT1TextBox.Text = v;
                                GiftSubT1TextBox2.Text = p;
                                break;
                            case "2000":
                                GiftSubT2TextBox.Text = v;
                                GiftSubT2TextBox2.Text = p;
                                break;
                            case "3000":
                                GiftSubT3TextBox.Text = v;
                                GiftSubT3TextBox2.Text = p;
                                break;
                        }
                        break;
                    case SubathonEventType.TwitchRaid:
                        RaidTextBox.Text = v;
                        Raid2TextBox.Text = p;
                        break;
                }
            }
        }

        private void LoadRoutes()
        {
            Overlays.Clear();
            using var db = new AppDbContext();
            var routes = db.Routes.OrderByDescending(r => r.UpdatedTimestamp).ToList();

            foreach (var route in routes)
                Overlays.Add(route);

            OverlaysList.ItemsSource = Overlays;
        }

        private void NumberOnly_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            e.Handled = !int.TryParse(e.Text, out _);
        }

        private void CopyRouteUrl_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Wpf.Ui.Controls.Button btn && btn.DataContext != null)
            {
                dynamic item = btn.DataContext;
                Route route = item;
                Clipboard.SetText(route.GetRouteUrl());
            }
        }

        private void AddRoute_Click(object sender, RoutedEventArgs e)
        {
            using var db = new AppDbContext();
            var newRoute = new Route
            {
                Name = "New Overlay",
                Width = 1920,
                Height = 1080
            };
            db.Routes.Add(newRoute);
            db.SaveChanges();

            Overlays.Insert(0, newRoute);
        }

        private void DeleteRoute_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Wpf.Ui.Controls.Button btn && btn.DataContext is Route route)
            {
                using var db = new AppDbContext();
                var found = db.Routes.FirstOrDefault(r => r.Id == route.Id);
                if (found != null)
                {
                    db.Routes.Remove(found);
                    db.SaveChanges();
                }

                Overlays.Remove(route);
            }
        }

        private void DuplicateRoute_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Wpf.Ui.Controls.Button btn && btn.DataContext is Route route)
            {
                using var db = new AppDbContext();
                var dbRoute = db.Routes.Include(r => r.Widgets)
                    .ThenInclude(w => w.CssVariables).FirstOrDefault(r => r.Id == route.Id);

                if (dbRoute == null) return;
                var clone = new Route
                {
                    Name = $"{dbRoute.Name} (Copy)",
                    Width = dbRoute.Width,
                    Height = dbRoute.Height
                };

                List<CssVariable> cloneCssVars = new();

                db.Routes.Add(clone);
                db.SaveChanges();

                foreach (var widget in dbRoute.Widgets.ToArray())
                {
                    var cloneWidget = new Widget(widget.Name, widget.HtmlPath);
                    cloneWidget.Width = widget.Width;
                    cloneWidget.Height = widget.Height;
                    cloneWidget.RouteId = clone.Id;
                    cloneWidget.X = widget.X;
                    cloneWidget.Y = widget.Y;
                    cloneWidget.Z = widget.Z;
                    db.Widgets.Add(cloneWidget);

                    foreach (var cssVar in widget.CssVariables)
                    {
                        var cloneCssVariable = new CssVariable
                        {
                            Name = cssVar.Name,
                            Value = cssVar.Value,
                            WidgetId = cloneWidget.Id
                        };
                        cloneCssVars.Add(cloneCssVariable);
                    }
                }

                db.SaveChanges();
                db.CssVariables.AddRange(cloneCssVars);
                db.SaveChanges();

                Overlays.Insert(0, clone);
            }
        }

        private void OpenRouteEditor(Route route)
        {
            var editor = new EditRouteWindow(route.Id)
            {
                Owner = this
            };
            editor.Closed += (s, _) => LoadRoutes();
            editor.ShowDialog();
        }

        private void RouteCard_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Route route)
            {
                OpenRouteEditor(route);
            }
        }

        private void EditRoute_Click(object sender, RoutedEventArgs e)
        {
            // open editor window for overlay/route
            // note: All edits in moving elements save LIVE. Widget copy/delete is LIVE.
            // Overlay name/size requires save button. Widget editing requires save button.
            if (sender is Wpf.Ui.Controls.Button btn && btn.DataContext is Route route)
            {
                OpenRouteEditor(route);
            }
        }


        private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(ServerPortTextBox.Text, out var port))
            {
                Config.Data["Server"]["Port"] = port.ToString();
                Config.Save();
            }
        }


        private async void ConnectTwitchButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Make button say "Reconnect" when it is already connected
            // TODO: Smartly stop everything inside before initialize, i think i do rn?
            try
            {
                try
                {
                    var cts = new CancellationTokenSource(5000);
                    await _twitchService.StopAsync(cts.Token);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex); // maybe?
                }

                await _twitchService.InitializeAsync();
                Console.WriteLine("Twitch connection established");
            }
            catch
            {
                Console.WriteLine("Failed to initialize twitch service.");
            }

        }
        
        private void SaveAllSubathonValuesButton_Click(object sender, RoutedEventArgs e)
        {
            
            using var db = new AppDbContext();

            // Twitch values
            var cheerValue =
                db.SubathonValues.FirstOrDefault(sv => sv.EventType == SubathonEventType.TwitchCheer && sv.Meta == "");
            // divide by 100 since UI shows "per 100 bits"
            if (cheerValue != null && double.TryParse(CheerTextBox.Text, out var cheerSeconds))
                cheerValue.Seconds = cheerSeconds / 100.0;
            if (cheerValue != null && int.TryParse(Cheer2TextBox.Text, out var cheerPoints))
                cheerValue.Points = cheerPoints;

            var raidValue =
                db.SubathonValues.FirstOrDefault(sv => sv.EventType == SubathonEventType.TwitchRaid && sv.Meta == "");
            if (raidValue != null && double.TryParse(RaidTextBox.Text, out var raidSeconds))
                raidValue.Seconds = raidSeconds;
            if (raidValue != null && int.TryParse(Raid2TextBox.Text, out var raidPoints))
                raidValue.Points = raidPoints;

            var followValue =
                db.SubathonValues.FirstOrDefault(sv => sv.EventType == SubathonEventType.TwitchFollow && sv.Meta == "");
            if (followValue != null && double.TryParse(FollowTextBox.Text, out var followSeconds))
                followValue.Seconds = followSeconds;
            if (followValue != null && int.TryParse(Follow2TextBox.Text, out var followPoints))
                followValue.Points = followPoints;

            void SaveSubTier(SubathonEventType type, string meta, Wpf.Ui.Controls.TextBox tb,
                Wpf.Ui.Controls.TextBox tb2)
            {
                var val = db.SubathonValues.FirstOrDefault(sv => sv.EventType == type && sv.Meta == meta);
                if (val != null && double.TryParse(tb.Text, out var seconds))
                    val.Seconds = seconds;
                if (val != null && int.TryParse(tb2.Text, out var points))
                    val.Points = points;

            }

            SaveSubTier(SubathonEventType.TwitchSub, "1000", SubT1TextBox, SubT1TextBox2);
            SaveSubTier(SubathonEventType.TwitchSub, "2000", SubT2TextBox,SubT2TextBox2);
            SaveSubTier(SubathonEventType.TwitchSub, "3000", SubT3TextBox, SubT3TextBox2);
            SaveSubTier(SubathonEventType.TwitchGiftSub, "1000", GiftSubT1TextBox, GiftSubT1TextBox2);
            SaveSubTier(SubathonEventType.TwitchGiftSub, "2000", GiftSubT2TextBox, GiftSubT2TextBox2);
            SaveSubTier(SubathonEventType.TwitchGiftSub, "3000", GiftSubT3TextBox, GiftSubT3TextBox2);

            db.SaveChanges();
        }
    }
}
