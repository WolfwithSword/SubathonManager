using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.UI.Controls;
using SubathonManager.UI.Services;
using SubathonManager.UI.UiUtils;

namespace SubathonManager.UI.Views.Actions;

public partial class ActionsView {
    private static readonly FontFamily Mono = new("Consolas, Menlo, monospace");

    private readonly List<StoreRow> _globalRows = [];
    private readonly List<StoreRow> _secretRows = [];
    private int _storeSuppress;

    private void InitStore() {
        EnterKeyCommit.Attach(GlobalsPanel, source => _ = CommitFromAsync(source, _globalRows));
        EnterKeyCommit.Attach(SecretsPanel, source => _ = CommitFromAsync(source, _secretRows));
    }

    private void OnGlobalsChanged() {
        Dispatcher.UIThread.Post(() => _ = RefreshStoreAsync());
    }

    private List<StoreRow> RowsOf(ActionStoreKind kind) {
        return kind == ActionStoreKind.Global ? _globalRows : _secretRows;
    }

    private StackPanel PanelOf(ActionStoreKind kind) {
        return kind == ActionStoreKind.Global ? GlobalsPanel : SecretsPanel;
    }

    private async Task RefreshStoreAsync() {
        List<ActionGlobal> all;
        try {
            all = await ServiceManager.Actions.GetGlobalsAsync();
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[Actions] Could not load globals and secrets");
            StoreStatusText.Text = "Could not load globals and secrets - check the logs";
            return;
        }

        foreach (ActionStoreKind kind in Enum.GetValues<ActionStoreKind>())
            Reconcile(kind, all.Where(g => g.Kind == kind).ToList());
        RefreshSavePending();
    }

    private void Reconcile(ActionStoreKind kind, List<ActionGlobal> entries) {
        List<StoreRow> rows = RowsOf(kind);

        foreach (StoreRow gone in rows.Where(r => !r.IsNew && !entries.Any(e => SameName(e.Name, r.Name))).ToList())
            RemoveRow(gone);

        foreach (ActionGlobal entry in entries) {
            StoreRow? row = rows.FirstOrDefault(r => !r.IsNew && SameName(r.Name, entry.Name));
            if (row == null) {
                row = BuildRow(kind);
                row.Name = entry.Name;
                row.NameBox.Text = entry.Name;
                row.NameBox.IsReadOnly = true;
                rows.Add(row);
                PanelOf(kind).Children.Add(row.Root);
            }

            UpdateRow(row, entry);
        }

        if (rows.Count == 0) AddRow(kind, false, false);
    }

