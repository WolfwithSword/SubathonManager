using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

public abstract class SettingsControl : UserControl {
    private static int _suppressCount;

    internal readonly IDbContextFactory<AppDbContext> _factory =
        AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();

    internal List<DynamicSubRow> _dynamicSubRows = new();
    protected SettingsView Host = null!;

    protected SettingsControl() {
        AttachedToVisualTree += (_, _) => {
            _suppressCount++;
            Dispatcher.UIThread.Post(() => {
                if (_suppressCount > 0) _suppressCount--;
            }, DispatcherPriority.Background);
        };
    }

    protected virtual SubathonEventType? _membershipEventType => null;
    protected virtual StackPanel? _MembershipsPanel => null;

    protected virtual bool allowMembershipDelete => true;

    // synced-tier integrations
    protected virtual TextBox? _DefaultMembershipSecondsBox => null;
    protected virtual TextBox? _DefaultMembershipPointsBox => null;
    protected virtual ComboBox? _MembershipTierCombo => null;

    // a non-tier value row
    protected virtual string? _PerUnitMembershipMeta => null;

    protected virtual void OnMembershipRowsLoaded() {
    }

    public virtual void Init(SettingsView host) {
        Host = host;
    }

    protected void SuppressUnsavedChanges(Action action) {
        _suppressCount++;
        try {
            action();
        }
        finally {
            _suppressCount--;
        }
    }

    protected void RegisterUnsavedChangeHandlers() {
        Dispatcher.UIThread.Post(() => WireInputs(this), DispatcherPriority.Loaded);
    }

    protected void WireControl(Visual control) {
        AttachHandler(control);
        WireInputs(control);
    }

    private void WireInputs(Visual parent) {
        foreach (Visual child in parent.GetVisualChildren()) {
            if (SettingsProperties.GetExcludeFromUnsaved(child))
                continue;

            if (child is Expander expander) {
                WireExpander(expander);
                continue;
            }

            AttachHandler(child);
            WireInputs(child);
        }
    }

    private void AttachHandler(Visual element) {
        if (SettingsProperties.GetUnsavedHandlerAttached(element)) return;

        switch (element) {
            case TextBox tb:
                tb.TextChanged += (s, _) => OnInputChanged(s);
                break;
            case AutoCompleteBox acb:
                acb.TextChanged += (s, _) => OnInputChanged(s);
                break;
            case ComboBox cb:
                cb.SelectionChanged += (s, _) => OnInputChanged(s);
                break;
            case CheckBox chk:
                chk.IsCheckedChanged += (s, _) => OnInputChanged(s);
                break;
            case Slider sld:
                sld.ValueChanged += (s, _) => OnInputChanged(s);
                break;
            default:
                return;
        }

        SettingsProperties.SetUnsavedHandlerAttached(element, true);
        DirtySaveGuard.Rebase(element);
    }

    private void WireExpander(Expander expander) {
        if (SettingsProperties.GetUnsavedHandlerAttached(expander)) return;
        SettingsProperties.SetUnsavedHandlerAttached(expander, true);

        var firstExpand = true;

        expander.PropertyChanged += (_, e) => {
            if (e.Property != Expander.IsExpandedProperty) return;
            if (e.NewValue is not true) return;
            Dispatcher.UIThread.Post(() => {
                if (firstExpand) {
                    firstExpand = false;
                    SuppressUnsavedChanges(() => WireInputs(expander));
                }
            }, DispatcherPriority.Loaded);
        };

        if (expander.IsExpanded) {
            firstExpand = false;
            WireInputs(expander);
        }
    }

    private void OnInputChanged(object? sender) {
        bool realChange = DirtySaveGuard.Consume(sender);
        if (_suppressCount > 0 || !realChange) return;
        SettingsEvents.RaiseSettingsUnsavedChanges(true);
    }

    internal abstract void UpdateStatus(IntegrationConnection? connection);

    protected internal virtual void LoadValues(AppDbContext db) {
    }

    public abstract bool UpdateValueSettings(AppDbContext db);

    protected internal virtual bool UpdateConfigValueSettings() {
        return false;
    }

    public abstract void UpdateCurrencyBoxes(List<string> currencies, string selected);

    public abstract (string seconds, string points, TextBox? timeBox, TextBox? pointsBox) GetValueBoxes(
        SubathonValue val);

