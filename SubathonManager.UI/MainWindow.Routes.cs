using System.Windows;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Models;
using SubathonManager.Data;

namespace SubathonManager.UI
{
    public partial class MainWindow
    {
        private void LoadRoutes()
        {
            Overlays.Clear();
            using var db = _factory.CreateDbContext();
            var routes = db.Routes.OrderByDescending(r => r.UpdatedTimestamp).ToList();

            foreach (var route in routes)
                Overlays.Add(route);

            OverlaysList.ItemsSource = Overlays;
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
            var editor = new EditRouteWindow(route.Id)
            {
                Owner = this
            };
            editor.Closed += (s, _) => LoadRoutes();
            editor.ShowDialog();
        }
    }
}
