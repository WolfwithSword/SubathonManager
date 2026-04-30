using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.UI.Services;
using SubathonManager.UI.Validation;

namespace SubathonManager.UI.Views.SettingsViews.Streaming;

public partial class YouTubeSettings : SettingsControl
{
    protected override StackPanel? _MembershipsPanel => MembershipsPanel;

    public YouTubeSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            RegisterUnsavedChangeHandlers();
            UpdateStatus(Utils.GetConnection(SubathonEventSource.YouTube, "Chat"));
        };

        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
        };
    }

    public override void Init(SettingsView host)
    {
        Host = host;
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        YTUserHandle.Text = config.Get("YouTube", "Handle", string.Empty)!;
    }
    
    protected internal override void LoadValues(AppDbContext db)
    {
        SuppressUnsavedChanges(() => LoadValuesCore(db));
    }
    
    private void LoadValuesCore(AppDbContext db)
    {
        var values = db.SubathonValues.Where(v => v.EventType == SubathonEventType.YouTubeMembership)
            .OrderBy(meta => meta)
            .AsNoTracking().ToList();

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
            var v = $"{value.Seconds}";
            var p = $"{value.Points}";
            
            if (value is { Meta: "DEFAULT", EventType: SubathonEventType.YouTubeMembership })
            {
                box1 = MemberDefaultTextBox;
                box2 = MemberDefaultTextBox2;
            }
            else if (value.EventType == SubathonEventType.YouTubeMembership)
            {
                var row = AddMembershipRow(value);
            }

            if (!string.IsNullOrWhiteSpace(v) && !string.IsNullOrWhiteSpace(p) && box1 != null && box2 != null)
            {
                Host.UpdateTimePointsBoxes(box1, box2, v, p);
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
            if (comboItem is not ComboBoxItem cbi || !string.Equals(cbi.Content?.ToString(), selectedTier,
                    StringComparison.OrdinalIgnoreCase)) continue;
            SimTierSelection.SelectedItem = cbi;
            break;
        }

        SimTierSelection.SelectedItem ??= SimTierSelection.Items[0];
    }
    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.YouTube }) return;
        Dispatcher.Invoke(() =>
        {
            if (YTUserHandle.Text != connection.Name && connection.Name != "None") YTUserHandle.Text = connection.Name; 
            Host.UpdateConnectionStatus(connection.Status, YTStatusText, ConnectYTBtn);
        });
    }

    public override bool UpdateValueSettings(AppDbContext db)
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
        
        
        var redirectValue = db.SubathonValues.FirstOrDefault(sv =>
            sv.EventType == SubathonEventType.YouTubeRedirect
            && sv.Meta == "");
        if (redirectValue != null && double.TryParse(RaidBox.Text, out var rdSeconds)
                                  && !rdSeconds.Equals(redirectValue.Seconds))
        {
            redirectValue.Seconds = rdSeconds;
            hasUpdated = true;
        }

        if (redirectValue != null && double.TryParse(RaidBox2.Text, out var rdPoints)
                                  && !rdPoints.Equals(redirectValue.Points))
        {
            redirectValue.Points = rdPoints;
            hasUpdated = true;
        }
        
        hasUpdated |= Host.SaveSubTier(db, SubathonEventType.YouTubeMembership, "DEFAULT", MemberDefaultTextBox, MemberDefaultTextBox2);
        hasUpdated |= Host.SaveSubTier(db, SubathonEventType.YouTubeGiftMembership, "DEFAULT", GiftMemberDefaultTextBox, GiftMemberDefaultTextBox2);
        
        
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
            // cleanup ones that were renamed
            List<string> names = ["DEFAULT"];
            foreach (var row in _dynamicSubRows)
            {
                string name = row.NameBox.Text.Trim();
                names.Add(name);
            }
            
            var dbRows = db.SubathonValues.Where(x =>
                !names.Contains(x.Meta) && x.EventType == SubathonEventType.YouTubeMembership).ToList();

            if (dbRows.Count > 0)
            {
                db.SubathonValues.RemoveRange(dbRows);
                hasUpdated = true;
            }
        }
        
        return hasUpdated;
    }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val)
    {
        string v = $"{val.Seconds}";
        string p = $"{val.Points}";
        TextBox? box = null;
        TextBox? box2 = null;
        switch (val.EventType)
        {
            case SubathonEventType.YouTubeGiftMembership:
                box = GiftMemberDefaultTextBox;
                box2 = GiftMemberDefaultTextBox2;
                break;
            case SubathonEventType.YouTubeSuperChat:
                box = DonoBox;
                box2 = DonoBox2;
                break;
            case SubathonEventType.YouTubeRedirect:
                box = RaidBox;
                box2 = RaidBox2;
                break;
        }
        return (v, p, box, box2);
    }

    private void ConnectYouTubeButton_Click(object sender, RoutedEventArgs e)
    {
        string user = YTUserHandle.Text.Trim();
        if (!user.StartsWith('@') && !string.IsNullOrEmpty(user))
            user = "@" + user;
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        if (config.Set("YouTube", "Handle", user))
            config.Save();

        ServiceManager.YouTube.Start(user);
    }
}