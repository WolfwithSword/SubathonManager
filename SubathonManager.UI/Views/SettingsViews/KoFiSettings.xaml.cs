using System.Windows.Controls;
using System.Windows;
using System.Text.Json;
using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Integration;
using SubathonManager.Core.Enums;
using SubathonManager.Data;
using SubathonManager.Core;
using SubathonManager.Core.Models;

namespace SubathonManager.UI.Views.SettingsViews;

public partial class KoFiSettings : UserControl
{
    public required SettingsView Host { get; set; }
    private readonly IDbContextFactory<AppDbContext> _factory;
    private List<KoFiSubRow> _dynamicSubRows = new();
    public KoFiSettings()
    {
        _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        InitializeComponent();
    }
    // todo streamerbot link to repo, copy to clipboard contents and open discussion page for it
    public void Init(SettingsView host)
    {
        Host = host;
    }

    public void LoadValues(AppDbContext db)
    {
        var values = db.SubathonValues.Where(v => v.EventType == SubathonEventType.KoFiSub)
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
            
            if (value.Meta == "DEFAULT" && value.EventType == SubathonEventType.KoFiSub)
            {
                box1 = KFSubDTextBox;
                box2 = KFSubDTextBox2;
                Console.WriteLine(p);
            }
            else if (value.EventType == SubathonEventType.KoFiSub)
            {
                KoFiSubRow row = AddMembershipRow(value);
                // box1 = row.TimeBox;
                // box2 = row.PointsBox;
            }

            if (!string.IsNullOrWhiteSpace(v) && !string.IsNullOrWhiteSpace(p) && box1 != null && box2 != null)
            {
                Host!.UpdateTimePointsBoxes(box1, box2, v, p);
            }
        }