    private static bool SameName(string? a, string? b) {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateRow(StoreRow row, ActionGlobal entry) {
        string value = row.Kind == ActionStoreKind.Secret
            ? ServiceManager.Actions.GetSecretValue(entry.Name) ?? ""
            : entry.Value ?? "";

        _storeSuppress++;
        try {
            bool valueEdited = (row.ValueBox.Text ?? "") != row.Saved;
            bool typeEdited = row.TypeBox != null && SelectedType(row) != row.SavedType;
            if (!valueEdited) row.ValueBox.Text = value;
            if (row.TypeBox != null && !typeEdited) SelectType(row, entry.ValueType);
            row.Saved = value;
            row.SavedType = entry.ValueType;
        }
        finally {
            _storeSuppress--;
        }

        RefreshRowState(row);
    }

    private void RefreshRowState(StoreRow row) {
        string name = row.IsNew ? (row.NameBox.Text ?? "").Trim() : row.Name!;
        row.CopyButton.IsEnabled = ActionStepTypeHelper.IsValidName(name);
        if (row.IsNew) {
            row.Detail.IsVisible = false;
            return;
        }

        List<string> usedBy = ServiceManager.Actions.ActionsUsing(row.Kind, row.Name!).Select(a => a.Name).ToList();
        string uses = usedBy.Count == 0 ? "Not used by any action" : $"Used by {string.Join(", ", usedBy)}";
        bool empty = row.Saved.Length == 0;
        row.Detail.IsVisible = true;
        row.Detail.Foreground = empty ? Brushes.Orange : Brushes.Gray;
        row.Detail.Text = empty ? $"No value - steps using it get it empty.  {uses}" : uses;
    }

    private void RefreshSavePending() {
        UiHelpers.UpdateButtonPendingBorder(StoreSaveBorder, _globalRows.Concat(_secretRows).Any(r => r.Dirty));
    }

    private StoreRow BuildRow(ActionStoreKind kind) {
        var nameBox = new TextBox {
            Height = 34, FontFamily = Mono,
            PlaceholderText = kind == ActionStoreKind.Global ? "name, e.g. deaths" : "name, e.g. my_token"
        };
        var valueBox = new TextBox { Height = 34, PlaceholderText = "value" };
        if (kind == ActionStoreKind.Secret) {
            valueBox.PasswordChar = '*';
            TextBoxAssist.SetReveal(valueBox, true);
        }

        var copy = new Button {
            Classes = { "opaquesecondary", "iconbtn" }, Width = 34, Height = 34, Padding = new Thickness(2),
            Content = new SymIcon { Glyph = "Clipboard20" }
        };
        var delete = new Button {
            Classes = { "danger", "iconbtn" }, Width = 34, Height = 34, Padding = new Thickness(2),
            Content = new SymIcon { Glyph = "Delete20" }
        };
        ToolTip.SetTip(delete, "Delete");
        var detail = new TextBlock {
            FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 4, 0, 0),
            IsVisible = false
        };

        ComboBox? typeBox = null;
        if (kind == ActionStoreKind.Global) {
            typeBox = new ComboBox { Height = 34, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (ActionValueType type in Enum.GetValues<ActionValueType>())
                typeBox.Items.Add(new ComboBoxItem { Content = type.GetLabel(), Tag = type });
            typeBox.SelectedIndex = 0;
            ToolTip.SetTip(typeBox, "What the global holds. Set Global steps must give a value of this type");
        }

        var grid = new Grid {
            ColumnDefinitions =
                new ColumnDefinitions(kind == ActionStoreKind.Global ? "Auto,2*,110,3*,Auto" : "Auto,2*,0,3*,Auto"),
            ColumnSpacing = 6
        };

        grid.Children.Add(copy);
        Grid.SetColumn(nameBox, 1);
        grid.Children.Add(nameBox);
        if (typeBox != null) {
            Grid.SetColumn(typeBox, 2);
            grid.Children.Add(typeBox);
        }

        Grid.SetColumn(valueBox, 3);
        grid.Children.Add(valueBox);
        Grid.SetColumn(delete, 4);
        grid.Children.Add(delete);

        var card = new Border {
            Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { grid, detail } }
        };
        card.SetDynamicResource(Border.BorderBrushProperty, "UiBrushBorder");
        card.SetDynamicResource(Border.BackgroundProperty, "UiBrushControlBackground");

        var row = new StoreRow {
            Kind = kind, Root = card, NameBox = nameBox, ValueBox = valueBox, TypeBox = typeBox, CopyButton = copy,
            Detail = detail
        };

        nameBox.TextChanged += (_, _) => Edited(row);
        valueBox.TextChanged += (_, _) => Edited(row);
        if (typeBox != null)
            typeBox.SelectionChanged += (_, _) => {
                UpdateValueHint(row);
                Edited(row);
            };
        copy.Click += async (_, _) => {
            string name = row.IsNew ? (row.NameBox.Text ?? "").Trim() : row.Name!;
            string placeholder = ActionStepTypeHelper.StorePlaceholder(kind, name);
            await UiHelpers.TrySetClipboardTextAsync(placeholder);
            StoreStatusText.Text = $"Copied {placeholder}";
        };
        delete.Click += async (_, _) => await DeleteRowAsync(row);

        UpdateValueHint(row);
        RefreshCopyTip(row);
        return row;
    }

