using System.Windows.Controls;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Events;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.UI.Services;
using SubathonManager.UI.Validation;

namespace SubathonManager.UI.Views.SettingsViews;

public partial class YouTubeSettings : UserControl
{
    public required SettingsView Host { get; set; }
    private readonly IDbContextFactory<AppDbContext> _factory;
    private List<MembershipRow> _dynamicSubRows = new();
    public YouTubeSettings()
    {
        _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        InitializeComponent();
    }

    public void Init(SettingsView host)
    {
        Host = host;
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        IntegrationEvents.ConnectionUpdated += UpdateYoutubeStatus;
        YTUserHandle.Text = config!.Get("YouTube", "Handle", string.Empty)!;
    }
    
    public void LoadValues(AppDbContext db)
    {
        var values = db.SubathonValues.Where(v => v.EventType == SubathonEventType.YouTubeMembership)
            .OrderBy(meta => meta)
            .AsNoTracking().ToList();
        
        string v, p;
        for (int i = MembershipsPanel.Children.Count - 1; i >= 0; i--)
        {
            var child = MembershipsPanel.Children[i];

            if (child is FrameworkElement fe && fe.Name != "DefaultMember" && fe.Name != "AddBtn")
            {
                MembershipsPanel.Children.RemoveAt(i);
            }
        }
        _dynamicSubRows.Clear();
        foreach (var value in values)
        {      
            TextBox? box1 = null;
            TextBox? box2 = null;
            v = $"{value.Seconds}";
            p = $"{value.Points}";
            
            if (value.Meta == "DEFAULT" && value.EventType == SubathonEventType.YouTubeMembership)
            {
                box1 = MemberDefaultTextBox;
                box2 = MemberDefaultTextBox2;
            }
            else if (value.EventType == SubathonEventType.YouTubeMembership)
            {
                MembershipRow row = AddMembershipRow(value);
            }

            if (!string.IsNullOrWhiteSpace(v) && !string.IsNullOrWhiteSpace(p) && box1 != null && box2 != null)
            {
                Host!.UpdateTimePointsBoxes(box1, box2, v, p);
            }
        }

        RefreshTierCombo();
    }
    
    public void RefreshTierCombo()
    {
        string selectedTier = (SimTierSelection.SelectedItem is ComboBoxItem item) 
            ? item.Content?.ToString() ?? "" 
            : "";       
        using var db = _factory.CreateDbContext();

        var metas = db.SubathonValues
            .Where(v => v.EventType == SubathonEventType.YouTubeMembership)
            .Select(v => v.Meta)
            .Where(meta => meta != "DEFAULT" && !string.IsNullOrWhiteSpace(meta))
            .Distinct()
            .OrderBy(meta => meta)
            .AsNoTracking()
            .ToList();

        SimTierSelection.Items.Clear();
        SimTierSelection.Items.Add(new ComboBoxItem{Content = "DEFAULT"});
        foreach (var meta in metas)
            SimTierSelection.Items.Add(new ComboBoxItem { Content = meta });

        foreach (var comboItem in SimTierSelection.Items)
        {
            if (comboItem is ComboBoxItem cbi && string.Equals(cbi.Content?.ToString(), selectedTier, StringComparison.OrdinalIgnoreCase))
            {
                SimTierSelection.SelectedItem = cbi;
                break;
            }
        }

        if (SimTierSelection.SelectedItem == null)
            SimTierSelection.SelectedItem = SimTierSelection.Items[0];
    }
    
    
    private MembershipRow AddMembershipRow(SubathonValue subathonValue)
    {
        var row = new Grid
        {
            Margin = new Thickness(0, 2, 0 ,2),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        
        var panelRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        var nameBox = new Wpf.Ui.Controls.TextBox { Width = 154, Text = subathonValue.Meta ?? "", 
            ToolTip = "Tier Name", PlaceholderText = "Tier Name",
            Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var secondsBox = new Wpf.Ui.Controls.TextBox { Width = 100, Text = $"{subathonValue.Seconds}", PlaceholderText = "Seconds",
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0)};
        var pointsBox = new Wpf.Ui.Controls.TextBox { Width = 100, Text = $"{subathonValue.Points}", PlaceholderText = "Points",
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(128, 0, 0, 0) };
        
        InputValidationBehavior.SetIsDecimalOnly(secondsBox, true);
        InputValidationBehavior.SetIsDecimalOnly(pointsBox, true);
        
        var deleteBtn = new Wpf.Ui.Controls.Button { ToolTip="Delete", 
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24,
                Margin = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Center},
            Foreground = System.Windows.Media.Brushes.Red,
            Cursor = System.Windows.Input.Cursors.Hand,
            Width = 36, Height = 36, Margin = new Thickness(64,0,0,0) };

        panelRow.Children.Add(nameBox);
        panelRow.Children.Add(secondsBox);
        panelRow.Children.Add(pointsBox);
        panelRow.Children.Add(deleteBtn);
        
        row.Children.Add(panelRow);

        MembershipsPanel.Children.Add(row);

        var subRow = new MembershipRow
        {
            SubValue = subathonValue,
            NameBox = nameBox,
            TimeBox = secondsBox,
            PointsBox = pointsBox,
            RowGrid = row
        };
        
        _dynamicSubRows.Add(subRow);

        deleteBtn.Click += (s, e) =>
        {
            DeleteRow(subathonValue, subRow);
        };
        return subRow;
    }