        RefreshKoFiTierCombo();
    }

    private void TestKoFiSub_Click(object sender, RoutedEventArgs e)
    {
        string selectedTier = (SimKoFiTierSelection.SelectedItem is ComboBoxItem item) 
            ? item.Content?.ToString() ?? "" 
            : "";       
        Dictionary<string, JsonElement> data = new Dictionary<string, JsonElement>();
        data.Add("user", JsonSerializer.SerializeToElement("SYSTEM"));
        data.Add("type", JsonSerializer.SerializeToElement(nameof(SubathonEventType.KoFiSub)));
        data.Add("value", JsonSerializer.SerializeToElement(selectedTier));
        data.Add("currency", JsonSerializer.SerializeToElement("member"));
        ExternalEventService.ProcessExternalSub(data);
    }
    
    public void RefreshKoFiTierCombo()
    {
        string selectedTier = (SimKoFiTierSelection.SelectedItem is ComboBoxItem item) 
            ? item.Content?.ToString() ?? "" 
            : "";       
        using var db = _factory.CreateDbContext();

        var metas = db.SubathonValues
            .Where(v => v.EventType == SubathonEventType.KoFiSub)
            .Select(v => v.Meta)
            .Where(meta => meta != "DEFAULT" && !string.IsNullOrWhiteSpace(meta))
            .Distinct()
            .OrderBy(meta => meta)
            .AsNoTracking()
            .ToList();

        SimKoFiTierSelection.Items.Clear();
        SimKoFiTierSelection.Items.Add(new ComboBoxItem{Content = "DEFAULT"});
        foreach (var meta in metas)
            SimKoFiTierSelection.Items.Add(new ComboBoxItem { Content = meta });

        foreach (var comboItem in SimKoFiTierSelection.Items)
        {
            if (comboItem is ComboBoxItem cbi && string.Equals(cbi.Content?.ToString(), selectedTier, StringComparison.OrdinalIgnoreCase))
            {
                SimKoFiTierSelection.SelectedItem = cbi;
                break;
            }
        }

        if (SimKoFiTierSelection.SelectedItem == null)
            SimKoFiTierSelection.SelectedItem = SimKoFiTierSelection.Items[0];
    }
    
    private void TestKoFiTip_Click(object sender, RoutedEventArgs e)
    {
        var value = SimulateKFTipAmountBox.Text;
        var currency = CurrencyBox.Text;
        Dictionary<string, JsonElement> data = new Dictionary<string, JsonElement>();
        data.Add("user", JsonSerializer.SerializeToElement("SYSTEM"));
        data.Add("type", JsonSerializer.SerializeToElement(nameof(SubathonEventType.KoFiDonation)));
        data.Add("currency", JsonSerializer.SerializeToElement(currency));
        data.Add("amount", JsonSerializer.SerializeToElement(value));
        ExternalEventService.ProcessExternalDonation(data);
    }

    private KoFiSubRow AddMembershipRow(SubathonValue subathonValue)
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

        var subRow = new KoFiSubRow
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

    private void DeleteRow(SubathonValue subathonValue, KoFiSubRow subRow)
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
            EventType = SubathonEventType.KoFiSub,
            Meta = name,
            Seconds = 0,
            Points = 0
        };
        AddMembershipRow(value);
    }
    
    private void EnsureUniqueName(List<KoFiSubRow> rows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            string original = row.NameBox.Text.Trim();
            string current = original;

            while (!seen.Add(current))
            {
                current = "New " + current;
            }
            row.NameBox.Text = current;
        }
    }
    
    public void UpdateValueSettings(AppDbContext db)
    {
        var defaultSubValue =
            db.SubathonValues.FirstOrDefault(sv =>
                sv.EventType == SubathonEventType.KoFiSub && sv.Meta == "DEFAULT");
        if (defaultSubValue != null && double.TryParse(KFSubDTextBox.Text, out var defaultSeconds))
            defaultSubValue.Seconds = defaultSeconds;

        if (defaultSubValue != null && int.TryParse(KFSubDTextBox2.Text, out var defaultPoints))
            defaultSubValue.Points = defaultPoints;

        var tipValue =
            db.SubathonValues.FirstOrDefault(sv =>
                sv.EventType == SubathonEventType.KoFiDonation);
        if (tipValue != null && double.TryParse(DonoBox.Text, out var tipSeconds))
            tipValue.Seconds = tipSeconds;
        if (tipValue != null && int.TryParse(DonoBox2.Text, out var tipPoints))
            tipValue.Points = tipPoints;

        var removeRows = _dynamicSubRows
            .Where(row =>string.IsNullOrWhiteSpace(row.NameBox.Text))
            .ToList();
        foreach (var row in removeRows)
            DeleteRow(row.SubValue, row);
        
        EnsureUniqueName(_dynamicSubRows);
        
        foreach (var subRow in _dynamicSubRows)
        {
            string meta = subRow.NameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(meta))
            {
                DeleteRow(subRow.SubValue, subRow);
                continue;
            }
            if (meta == "DEFAULT" )
                continue;

            if (!double.TryParse(subRow.TimeBox.Text, out double seconds))
                seconds = 0;

            if (!int.TryParse(subRow.PointsBox.Text, out int points))
                points = 0;
            
            var existing = db.SubathonValues
                .FirstOrDefault(sv => sv.EventType == SubathonEventType.KoFiSub 
                                      && sv.Meta == meta);
            
            if (existing != null)
            {
                existing.Seconds = seconds;
                existing.Points = points;
                subRow.SubValue = existing;
            }
            else
            {
                subRow.SubValue.Meta = meta;
                subRow.SubValue.Seconds = seconds;
                subRow.SubValue.Points = points;
                db.SubathonValues.Add(subRow.SubValue);
            }
        }
    }

    private void OpenKoFiSetup_Click(object sender, RoutedEventArgs e)
    {
        OpenDiscussion();
    }

    private void OpenDiscussion()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/WolfwithSword/SubathonManager/discussions/108",
                UseShellExecute = true
            });
        }
        catch {/**/}
    }
    
    private async void CopyImportString_Click(object sender, RoutedEventArgs e)
    {
        string version = AppServices.AppVersion;
        if (!version.StartsWith('v') || version.Length > 16) version = "nightly";
        string url =
            $"https://github.com/WolfwithSword/SubathonManager/releases/download/{version}/SubathonManager_KoFi.sb";
        try
        {
            using var http = new HttpClient();
            string content = await http.GetStringAsync(url);

            if (string.IsNullOrWhiteSpace(content))
            {
                OpenDiscussion();
                return;
            }

            Clipboard.SetText(content);
            var button = sender as Button;
            var originalContent = button!.Content;
            button!.Content = "Copied!";
            await Task.Delay(1500);
            button!.Content = originalContent;
        }
        catch
        {
            OpenDiscussion();
        }
    }
}

public class KoFiSubRow
{
    public required SubathonValue SubValue { get; set; }
    public required Wpf.Ui.Controls.TextBox NameBox { get; set; }
    public required Wpf.Ui.Controls.TextBox TimeBox { get; set; }
    public required Wpf.Ui.Controls.TextBox PointsBox { get; set; }
    public required Grid RowGrid { get; set; }
}