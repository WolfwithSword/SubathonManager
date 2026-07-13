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

        private ContextMenu? _statusMenu;

        private void OnConnectionStatusChanged(IntegrationConnection _)
            => Dispatcher.BeginInvoke(() =>
            {
                UpdateConnectionStatusDot();
                RefreshOpenConnectionStatusMenu();
            });

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
            !c.Status && (!c.Configured
                || c is { Source: SubathonEventSource.KoFi, Service: "Socket" }
                    or { Source: SubathonEventSource.OBS, Service: "HelperScript" });

        private static Brush LeafBrush(IntegrationConnection? c) =>
            c is not { Status: true }
                ? c is { Configured: true } ? StatusDownBrush : StatusNoneBrush
                : StatusUpBrush;

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
            var menu = new ContextMenu
            {
                PlacementTarget = ConnectionStatusBtn,
                Placement = PlacementMode.Bottom
            };
            FillConnectionStatusMenu(menu);
            menu.Closed += (_, _) =>
            {
                if (ReferenceEquals(_statusMenu, menu)) _statusMenu = null;
            };
            _statusMenu = menu;
            menu.IsOpen = true;
        }
        
        private void RefreshOpenConnectionStatusMenu()
        {
            if (_statusMenu is not { IsOpen: true } menu) return;
            var fresh = new ContextMenu();
            FillConnectionStatusMenu(fresh);
            SyncMenuItems(menu.Items, fresh.Items);
        }

        private static void SyncMenuItems(ItemCollection target, ItemCollection source)
        {
            if (target.Count != source.Count)
            {
                var items = source.Cast<object>().ToList();
                source.Clear();
                target.Clear();
                foreach (var item in items) target.Add(item);
                return;
            }

            for (int i = 0; i < target.Count; i++)
            {
                if (target[i] is not MenuItem tgt || source[i] is not MenuItem src)
                    continue;
                tgt.Header = src.Header;
                tgt.ToolTip = src.ToolTip;
                tgt.IsEnabled = src.IsEnabled;
                if (src.Icon is Ellipse dot) tgt.Icon = MakeStatusDot(dot.Fill);
                SyncMenuItems(tgt.Items, src.Items);
            }
        }

        private static void FillConnectionStatusMenu(ContextMenu menu)
        {
            menu.Items.Clear();
            var connections = Utils.GetAllConnections().ToList();
            if (connections.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = "No integrations active", IsEnabled = false });
                return;
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
                Brush brush = up ? StatusUpBrush
                    : sourceGroup == SubathonSourceGroup.ExternalSoftware || connections.Any(c => c.Configured)
                        ? StatusDownBrush
                        : StatusNoneBrush;
                return MakeLeafItem(source.GetDescription(), brush, null);
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
                if (connection.Source == SubathonEventSource.GoAffPro
                    && GoAffProStoreRegistry.TryGetByInternalName(connection.Service, out var store))
                    label = store.StoreName;
                string detail = ShowableDetail(connection);
                if (detail.Length > 0) label += $":  {detail}";
                sourceItem.Items.Add(MakeLeafItem(label, LeafBrush(connection), null));
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
                Icon = MakeStatusDot(up ? StatusUpBrush : LeafBrush(tunnel)),
                StaysOpenOnClick = true
            };
            item.Items.Add(MakeLeafItem("Webhook Tunnel", LeafBrush(tunnel), null));

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
            var brush = LeafBrush(socket);
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
                item.Items.Add(MakeLeafItem(label, LeafBrush(cli),
                    cli.Status ? null : "Not Installed"));
            }
            if (login != null)
            {
                string label = login.Status && !string.IsNullOrWhiteSpace(login.Detail)
                    ? $"Login:  {login.Detail}"
                    : "Login";
                item.Items.Add(MakeLeafItem(label, LeafBrush(login), null));
            }
            if (tunnel != null)
                item.Items.Add(MakeLeafItem("Tunnel", LeafBrush(tunnel), null));
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
            if (string.Equals(connection.Name, "None", StringComparison.OrdinalIgnoreCase)) return "";
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
