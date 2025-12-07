using System.Windows;
using System.Windows.Input;
using System.IO;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;

namespace SubathonManager.UI
{
    public partial class MainWindow
    {
        private EditRouteWindow? _editWindow;
        
        private void LoadRoutes()
        {
            Overlays.Clear();
            using var db = _factory.CreateDbContext();
            var routes = db.Routes.OrderByDescending(r => r.UpdatedTimestamp).ToList();

            foreach (var route in routes)
                Overlays.Add(route);

            OverlaysList.ItemsSource = Overlays;
        }

        private void OpenPresets_Click(object sender, RoutedEventArgs e)
        {
            string path = Path.GetFullPath("./presets");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch
            {
                _logger?.LogWarning($"Unable to locate presets folder: {path}");
            }
        }

        private void SendRefreshRequest_Click(object sender, RoutedEventArgs e)
        {
            OverlayEvents.RaiseOverlayRefreshAllRequested();
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
            using var db = _factory.CreateDbContext();
            var newRoute = new Route
            {
                Name = $"New Overlay {Overlays.Count + 1}",
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
                using var db = _factory.CreateDbContext();
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
                using var db = _factory.CreateDbContext();
                var dbRoute = db.Routes.Include(r => r.Widgets)
                    .ThenInclude(w => w.CssVariables).Include(r => r.Widgets)
                    .ThenInclude(w => w.JsVariables).FirstOrDefault(r => r.Id == route.Id);

                if (dbRoute == null) return;
                var clone = new Route
                {
                    Name = $"{dbRoute.Name} (Copy)",
                    Width = dbRoute.Width,
                    Height = dbRoute.Height
                };
                
                db.Routes.Add(clone);
                db.SaveChanges();

                foreach (var widget in dbRoute.Widgets.ToArray())
                {
                    var cloneWidget = widget.Clone(clone.Id, widget.Name, widget.Z);
                    db.Widgets.Add(cloneWidget);
                    db.CssVariables.AddRange(cloneWidget.CssVariables);
                    db.JsVariables.AddRange(cloneWidget.JsVariables);
                    db.SaveChanges();
                }

                Overlays.Insert(0, clone);
            }
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
        
        private void OpenRouteEditor(Route route)
        {
            if (_editWindow != null)
            {
                if (_editWindow.EditorRouteId == route.Id)
                    return;
                _editWindow.Close();
            }
            
            _editWindow = new EditRouteWindow(route.Id)
            {
                Owner = this
            };
            _editWindow.Closed += (s, _) =>
            {
                LoadRoutes();
                _editWindow = null;
            };
            _editWindow.Show();
        }
    }
}