    internal static void EnsureUniqueName(List<DynamicSubRow> rows) {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DynamicSubRow row in rows) {
            string current = (row.NameBox.Text ?? "").Trim();
            while (!seen.Add(current.ToLower()))
                current = "New " + current;
            row.NameBox.Text = current;
        }
    }

    internal virtual void AddMembership_Click(object? sender, RoutedEventArgs e) {
        if (_membershipEventType == null) return;
        var name = $"New {_dynamicSubRows.Count}";
        string[] allNames = _dynamicSubRows.Select(x => (x.NameBox.Text ?? "").Trim()).ToArray();
        while (allNames.Contains(name)) name = $"New {name}";
        allNames = _dynamicSubRows.Select(x => x.SubValue.Meta.Trim()).ToArray();
        while (allNames.Contains(name)) name = $"New {name}";
        AddMembershipRow(new SubathonValue
            { EventType = _membershipEventType.Value, Meta = name, Seconds = 0, Points = 0 });
    }

    internal DynamicSubRow? AddMembershipRow(SubathonValue subathonValue) {
        if (_MembershipsPanel == null) return null;
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

        var panelRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        var nameBox = new TextBox {
            Width = 154, Height = 32, Text = subathonValue.Meta ?? "",
            IsReadOnly = !allowMembershipDelete,
            PlaceholderText = "Tier Name",
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(nameBox, "Subscription Tier Name");
        TextBoxAssist.SetClear(nameBox, true);
        var secondsBox = new TextBox {
            Width = 100, Height = 32, Text = $"{subathonValue.Seconds}", PlaceholderText = "Seconds",
            VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        var pointsBox = new TextBox {
            Width = 100, Height = 32, Text = $"{subathonValue.Points}", PlaceholderText = "Points",
            VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(128, 0, 0, 0)
        };

        NumericInputBehaviour.SetMode(secondsBox, NumericInputBehaviour.NumericMode.SignedDecimal);
        NumericInputBehaviour.SetMode(pointsBox, NumericInputBehaviour.NumericMode.SignedDecimal);

        var deleteBtn = new Button {
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

        var subRow = new DynamicSubRow {
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

    internal void DeleteRow(SubathonValue subathonValue, DynamicSubRow subRow) {
        if (_MembershipsPanel == null) return;
        using AppDbContext db = _factory.CreateDbContext();
        SubathonValue? dbRow = db.SubathonValues.FirstOrDefault(x =>
            x.Meta == subathonValue.Meta && x.EventType == subathonValue.EventType);
        if (dbRow != null) {
            db.SubathonValues.Remove(dbRow);
            db.SaveChanges();
        }

        _dynamicSubRows.Remove(subRow);
        _MembershipsPanel.Children.Remove(subRow.RowGrid);
    }

    internal void SyncMembershipTiers(IEnumerable<string> tierNames) {
        if (_membershipEventType is not { } type) return;
        List<string> names = tierNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        using AppDbContext db = _factory.CreateDbContext();
        List<string> existing = db.SubathonValues.Where(v => v.EventType == type && names.Contains(v.Meta))
            .Select(v => v.Meta).ToList();
        // new tiers start from the DEFAULT row so they count the same until set
        SubathonValue? fallback = db.SubathonValues.AsNoTracking()
            .FirstOrDefault(v => v.EventType == type && v.Meta == "DEFAULT");
        List<SubathonValue> newValues = names.Where(n => !existing.Contains(n))
            .Select(n => new SubathonValue {
                Meta = n, Seconds = fallback?.Seconds ?? 0, Points = fallback?.Points ?? 0, EventType = type
            })
            .ToList();
        if (newValues.Count == 0) return;

        db.SubathonValues.AddRange(newValues);
        db.SaveChanges();
        Dispatcher.UIThread.Post(() => SuppressUnsavedChanges(() => LoadMembershipValues(null)));
    }

    internal void LoadMembershipValues(AppDbContext? db) {
        if (_membershipEventType is not { } type || _MembershipsPanel == null) return;
        using AppDbContext? owned = db == null ? _factory.CreateDbContext() : null;
        db ??= owned!;
        List<SubathonValue> values = db.SubathonValues.Where(v => v.EventType == type)
            .OrderBy(v => v.Meta).AsNoTracking().ToList();

        for (int i = _MembershipsPanel.Children.Count - 1; i >= 0; i--) {
            Control child = _MembershipsPanel.Children[i];
            if (child.Name != "DefaultMember" && child.Name != "AddBtn")
                _MembershipsPanel.Children.RemoveAt(i);
        }

        _dynamicSubRows.Clear();
        foreach (SubathonValue value in values) {
            if (value.Meta == _PerUnitMembershipMeta) continue;
            if (value.Meta != "DEFAULT") {
                AddMembershipRow(value);
                continue;
            }

            if (_DefaultMembershipSecondsBox != null && _DefaultMembershipPointsBox != null)
                Host.UpdateTimePointsBoxes(_DefaultMembershipSecondsBox, _DefaultMembershipPointsBox,
                    $"{value.Seconds}", $"{value.Points}");
        }

        RefreshMembershipTierCombo(values.Select(v => v.Meta).Where(m => m != _PerUnitMembershipMeta));
        OnMembershipRowsLoaded();
    }

    internal bool SaveMembershipValues(AppDbContext db) {
        if (_membershipEventType is not { } type) return false;
        var hasUpdated = false;

        SubathonValue? defaultValue =
            db.SubathonValues.FirstOrDefault(sv => sv.EventType == type && sv.Meta == "DEFAULT");
        if (defaultValue != null && double.TryParse(_DefaultMembershipSecondsBox?.Text, out double defaultSeconds) &&
            !defaultSeconds.Equals(defaultValue.Seconds)) {
            defaultValue.Seconds = defaultSeconds;
            hasUpdated = true;
        }

        if (defaultValue != null && double.TryParse(_DefaultMembershipPointsBox?.Text, out double defaultPoints) &&
            !defaultPoints.Equals(defaultValue.Points)) {
            defaultValue.Points = defaultPoints;
            hasUpdated = true;
        }

        List<DynamicSubRow> removeRows =
            _dynamicSubRows.Where(row => string.IsNullOrWhiteSpace(row.NameBox.Text)).ToList();
        if (removeRows.Count > 0) hasUpdated = true;
        foreach (DynamicSubRow row in removeRows)
            DeleteRow(row.SubValue, row);

        EnsureUniqueName(_dynamicSubRows);

        foreach (DynamicSubRow subRow in _dynamicSubRows) {
            string meta = (subRow.NameBox.Text ?? "").Trim();
            if (meta == "DEFAULT") continue;
            if (!double.TryParse(subRow.TimeBox.Text, out double seconds)) seconds = 0;
            if (!double.TryParse(subRow.PointsBox.Text, out double points)) points = 0;

#pragma warning disable CA1862
            SubathonValue? existing = db.SubathonValues.FirstOrDefault(sv =>
                sv.EventType == type && sv.Meta.ToLower() == meta.ToLower());
#pragma warning restore CA1862
            if (existing != null) {
                hasUpdated |= !seconds.Equals(existing.Seconds) || !points.Equals(existing.Points);
                existing.Seconds = seconds;
                existing.Points = points;
                subRow.SubValue = existing;
            }
            else {
                subRow.SubValue.Meta = meta;
                subRow.SubValue.Seconds = seconds;
                subRow.SubValue.Points = points;
                db.SubathonValues.Add(subRow.SubValue);
                hasUpdated = true;
            }
        }

        List<string> names = ["DEFAULT", .. _dynamicSubRows.Select(row => (row.NameBox.Text ?? "").Trim())];
        if (_PerUnitMembershipMeta != null) names.Add(_PerUnitMembershipMeta);
        List<SubathonValue> stale = db.SubathonValues.Where(x => x.EventType == type && !names.Contains(x.Meta))
            .ToList();
        if (stale.Count > 0) {
            db.SubathonValues.RemoveRange(stale);
            hasUpdated = true;
        }

        return hasUpdated;
    }

    private void RefreshMembershipTierCombo(IEnumerable<string> metas) {
        if (_MembershipTierCombo is not { } combo) return;
        string selectedTier = (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

        combo.Items.Clear();
        combo.Items.Add(new ComboBoxItem { Content = "DEFAULT" });
        foreach (string meta in metas.Where(m => m != "DEFAULT" && !string.IsNullOrWhiteSpace(m)).Distinct()
                     .OrderBy(m => m))
            combo.Items.Add(new ComboBoxItem { Content = meta });

        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i =>
            string.Equals(i.Content?.ToString(), selectedTier, StringComparison.OrdinalIgnoreCase)) ?? combo.Items[0];
    }
}

public class DynamicSubRow {
    public required SubathonValue SubValue { get; set; }
    public required TextBox NameBox { get; set; }
    public required TextBox TimeBox { get; set; }
    public required TextBox PointsBox { get; set; }
    public required Grid RowGrid { get; set; }
}