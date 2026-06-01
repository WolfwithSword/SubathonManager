using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.UI.Validation;

// ReSharper disable InconsistentNaming

namespace SubathonManager.UI.Views;

public abstract class SettingsControl : UserControl
{
#pragma warning disable CS8618
    protected SettingsView Host;
#pragma warning restore CS8618

    
    internal readonly IDbContextFactory<AppDbContext> _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    private int _suppressCount = 0;
    
    internal List<DynamicSubRow> _dynamicSubRows = new();
    protected virtual SubathonEventType? _membershipEventType => null;
    protected virtual StackPanel? _MembershipsPanel => null;

    protected virtual bool allowMembershipDelete => true; 

    public virtual void Init(SettingsView host)
    {
        Host = host;
    }

    protected void SuppressUnsavedChanges(Action action)
    {
        _suppressCount++;
        try { action(); }
        finally { _suppressCount--; }
    }

    protected void RegisterUnsavedChangeHandlers()
    {
        Dispatcher.InvokeAsync(() => WireInputs(this), DispatcherPriority.Loaded);
    }

    protected void WireControl(DependencyObject control)
    {
        AttachHandler(control);
        WireInputs(control);
    }

    private void WireInputs(DependencyObject parent)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);

            if (child is { } dep &&
                SettingsProperties.GetExcludeFromUnsaved(dep))
                continue;

            switch (child)
            {
                case Expander expander:
                    WireExpander(expander);
                    continue;

                default:
                    AttachHandler(child);
                    break;
            }
            WireInputs(child);
        }
    }

    private void AttachHandler(DependencyObject element)
    {
        switch (element)
        {
            case TextBox tb:
                tb.TextChanged += OnInputChanged;
                break;
            case PasswordBox pb:
                pb.PasswordChanged += OnInputChanged;
                break;
            case ComboBox cb:
                cb.SelectionChanged += OnInputChanged;
                break;
            case CheckBox chk:
                chk.Checked += OnInputChanged;
                chk.Unchecked += OnInputChanged;
                break;
            case Slider sld:
                sld.ValueChanged += OnInputChanged;
                break;
        }
    }

    private void WireExpander(Expander expander)
    {
        bool firstExpand = true;

        expander.Expanded += (_, _) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                // ReSharper disable once AccessToModifiedClosure
                if (firstExpand)
                {
                    firstExpand = false;
                    SuppressUnsavedChanges(() => WireInputs(expander));
                }
            }, DispatcherPriority.Loaded);
        };

        if (expander.IsExpanded)
        {
            firstExpand = false;
            WireInputs(expander);
        }
    }

    private void OnInputChanged(object sender, EventArgs e)
    {
        if (_suppressCount > 0) return;
        SettingsEvents.RaiseSettingsUnsavedChanges(true);
    }

    internal abstract void UpdateStatus(IntegrationConnection? connection);

    protected internal virtual void LoadValues(AppDbContext db)
    {
        return;
    }
    public abstract bool UpdateValueSettings(AppDbContext db);

    protected internal virtual bool UpdateConfigValueSettings()
    {
        return false;
    }
    public abstract void UpdateCurrencyBoxes(List<string> currencies, string selected);

    public abstract (string seconds, string points, TextBox? timeBox, TextBox? pointsBox) GetValueBoxes(SubathonValue val);
    
    internal static void EnsureUniqueName(List<DynamicSubRow> rows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            string current = row.NameBox.Text.Trim();
            while (!seen.Add(current.ToLower()))
                current = "New " + current;
            row.NameBox.Text = current;
        }
    }
    
    internal virtual void AddMembership_Click(object sender, RoutedEventArgs e)
    {
        if (_membershipEventType == null) return;
        var name = $"New {_dynamicSubRows.Count}";
        var allNames = _dynamicSubRows.Select(x => x.NameBox.Text.Trim()).ToArray();
        while (allNames.Contains(name)) name = $"New {name}";
        allNames = _dynamicSubRows.Select(x => x.SubValue.Meta.Trim()).ToArray();
        while (allNames.Contains(name)) name = $"New {name}";
        AddMembershipRow(new SubathonValue { EventType = _membershipEventType.Value, Meta = name, Seconds = 0, Points = 0 });
    }
    
    internal DynamicSubRow? AddMembershipRow(SubathonValue subathonValue)
    {
        if (_MembershipsPanel == null) return null;
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

        var panelRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        var nameBox = new Wpf.Ui.Controls.TextBox
        {
            Width = 154, Text = subathonValue.Meta ?? "",
            IsReadOnly = !allowMembershipDelete,
            ClearButtonEnabled = allowMembershipDelete,
            ToolTip = "Subscription Tier Name", PlaceholderText = "Tier Name",
            Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center
        };
        var secondsBox = new Wpf.Ui.Controls.TextBox
        {
            Width = 100, Text = $"{subathonValue.Seconds}", PlaceholderText = "Seconds",
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0)
        };
        var pointsBox = new Wpf.Ui.Controls.TextBox
        {
            Width = 100, Text = $"{subathonValue.Points}", PlaceholderText = "Points",
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(128, 0, 0, 0)
        };
        
        var deleteBtn = new Wpf.Ui.Controls.Button
        {
            ToolTip = "Delete",
            Icon = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24,
                Margin = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Center
            },
            Foreground = System.Windows.Media.Brushes.Red,
            Cursor = System.Windows.Input.Cursors.Hand,
            Width = 36, Height = 36, Margin = new Thickness(64, 0, 0, 0)
        };

        InputValidationBehavior.SetIsDecimalOnly(secondsBox, true);
        InputValidationBehavior.SetIsDecimalOnly(pointsBox, true);

        WireControl(nameBox);
        WireControl(secondsBox);
        WireControl(pointsBox);

        panelRow.Children.Add(nameBox);
        panelRow.Children.Add(secondsBox);
        panelRow.Children.Add(pointsBox);
        if (allowMembershipDelete)
            panelRow.Children.Add(deleteBtn);
        row.Children.Add(panelRow);
        _MembershipsPanel.Children.Add(row);

        var subRow = new DynamicSubRow
        {
            SubValue = subathonValue,
            NameBox = nameBox,
            TimeBox = secondsBox,
            PointsBox = pointsBox,
            RowGrid = row
        };
        _dynamicSubRows.Add(subRow);

        if (allowMembershipDelete)
            deleteBtn.Click += (_, _) => DeleteRow(subathonValue, subRow);
        return subRow;
    }
    
    internal void DeleteRow(SubathonValue subathonValue, DynamicSubRow subRow)
    {
        if (_MembershipsPanel == null) return;
        using var db = _factory.CreateDbContext();
        var dbRow = db.SubathonValues.FirstOrDefault(x =>
            x.Meta == subathonValue.Meta && x.EventType == subathonValue.EventType);
        if (dbRow != null) { db.SubathonValues.Remove(dbRow); db.SaveChanges(); }
        _dynamicSubRows.Remove(subRow);
        _MembershipsPanel.Children.Remove(subRow.RowGrid);
    }

}

public class DynamicSubRow
{
    public required SubathonValue SubValue { get; set; }
    public required Wpf.Ui.Controls.TextBox NameBox { get; set; }
    public required Wpf.Ui.Controls.TextBox TimeBox { get; set; }
    public required Wpf.Ui.Controls.TextBox PointsBox { get; set; }
    public required Grid RowGrid { get; set; }
}