    private void DeleteRow(SubathonValue subathonValue, MembershipRow subRow)
    {
        using var db = _factory.CreateDbContext();

        var dbRow = db.SubathonValues
            .FirstOrDefault(x => x.Meta == subathonValue.Meta && x.EventType == subathonValue.EventType);

        if (dbRow != null)
        {
            db.SubathonValues.Remove(dbRow);
            db.SaveChanges();
        }

        _dynamicSubRows.Remove(subRow);
        MembershipsPanel.Children.Remove(subRow.RowGrid);
    }
    
    private void AddMembership_Click(object sender, RoutedEventArgs e)
    {
        var name = $"New {_dynamicSubRows.Count}";
        var allNames = _dynamicSubRows.Select(x => x.NameBox.Text.Trim()).ToArray();
        while (allNames.Contains(name)) name = $"New {name}";
        allNames = _dynamicSubRows.Select(x => x.SubValue.Meta.Trim()).ToArray();
        while (allNames.Contains(name)) name = $"New {name}";
        var value = new SubathonValue
        {
            EventType = SubathonEventType.YouTubeMembership,
            Meta = name,
            Seconds = 0,
            Points = 0
        };
        AddMembershipRow(value);
    }
    
    private void EnsureUniqueName(List<MembershipRow> rows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            string original = row.NameBox.Text.Trim();
            string current = original;

            while (!seen.Add(current.ToLower()))
            {
                current = "New " + current;
            }
            row.NameBox.Text = current;
        }
    }
    
    private void UpdateYoutubeStatus(bool status, SubathonEventSource source, string name, string service)
    {
        if (source != SubathonEventSource.YouTube) return;
        Dispatcher.Invoke(() =>
        {
            if (YTUserHandle.Text != name && name != "None") YTUserHandle.Text = name; 
            Host!.UpdateConnectionStatus(status, YTStatusText, ConnectYTBtn);
        });
    }

    public bool UpdateValueSettings(AppDbContext db)
    {
        bool hasUpdated = false;
        var superchatValue = db.SubathonValues.FirstOrDefault(sv =>
            sv.EventType == SubathonEventType.YouTubeSuperChat
            && sv.Meta == "");
        if (superchatValue != null && double.TryParse(DonoBox.Text, out var scSeconds)
            && !scSeconds.Equals(superchatValue.Seconds))
        {
            superchatValue.Seconds = scSeconds;
            hasUpdated = true;
        }

        if (superchatValue != null && double.TryParse(DonoBox2.Text, out var scPoints)
            && !scPoints.Equals(superchatValue.Points))
        {
            superchatValue.Points = scPoints;
            hasUpdated = true;
        }

        hasUpdated |= Host!.SaveSubTier(db, SubathonEventType.YouTubeMembership, "DEFAULT", MemberDefaultTextBox, MemberDefaultTextBox2);
        hasUpdated |= Host!.SaveSubTier(db, SubathonEventType.YouTubeGiftMembership, "DEFAULT", GiftMemberDefaultTextBox, GiftMemberDefaultTextBox2);
        
        
        var removeRows = _dynamicSubRows
            .Where(row =>string.IsNullOrWhiteSpace(row.NameBox.Text))
            .ToList();
        if (removeRows.Any()) 
            hasUpdated = true;
        foreach (var row in removeRows)
            DeleteRow(row.SubValue, row);
        
        EnsureUniqueName(_dynamicSubRows);
        
        foreach (var subRow in _dynamicSubRows)
        {
            string meta = subRow.NameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(meta))
            {
                DeleteRow(subRow.SubValue, subRow);
                hasUpdated = true;
                continue;
            }
            if (meta == "DEFAULT")
                continue;

            if (!double.TryParse(subRow.TimeBox.Text, out double seconds))
                seconds = 0;

            if (!double.TryParse(subRow.PointsBox.Text, out double points))
                points = 0;
            
#pragma warning disable CA1862
            var existing = db.SubathonValues
                .FirstOrDefault(sv => sv.EventType == SubathonEventType.YouTubeMembership 
                                      && sv.Meta.ToLower() == meta.ToLower());
#pragma warning restore CA1862
            
            if (existing != null)
            {
                existing.Seconds = seconds;
                existing.Points = points;
                subRow.SubValue = existing;
                if (!seconds.Equals(existing.Seconds) || !points.Equals(existing.Points))
                    hasUpdated = true;
            }
            else
            {
                subRow.SubValue.Meta = meta;
                subRow.SubValue.Seconds = seconds;
                subRow.SubValue.Points = points;
                db.SubathonValues.Add(subRow.SubValue);
                hasUpdated = true;
            }
        }
        
        return hasUpdated;
    }

    private void ConnectYouTubeButton_Click(object sender, RoutedEventArgs e)
    {
        string user = YTUserHandle.Text.Trim();
        if (!user.StartsWith("@") && !string.IsNullOrEmpty(user))
            user = "@" + user;
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        config!.Set("YouTube", "Handle", user);
        config!.Save();

        ServiceManager.YouTube.Start(user);
    }
    
    public class MembershipRow
    {
        public required SubathonValue SubValue { get; set; }
        public required Wpf.Ui.Controls.TextBox NameBox { get; set; }
        public required Wpf.Ui.Controls.TextBox TimeBox { get; set; }
        public required Wpf.Ui.Controls.TextBox PointsBox { get; set; }
        public required Grid RowGrid { get; set; }
    }
}