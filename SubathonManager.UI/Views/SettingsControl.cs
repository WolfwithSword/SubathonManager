using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.UI.Controls;
using SubathonManager.UI.UiUtils;
using SubathonManager.UI.Validation;
// ReSharper disable InconsistentNaming

namespace SubathonManager.UI.Views;

public abstract class SettingsControl : UserControl
{
    protected SettingsView Host = null!;

    internal readonly IDbContextFactory<AppDbContext> _factory =
        AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    private int _suppressCount = 0;

    protected SettingsControl()
    {
        AttachedToVisualTree += (_, _) =>
        {
            _suppressCount++;
            Dispatcher.UIThread.Post(() =>
            {
                if (_suppressCount > 0) _suppressCount--;
            }, DispatcherPriority.Background);
        };
    }

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
        Dispatcher.UIThread.Post(() => WireInputs(this), DispatcherPriority.Loaded);
    }

    protected void WireControl(Visual control)
    {
        AttachHandler(control);
        WireInputs(control);
    }

    private void WireInputs(Visual parent)
    {
        foreach (var child in parent.GetVisualChildren())
        {
            if (SettingsProperties.GetExcludeFromUnsaved(child))
                continue;

            if (child is Expander expander)
            {
                WireExpander(expander);
                continue;
            }

            AttachHandler(child);
            WireInputs(child);
        }
    }

    private void AttachHandler(Visual element)
    {
        switch (element)
        {
            case TextBox tb:
                tb.TextChanged += (_, _) => OnInputChanged();
                break;
            case ComboBox cb:
                cb.SelectionChanged += (_, _) => OnInputChanged();
                break;
            case CheckBox chk:
                chk.IsCheckedChanged += (_, _) => OnInputChanged();
                break;
            case Slider sld:
                sld.ValueChanged += (_, _) => OnInputChanged();
                break;
        }
    }

    private void WireExpander(Expander expander)
    {
        bool firstExpand = true;

        expander.PropertyChanged += (_, e) =>
        {
            if (e.Property != Expander.IsExpandedProperty) return;
            if (e.NewValue is not true) return;
            Dispatcher.UIThread.Post(() =>
            {
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

    private void OnInputChanged()
    {
        if (_suppressCount > 0) return;
        SettingsEvents.RaiseSettingsUnsavedChanges(true);
    }

    internal abstract void UpdateStatus(IntegrationConnection? connection);

    protected internal virtual void LoadValues(AppDbContext db)
    {
    }

    public abstract bool UpdateValueSettings(AppDbContext db);

    protected internal virtual bool UpdateConfigValueSettings() => false;

    public abstract void UpdateCurrencyBoxes(List<string> currencies, string selected);

    public abstract (string seconds, string points, TextBox? timeBox, TextBox? pointsBox) GetValueBoxes(SubathonValue val);

    internal static void EnsureUniqueName(List<DynamicSubRow> rows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            string current = (row.NameBox.Text ?? "").Trim();
            while (!seen.Add(current.ToLower()))
                current = "New " + current;
            row.NameBox.Text = current;
        }
    }

    internal virtual void AddMembership_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_membershipEventType == null) return;
        var name = $"New {_dynamicSubRows.Count}";
        var allNames = _dynamicSubRows.Select(x => (x.NameBox.Text ?? "").Trim()).ToArray();
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
        var nameBox = new TextBox
        {
            Width = 154, Height = 32, Text = subathonValue.Meta ?? "",
            IsReadOnly = !allowMembershipDelete,
            PlaceholderText = "Tier Name",
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(nameBox, "Subscription Tier Name");
        TextBoxAssist.SetClear(nameBox, true);
        var secondsBox = new TextBox
        {
            Width = 100, Height = 32, Text = $"{subathonValue.Seconds}", PlaceholderText = "Seconds",
            VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        var pointsBox = new TextBox
        {
            Width = 100, Height = 32, Text = $"{subathonValue.Points}", PlaceholderText = "Points",
            VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(128, 0, 0, 0)
        };

        NumericInputBehaviour.SetMode(secondsBox, NumericInputBehaviour.NumericMode.SignedDecimal);
        NumericInputBehaviour.SetMode(pointsBox, NumericInputBehaviour.NumericMode.SignedDecimal);

        var deleteBtn = new Button
        {
            Content = new SymIcon { Glyph = "Delete20", HorizontalAlignment = HorizontalAlignment.Center },
            Foreground = Brushes.Red,
            Cursor = new Cursor(StandardCursorType.Hand),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Width = 32, Height = 32, Margin = new Thickness(64, 0, 0, 0)
        };
        ToolTip.SetTip(deleteBtn, "Delete");

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
    public required TextBox NameBox { get; set; }
    public required TextBox TimeBox { get; set; }
    public required TextBox PointsBox { get; set; }
    public required Grid RowGrid { get; set; }
}
