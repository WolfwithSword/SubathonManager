using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Objects;

namespace SubathonManager.UI
{
    public partial class MainWindow
    {
        private static readonly Brush StatusUpBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
        private static readonly Brush StatusDownBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x4A, 0x4A));
        private static readonly Brush StatusMixedBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D));
        private static readonly Brush StatusNoneBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));

        private void InitConnectionStatus()
        {
            IntegrationEvents.ConnectionUpdated += OnConnectionStatusChanged;
            Closed += (_, _) => IntegrationEvents.ConnectionUpdated -= OnConnectionStatusChanged;
            UpdateConnectionStatusDot();
        }

        private void OnConnectionStatusChanged(IntegrationConnection _)
            => Dispatcher.BeginInvoke(UpdateConnectionStatusDot);

        private void UpdateConnectionStatusDot()
        {
            var counted = Utils.GetAllConnections().Where(c => !IsAggregateExempt(c)).ToList();
            ConnectionStatusDot.Fill = GetAggregateBrush(counted);
            int up = counted.Count(c => c.Status);
            ConnectionStatusBtn.ToolTip = counted.Count == 0
                ? "Connection Status"
                : $"Connection Status: {up}/{counted.Count} connected";
        }

        private static bool IsAggregateExempt(IntegrationConnection c) =>
            !c.Status && c is { Source: SubathonEventSource.KoFi, Service: "Socket" } 
                or { Source: SubathonEventSource.OBS, Service: "HelperScript" };

        private static Brush GetAggregateBrush(IEnumerable<IntegrationConnection> connections)
        {
            var counted = connections.Where(c => !IsAggregateExempt(c)).ToList();
            if (counted.Count == 0) return StatusNoneBrush;
            int up = counted.Count(c => c.Status);
            if (up == counted.Count) return StatusUpBrush;
            return up == 0 ? StatusDownBrush : StatusMixedBrush;
        }

        private void ConnectionStatusBtn_Click(object sender, RoutedEventArgs e)
        {
            var menu = BuildConnectionStatusMenu();
            menu.PlacementTarget = ConnectionStatusBtn;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private static ContextMenu BuildConnectionStatusMenu()
        {
            var menu = new ContextMenu();
            var connections = Utils.GetAllConnections().ToList();
            if (connections.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = "No integrations active", IsEnabled = false });
                return menu;
            }

            var bySource = connections
                .GroupBy(c => GetEffectiveSource(c.Source))
                .OrderBy(g => SubathonEventSourceHelper.GetSourceOrder(g.Key))
                .ToList();

            var byGroup = bySource
                .GroupBy(g => g.Key.GetGroup())
                .OrderBy(gg => gg.Min(s => SubathonEventSourceHelper.GetSourceOrder(s.Key)));

            foreach (var group in byGroup)
            {
                var groupItem = new MenuItem
                {
                    Header = group.First().Key.GetGroupLabel(),
                    Icon = MakeStatusDot(GetAggregateBrush(group.SelectMany(s => s))),
                    StaysOpenOnClick = true
                };
                foreach (var source in group)
                    groupItem.Items.Add(BuildSourceItem(source.Key, source.ToList()));
                menu.Items.Add(groupItem);
            }
            return menu;
        }

        private static MenuItem BuildSourceItem(SubathonEventSource source, List<IntegrationConnection> connections)
        {
            switch (source)
            {
                case SubathonEventSource.KoFi: return BuildKoFiItem(connections);
                case SubathonEventSource.PallyGG: return BuildPallyItem(connections);
                case SubathonEventSource.DevTunnels: return BuildDevTunnelsItem(connections);
                case SubathonEventSource.OBS: return BuildObsItem(connections);
            }

            var sourceGroup = source.GetGroup();
            if (sourceGroup is SubathonSourceGroup.StreamExtension or SubathonSourceGroup.ExternalSoftware
                || source is SubathonEventSource.FourthWall or SubathonEventSource.Throne)
            {
                bool up = connections.Any(c => c.Status);
                return MakeLeafItem(source.GetDescription(), up ? StatusUpBrush : StatusDownBrush, null);
            }

            var sourceItem = new MenuItem
            {
                Header = source.GetDescription(),
                Icon = MakeStatusDot(GetAggregateBrush(connections)),
                StaysOpenOnClick = true
            };
            var ordered = connections
                .OrderBy(c => c.Service == c.Source.ToString() ? 0 : 1)
                .ThenBy(c => c.Service, StringComparer.OrdinalIgnoreCase);
            foreach (var connection in ordered)
            {
                string label = connection.Service == connection.Source.ToString() ? "Account" : connection.Service;
                string detail = ShowableDetail(connection);
                if (detail.Length > 0) label += $":  {detail}";
                sourceItem.Items.Add(MakeLeafItem(label,
                    connection.Status ? StatusUpBrush : StatusDownBrush, null));
            }
            return sourceItem;
        }

        private static MenuItem BuildKoFiItem(List<IntegrationConnection> connections)
        {
            var tunnel = connections.FirstOrDefault(c => c.Source == SubathonEventSource.KoFiTunnel);
            var socket = connections.FirstOrDefault(c => c is { Source: SubathonEventSource.KoFi, Service: "Socket" });
            bool up = tunnel?.Status == true || socket?.Status == true;

            var item = new MenuItem
            {
                Header = SubathonEventSource.KoFi.GetDescription(),
                Icon = MakeStatusDot(up ? StatusUpBrush : StatusDownBrush),
                StaysOpenOnClick = true
            };
            bool tunnelUp = tunnel?.Status == true;
            item.Items.Add(MakeLeafItem("Webhook Tunnel",
                tunnelUp ? StatusUpBrush : StatusDownBrush, null));

            bool socketUp = socket?.Status == true;
            var socketItem = MakeLeafItem("Socket (Legacy)",
                socketUp ? StatusUpBrush : StatusNoneBrush, null);
            socketItem.IsEnabled = socketUp;
            item.Items.Add(socketItem);
            return item;
        }

        private static MenuItem BuildPallyItem(List<IntegrationConnection> connections)
        {
            var socket = connections.FirstOrDefault(c => c.Service == "Socket") ?? connections.First();
            var brush = socket.Status ? StatusUpBrush : StatusDownBrush;
            var item = new MenuItem
            {
                Header = SubathonEventSource.PallyGG.GetDescription(),
                Icon = MakeStatusDot(brush),
                StaysOpenOnClick = true
            };
            string room = string.IsNullOrWhiteSpace(socket.Name) ? "All" : socket.Name;
            item.Items.Add(MakeLeafItem($"Room:  {room}", brush, null));
            return item;
        }

        private static MenuItem BuildDevTunnelsItem(List<IntegrationConnection> connections)
        {
            var item = new MenuItem
            {
                Header = SubathonEventSource.DevTunnels.GetDescription(),
                Icon = MakeStatusDot(GetAggregateBrush(connections)),
                StaysOpenOnClick = true
            };

            var cli = connections.FirstOrDefault(c => c.Service == "Cli");
            var login = connections.FirstOrDefault(c => c.Service == "Login");
            var tunnel = connections.FirstOrDefault(c => c.Service == "Tunnel");

            if (cli != null)
            {
                string label = cli.Status && !string.IsNullOrWhiteSpace(cli.Name) ? $"CLI:  v{cli.Name}" : "CLI";
                item.Items.Add(MakeLeafItem(label,
                    cli.Status ? StatusUpBrush : StatusDownBrush,
                    cli.Status ? null : "Not Installed"));
            }
            if (login != null)
            {
                string label = login.Status && !string.IsNullOrWhiteSpace(login.Detail)
                    ? $"Login:  {login.Detail}"
                    : "Login";
                item.Items.Add(MakeLeafItem(label,
                    login.Status ? StatusUpBrush : StatusDownBrush, null));
            }
            if (tunnel != null)
                item.Items.Add(MakeLeafItem("Tunnel",
                    tunnel.Status ? StatusUpBrush : StatusDownBrush, null));
            return item;
        }

        private static MenuItem BuildObsItem(List<IntegrationConnection> connections)
        {
            var webSocket = connections.FirstOrDefault(c => c.Service == "OBS");
            var helper = connections.FirstOrDefault(c => c.Service == "HelperScript");

            var item = new MenuItem
            {
                Header = SubathonEventSource.OBS.GetDescription(),
                Icon = MakeStatusDot(GetAggregateBrush(connections)),
                StaysOpenOnClick = true
            };
            bool wsUp = webSocket?.Status == true;
            item.Items.Add(MakeLeafItem("WebSocket",
                wsUp ? StatusUpBrush : StatusDownBrush, null));

            bool helperUp = helper?.Status == true;
            string helperLabel = helperUp && !string.IsNullOrWhiteSpace(helper!.Detail)
                ? $"Helper Script:  {helper.Detail}"
                : "Helper Script";
            item.Items.Add(MakeLeafItem(helperLabel,
                helperUp ? StatusUpBrush : StatusNoneBrush,
                helperUp ? null : "Not detected. Add via OBS Tools -> Scripts"));
            return item;
        }
        
        private static string ShowableDetail(IntegrationConnection connection)
        {
            if (string.IsNullOrWhiteSpace(connection.Name)) return "";
            if (connection.Name.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return "";
            return Truncate(connection.Name, 36);
        }

        private static SubathonEventSource GetEffectiveSource(SubathonEventSource source)
        {
            var trueSource = EnumMetaCache.Get<EventSourceMetaAttribute>(source)?.TrueSource
                             ?? SubathonEventSource.Unknown;
            return trueSource == SubathonEventSource.Unknown ? source : trueSource;
        }

        private static MenuItem MakeLeafItem(string header, Brush brush, string? toolTip) => new()
        {
            Header = header,
            Icon = MakeStatusDot(brush),
            ToolTip = toolTip,
            StaysOpenOnClick = true
        };

        private static Ellipse MakeStatusDot(Brush brush) => new()
        {
            Width = 9,
            Height = 9,
            Fill = brush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        private static string Truncate(string value, int max)
            => value.Length <= max ? value : value[..(max - 1)] + "...";
    }
}