    private void Edited(StoreRow row) {
        if (_storeSuppress > 0) return;
        RefreshRowState(row);
        RefreshCopyTip(row);
        RefreshSavePending();
    }

    private static void RefreshCopyTip(StoreRow row) {
        string name = row.IsNew ? (row.NameBox.Text ?? "").Trim() : row.Name!;
        ToolTip.SetTip(row.CopyButton, ActionStepTypeHelper.IsValidName(name)
            ? $"Copy {ActionStepTypeHelper.StorePlaceholder(row.Kind, name)}"
            : "Names are letters, numbers and _ only (max length: 64)");
    }

    private static ActionValueType SelectedType(StoreRow row) {
        return (row.TypeBox?.SelectedItem as ComboBoxItem)?.Tag is ActionValueType type ? type : ActionValueType.Text;
    }

    private static void SelectType(StoreRow row, ActionValueType type) {
        row.TypeBox!.SelectedItem = row.TypeBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (ActionValueType?)i.Tag == type);
        UpdateValueHint(row);
    }

    private static void UpdateValueHint(StoreRow row) {
        if (row.Kind == ActionStoreKind.Secret) return;
        row.ValueBox.PlaceholderText = SelectedType(row) switch {
            ActionValueType.Number => "a number",
            ActionValueType.Boolean => "true or false",
            _ => "text"
        };
    }

    private StoreRow AddRow(ActionStoreKind kind, bool focus, bool atTop) {
        StoreRow row = BuildRow(kind);
        List<StoreRow> rows = RowsOf(kind);
        int at = atTop ? 0 : rows.Count;
        rows.Insert(at, row);
        PanelOf(kind).Children.Insert(at, row.Root);
        RefreshRowState(row);
        if (focus)
            Dispatcher.UIThread.Post(() => {
                row.Root.BringIntoView();
                row.NameBox.Focus();
            }, DispatcherPriority.Background);
        return row;
    }

    private void RemoveRow(StoreRow row) {
        RowsOf(row.Kind).Remove(row);
        PanelOf(row.Kind).Children.Remove(row.Root);
    }

    private void AddGlobalRow_Click(object? sender, RoutedEventArgs e) {
        AddRow(ActionStoreKind.Global, true, true);
    }

    private void AddSecretRow_Click(object? sender, RoutedEventArgs e) {
        AddRow(ActionStoreKind.Secret, true, true);
    }

    private async Task CommitFromAsync(object? source, List<StoreRow> rows) {
        StoreRow? row = rows.FirstOrDefault(r => source is Visual v && (v == r.Root || r.Root.IsVisualAncestorOf(v)));
        if (row == null) return;
        if (row.IsNew && string.IsNullOrWhiteSpace(row.NameBox.Text)) return;
        if (row.Dirty && await SaveRowAsync(row) is { } error) {
            StoreStatusText.Text = error;
            return;
        }

        if (rows.LastOrDefault() == row) AddRow(row.Kind, true, false);
    }

    private async void SaveStore_Click(object? sender, RoutedEventArgs e) {
        List<StoreRow> changed = _globalRows.Concat(_secretRows)
            .Where(r => r.Dirty && !(r.IsNew && string.IsNullOrWhiteSpace(r.NameBox.Text))).ToList();
        if (changed.Count == 0) {
            StoreStatusText.Text = "Nothing to save";
            return;
        }

        var errors = new List<string>();
        foreach (StoreRow row in changed)
            if (await SaveRowAsync(row) is { } error)
                errors.Add(error);

        int saved = changed.Count - errors.Count;
        StoreStatusText.Text = errors.Count == 0
            ? $"Saved {saved} change(s)"
            : $"Saved {saved}, {errors.Count} not saved: {string.Join("; ", errors)}";
        RefreshSavePending();
    }

    private async Task<string?> SaveRowAsync(StoreRow row) {
        string name = row.IsNew ? (row.NameBox.Text ?? "").Trim() : row.Name!;
        string value = row.ValueBox.Text ?? "";
        string placeholder = ActionStepTypeHelper.StorePlaceholder(row.Kind, name);

        if (!ActionStepTypeHelper.IsValidName(name))
            return $"\"{name}\": names are letters, numbers, and _ only (max length: 64)";

        if (row.IsNew && RowsOf(row.Kind).Any(r => r != row && !r.IsNew && SameName(r.Name, name)))
            return $"{placeholder} already exists";

        string? note = null;
        if (row.Kind == ActionStoreKind.Secret) {
            if (!await ServiceManager.Actions.SetSecretAsync(name, value))
                return $"could not save {placeholder} - check the logs";
        }
        else {
            ActionValueType type = SelectedType(row);
            if (!row.IsNew && value == row.Saved && type != row.SavedType) {
                if (!await ServiceManager.Actions.SetGlobalTypeAsync(name, type))
                    note = $"{placeholder}'s value couldn't convert to {type.GetLabel()} so it was cleared";
                value = (await ServiceManager.Actions.GetGlobalsAsync(ActionStoreKind.Global))
                    .FirstOrDefault(g => SameName(g.Name, name))?.Value ?? "";
            }
            else {
                if (await ServiceManager.Actions.SetGlobalAsync(name, type, value) is { } error)
                    return $"{placeholder}: {error}";
                value = value.Length == 0 ? type.DefaultValue() : type.NormalizeValue(value) ?? "";
            }

            row.SavedType = type;
        }

        _storeSuppress++;
        try {
            row.Name = name;
            row.NameBox.IsReadOnly = true;
            row.ValueBox.Text = value;
            row.Saved = value;
        }
        finally {
            _storeSuppress--;
        }

        RefreshRowState(row);
        RefreshSavePending();
        StoreStatusText.Text = note ?? $"Saved {placeholder}";
        return null;
    }

    private async Task DeleteRowAsync(StoreRow row) {
        if (row.IsNew) {
            RemoveRow(row);
            if (RowsOf(row.Kind).Count == 0) AddRow(row.Kind, false, false);
            RefreshSavePending();
            return;
        }

        string placeholder = ActionStepTypeHelper.StorePlaceholder(row.Kind, row.Name!);
        List<string> usedBy = ServiceManager.Actions.ActionsUsing(row.Kind, row.Name!).Select(a => a.Name).ToList();
        var dialog = new FAContentDialog {
            Title = $"Delete {row.Kind.TokenPrefix()}",
            Content = usedBy.Count == 0
                ? $"Delete {placeholder}?"
                : $"Delete {placeholder}? {usedBy.Count} action(s) use it: {string.Join(", ", usedBy)}. " +
                  "They'll be replaced with an empty value",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel"
        };
        if (await dialog.ShowAsync() != FAContentDialogResult.Primary) return;

        await ServiceManager.Actions.DeleteGlobalAsync(row.Kind, row.Name!);
        StoreStatusText.Text = $"Deleted {placeholder}";
    }

    private sealed class StoreRow {
        public required ActionStoreKind Kind { get; init; }
        public required Control Root { get; init; }
        public required TextBox NameBox { get; init; }
        public required TextBox ValueBox { get; init; }
        public required ComboBox? TypeBox { get; init; }
        public required Button CopyButton { get; init; }
        public required TextBlock Detail { get; init; }

        // null until the row is saved
        public string? Name { get; set; }
        public string Saved { get; set; } = "";
        public ActionValueType SavedType { get; set; }

        public bool IsNew => Name == null;

        public bool Dirty => IsNew
            ? !string.IsNullOrWhiteSpace(NameBox.Text)
            : (ValueBox.Text ?? "") != Saved || (TypeBox != null && SelectedType(this) != SavedType);
    }
}