using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.UI.Validation;

namespace SubathonManager.UI.Views.SettingsViews.External;

public partial class ExternalServiceSettings : SettingsControl
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<ExternalServiceSettings>>();
    private List<ExternalSubRow> _dynamicSubRows = new();
    
    public ExternalServiceSettings()
    {
        _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        InitializeComponent();
        
        Loaded += (_, _) =>
        {
            RegisterUnsavedChangeHandlers();
        };
    }

    public override void Init(SettingsView host)
    {
        Host = host;
        RegisterUnsavedChangeHandlers();
    }

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        throw new NotImplementedException();
    }

    public override bool UpdateValueSettings(AppDbContext db)
    {
        bool hasUpdated = false;
        var externalDonoValue = db.SubathonValues.FirstOrDefault(sv =>
            sv.EventType == SubathonEventType.ExternalDonation
            && sv.Meta == "");
        if (externalDonoValue != null && double.TryParse(DonoBox.Text, out var exSeconds) &&
            !exSeconds.Equals(externalDonoValue.Seconds))
        {
            externalDonoValue.Seconds = exSeconds;
            hasUpdated = true;
        }

        if (externalDonoValue != null && double.TryParse(DonoBox2.Text, out var exPoints)
            && !exPoints.Equals(externalDonoValue.Points))
        {
            externalDonoValue.Points = exPoints;
            hasUpdated = true;
        }
        
        var defaultSubValue =
            db.SubathonValues.FirstOrDefault(sv =>
                sv.EventType == SubathonEventType.ExternalSub && sv.Meta == "DEFAULT");
        if (defaultSubValue != null && double.TryParse(SubDTextBox.Text, out var defaultSeconds) &&
            !defaultSeconds.Equals(defaultSubValue.Seconds))
        {
            defaultSubValue.Seconds = defaultSeconds;
            hasUpdated = true;
        }

        if (defaultSubValue != null && double.TryParse(SubDTextBox2.Text, out var defaultPoints) &&
            !defaultPoints.Equals(defaultSubValue.Points))
        {
            defaultSubValue.Points = defaultPoints;
            hasUpdated = true;
        }
        
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
            if (meta == "DEFAULT" )
                continue;

            if (!double.TryParse(subRow.TimeBox.Text, out double seconds))
                seconds = 0;

            if (!double.TryParse(subRow.PointsBox.Text, out double points))
                points = 0;
            
#pragma warning disable CA1862
            var existing = db.SubathonValues
                .FirstOrDefault(sv => sv.EventType == SubathonEventType.ExternalSub 
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
        List<string> names = ["DEFAULT"];
        foreach (var row in _dynamicSubRows)
        {
            string name = row.NameBox.Text.Trim();
            names.Add(name);
        }
            
        var dbRows   = db.SubathonValues.Where(x =>
            !names.Contains(x.Meta) && x.EventType == SubathonEventType.ExternalSub).ToList();

        if (dbRows.Count > 0)
        {
            db.SubathonValues.RemoveRange(dbRows);
            hasUpdated = true;
        }
        return hasUpdated;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected)
    {
        CurrencyBox.ItemsSource = currencies;
        CurrencyBox.SelectedItem = selected;
    }


    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val)
    {
        string v = $"{val.Seconds}";
        string p = $"{val.Points}";
        TextBox? box = null;
        TextBox? box2 = null;
        switch (val.EventType)
        {
            case SubathonEventType.ExternalDonation:
                box = DonoBox;
                box2 = DonoBox2;
                break;
        }
        return (v, p, box, box2);
    }

    private void TestExternalDonation_Click(object sender, RoutedEventArgs e)
    {
        var value = SimulateExternalAmt.Text;
        var currency = CurrencyBox.Text;
        Dictionary<string, JsonElement> data = new Dictionary<string, JsonElement>();
        data.Add("type", JsonSerializer.SerializeToElement($"{SubathonEventType.ExternalDonation}"));
        data.Add("user", JsonSerializer.SerializeToElement("SYSTEM"));
        data.Add("currency", JsonSerializer.SerializeToElement(currency));
        data.Add("amount", JsonSerializer.SerializeToElement(value));
        ExternalEventService.ProcessExternalDonation(data);
    }
    
    protected internal override void LoadValues(AppDbContext db)
    {
        SuppressUnsavedChanges(() => LoadValuesCore(db));
    }
    
    private void LoadValuesCore(AppDbContext db)
    {
        var values = db.SubathonValues.Where(v => v.EventType == SubathonEventType.ExternalSub)
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
            
            if (value is { Meta: "DEFAULT", EventType: SubathonEventType.ExternalSub })
            {
                box1 = SubDTextBox;
                box2 = SubDTextBox2;
            }
            else if (value.EventType == SubathonEventType.ExternalSub)
            {
                ExternalSubRow row = AddMembershipRow(value);
            }

            if (!string.IsNullOrWhiteSpace(v) && !string.IsNullOrWhiteSpace(p) && box1 != null && box2 != null)
            {
                Host.UpdateTimePointsBoxes(box1, box2, v, p);
            }
        }

        RefreshTierCombo();
    }
    
    private void TestSub_Click(object sender, RoutedEventArgs e)
    {
        string selectedTier = (SimTierSelection.SelectedItem is ComboBoxItem item) 
            ? item.Content?.ToString() ?? "" 
            : "";       
        Dictionary<string, JsonElement> data = new Dictionary<string, JsonElement>();
        data.Add("user", JsonSerializer.SerializeToElement("SYSTEM"));
        data.Add("type", JsonSerializer.SerializeToElement(nameof(SubathonEventType.ExternalSub)));
        data.Add("value", JsonSerializer.SerializeToElement(selectedTier));
        data.Add("currency", JsonSerializer.SerializeToElement("member"));
        ExternalEventService.ProcessExternalSub(data);
    }
    
    public void RefreshTierCombo()
    {
        string selectedTier = (SimTierSelection.SelectedItem is ComboBoxItem item) 
            ? item.Content?.ToString() ?? "" 
            : "";       
        using var db = _factory.CreateDbContext();

        var metas = db.SubathonValues
            .Where(v => v.EventType == SubathonEventType.ExternalSub)
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
    
    private ExternalSubRow AddMembershipRow(SubathonValue subathonValue)
    {
        var row = new Grid
        {
            Margin = new Thickness(0, 2, 0 ,2),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        
        var panelRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        var nameBox = new Wpf.Ui.Controls.TextBox { Width = 154, Text = subathonValue.Meta ?? "", 
            ToolTip = "Subscription Name", PlaceholderText = "Subscription Name",
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
        
        InputValidationBehavior.SetIsDecimalOnly(secondsBox, true);
        InputValidationBehavior.SetIsDecimalOnly(pointsBox, true);
        
        WireControl(nameBox);
        WireControl(secondsBox);
        WireControl(pointsBox);
        
        panelRow.Children.Add(nameBox);
        panelRow.Children.Add(secondsBox);
        panelRow.Children.Add(pointsBox);
        panelRow.Children.Add(deleteBtn);
        
        row.Children.Add(panelRow);

        MembershipsPanel.Children.Add(row);

        var subRow = new ExternalSubRow
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

    private void DeleteRow(SubathonValue subathonValue, ExternalSubRow subRow)
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
    
    private void AddSub_Click(object sender, RoutedEventArgs e)
    {
        var name = $"New {_dynamicSubRows.Count}";
        var allNames = _dynamicSubRows.Select(x => x.NameBox.Text.Trim()).ToArray();
        while (allNames.Contains(name)) name = $"New {name}";
        allNames = _dynamicSubRows.Select(x => x.SubValue.Meta.Trim()).ToArray();
        while (allNames.Contains(name)) name = $"New {name}";
        var value = new SubathonValue
        {
            EventType = SubathonEventType.ExternalSub,
            Meta = name,
            Seconds = 0,
            Points = 0
        };
        AddMembershipRow(value);
    }
    
    private void EnsureUniqueName(List<ExternalSubRow> rows)
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
}


public class ExternalSubRow
{
    public required SubathonValue SubValue { get; set; }
    public required Wpf.Ui.Controls.TextBox NameBox { get; set; }
    public required Wpf.Ui.Controls.TextBox TimeBox { get; set; }
    public required Wpf.Ui.Controls.TextBox PointsBox { get; set; }
    public required Grid RowGrid { get; set; }
}