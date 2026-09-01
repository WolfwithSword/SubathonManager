using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.UI.Controls;
using SubathonManager.UI.Services;
using SubathonManager.UI.UiUtils;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views.WheelSpin;

public partial class WheelEditor : UserControl {
    private const int HistoryPageSize = 10;

    private static readonly SolidColorBrush SelectedRowBrush = new(Color.FromArgb(30, 100, 149, 237));

    private readonly IConfig _config;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger<WheelEditor> _logger;
    private WheelSet? _activeWheel;
    private readonly Dictionary<string, string> _vtsHotkeyIdsByDisplay =
        new(StringComparer.OrdinalIgnoreCase);

    private WheelSpinHistoryStatus? _historyFilter;
    private bool _historyLoading;
    private int _historyOffset;
    private bool _initialized;
    private bool _isSpinning;
    private volatile bool _multiplierActive;
    private int _multiplierRefreshQueued;
    private WheelItem? _selectedItem;
    private int _spinsOwed;
    private int _suppressCount;

    public WheelEditor() {
        _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        _config = AppServices.Provider.GetRequiredService<IConfig>();
        _logger = AppServices.Provider.GetRequiredService<ILogger<WheelEditor>>();
        InitializeComponent();
        PopulateActionTypeComboBox();
        PopulateVtsComboBoxes();
        LoadActiveWheel();
        LoadGlobalState();

        WheelEvents.WheelSpinRequested += OnWheelSpinRequested;
        Loaded += (_, _) => {
            if (!_initialized) {
                AttachChangeHandlers();
                EnterKeyCommit.Attach(this, () => Save_Click(this, new RoutedEventArgs()));
                _initialized = true;
            }

            SubathonEvents.SubathonDataUpdate += OnSubathonDataUpdate;
            WheelEvents.OnSpinsOwedUpdateFromEvent += AdjustSpinsBoxByEvent;
            WheelEvents.WheelSpinStatusChanged += OnWheelSpinStatusChanged;
            ServiceManager.VTubeStudio.ModelDataChanged += OnVtsModelDataChanged;
            IntegrationEvents.ConnectionUpdated += OnVtsConnectionUpdated;
            RefreshSpinsOwed();
        };
        Unloaded += (_, _) => {
            SubathonEvents.SubathonDataUpdate -= OnSubathonDataUpdate;
            WheelEvents.OnSpinsOwedUpdateFromEvent -= AdjustSpinsBoxByEvent;
            WheelEvents.WheelSpinStatusChanged -= OnWheelSpinStatusChanged;
            ServiceManager.VTubeStudio.ModelDataChanged -= OnVtsModelDataChanged;
            IntegrationEvents.ConnectionUpdated -= OnVtsConnectionUpdated;
        };
    }

    private VtsTargetKind SelectedVtsKind =>
        (VtsKindBox.SelectedItem as ComboBoxItem)?.Tag as VtsTargetKind? ?? VtsTargetKind.Expression;

    private void LoadGlobalState() {
        int delay = int.TryParse(_config.Get("WheelSpin", "SpinDelaySeconds", "4"), out int d) ? Math.Max(0, d) : 4;
        SuppressChanges(() => SpinDelayBox.Text = delay.ToString());

        using AppDbContext db = _factory.CreateDbContext();
        _spinsOwed = StateValueHelper.Get<int>(db, StateKeys.WheelSpinsOwed);
        SuppressChanges(() => SpinsOwedBox.Text = _spinsOwed.ToString());

        SubathonData? subathon = db.SubathonDatas
            .Include(s => s.Multiplier)
            .AsNoTracking()
            .FirstOrDefault(s => s.IsActive);
        _multiplierActive = subathon?.Multiplier?.IsRunning() ?? false;
    }

    private void PopulateActionTypeComboBox() {
        ActionTypeBox.Items.Clear();
        foreach (WheelSpinActionType t in Enum.GetValues<WheelSpinActionType>())
            ActionTypeBox.Items.Add(new ComboBoxItem { Content = t.GetLabel(), Tag = (WheelSpinActionType?)t });
        ActionTypeBox.SelectedIndex = 0;
    }

    private void LoadActiveWheel() {
        using AppDbContext db = _factory.CreateDbContext();
        List<WheelSet> allWheels = db.WheelSets.OrderBy(w => w.Name).ToList();
        if (allWheels.Count == 0) {
            StatusText.Text = "No wheels found";
            return;
        }

        SuppressChanges(() => {
            WheelSelectorBox.Items.Clear();
            foreach (WheelSet w in allWheels)
                WheelSelectorBox.Items.Add(new ComboBoxItem { Content = w.Name, Tag = w.Id });
        });

        WheelSet activeWheel = allWheels.FirstOrDefault(w => w.IsActive) ?? allWheels.First();
        SuppressChanges(() => {
            ComboBoxItem? item = WheelSelectorBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (Guid)i.Tag! == activeWheel.Id);
            WheelSelectorBox.SelectedItem = item;
        });

        LoadWheelById(activeWheel.Id);
    }

    private void LoadWheelById(Guid wheelId) {
        using AppDbContext db = _factory.CreateDbContext();
        _activeWheel = db.WheelSets
            .Include(w => w.WheelItems)
            .ThenInclude(i => i.Action)
            .FirstOrDefault(w => w.Id == wheelId);

        if (_activeWheel == null) {
            StatusText.Text = "Wheel not found";
            return;
        }

        StatusText.Text = "";
        _selectedItem = null;
        ItemDetailBorder.IsVisible = false;

        SuppressChanges(() => {
            WheelNameTextBox.Text = _activeWheel.Name;
            SpinCountBox.Text = _activeWheel.SpinCount.ToString();
        });

        using AppDbContext dbActive = _factory.CreateDbContext();
        foreach (WheelSet w in dbActive.WheelSets.Where(w => w.Id != wheelId))
            w.IsActive = false;
        WheelSet? activeTracked = dbActive.WheelSets.Find(wheelId);
        if (activeTracked != null) activeTracked.IsActive = true;
        dbActive.SaveChanges();

        using AppDbContext db2 = _factory.CreateDbContext();
        DeleteWheelBtn.IsEnabled = db2.WheelSets.Count() > 1;

        Dispatcher.UIThread.Post(LoadItemRows);
        Dispatcher.UIThread.Post(async () => await LoadHistoryAsync());
    }

    private void WheelSelectorBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        if (_suppressCount > 0) return;
        if (WheelSelectorBox.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not Guid wheelId) return;
        LoadWheelById(wheelId);
    }

    private async void WheelNameTextBox_LostFocus(object? sender, RoutedEventArgs e) {
        if (_activeWheel == null) return;
        string newName = (WheelNameTextBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName == _activeWheel.Name) return;

        _activeWheel.Name = newName;

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        WheelSet? tracked = await db.WheelSets.FindAsync(_activeWheel.Id);
        if (tracked == null) return;
        tracked.Name = newName;
        await db.SaveChangesAsync();

        SuppressChanges(() => {
            ComboBoxItem? item = WheelSelectorBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (Guid)i.Tag! == _activeWheel.Id);
            item?.Content = newName;
        });
        RaiseWheelDataChanged();
    }

    private async void ResetSpinCount_Click(object? sender, RoutedEventArgs e) {
        if (_activeWheel == null) return;

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        WheelSet? tracked = await db.WheelSets.FindAsync(_activeWheel.Id);
        if (tracked == null) return;
        tracked.SpinCount = 0;
        await db.SaveChangesAsync();

        _activeWheel.SpinCount = 0;
        SuppressChanges(() => SpinCountBox.Text = "0");
        RaiseWheelDataChanged();
    }

    private async void SpinCountBox_LostFocus(object? sender, RoutedEventArgs e) {
        if (_activeWheel == null) return;
        if (!int.TryParse(SpinCountBox.Text, out int newCount)) return;
        newCount = Math.Max(0, newCount);
        if (newCount == _activeWheel.SpinCount) return;

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        WheelSet? tracked = await db.WheelSets.FindAsync(_activeWheel.Id);
        if (tracked == null) return;
        tracked.SpinCount = newCount;
        await db.SaveChangesAsync();
        _activeWheel.SpinCount = newCount;
        SuppressChanges(() => SpinCountBox.Text = newCount.ToString());
        RaiseWheelDataChanged();
    }

    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e) {
        (sender as Control)?.Focus();
    }

    private async void NewWheel_Click(object? sender, RoutedEventArgs e) {
        await using AppDbContext db = await _factory.CreateDbContextAsync();
        var newWheel = new WheelSet { Name = "New Wheel", IsActive = false, SpinCount = 0 };
        db.WheelSets.Add(newWheel);
        await db.SaveChangesAsync();

        var newItem = new ComboBoxItem { Content = newWheel.Name, Tag = newWheel.Id };
        SuppressChanges(() => {
            WheelSelectorBox.Items.Add(newItem);
            WheelSelectorBox.SelectedItem = newItem;
        });

        LoadWheelById(newWheel.Id);
        DeleteWheelBtn.IsEnabled = true;
        WheelNameTextBox.Focus();
    }

    private async void DeleteWheel_Click(object? sender, RoutedEventArgs e) {
        if (_activeWheel == null) return;

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        int total = await db.WheelSets.CountAsync();
        if (total <= 1) return;

        Guid deletingId = _activeWheel.Id;
        bool wasActive = _activeWheel.IsActive;

        WheelSet? tracked = await db.WheelSets.FindAsync(deletingId);
        if (tracked != null)
            db.WheelSets.Remove(tracked);

        if (wasActive) {
            WheelSet? next = await db.WheelSets
                .Where(w => w.Id != deletingId)
                .OrderBy(w => w.Name)
                .FirstOrDefaultAsync();
            next?.IsActive = true;
        }

        await db.SaveChangesAsync();

        SuppressChanges(() => {
            ComboBoxItem? item = WheelSelectorBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (Guid)i.Tag! == deletingId);
            if (item != null) WheelSelectorBox.Items.Remove(item);
        });

        WheelSet remaining = db.WheelSets.OrderBy(w => w.Name).First();
        ComboBoxItem? selectItem = WheelSelectorBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (Guid)i.Tag! == remaining.Id);
        SuppressChanges(() => WheelSelectorBox.SelectedItem = selectItem);

        LoadWheelById(remaining.Id);
        DeleteWheelBtn.IsEnabled = WheelSelectorBox.Items.Count > 1;
    }

    private async void LoadItemRows() {
        ItemsStack.Children.Clear();
        if (_activeWheel == null) return;

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        List<WheelItem> items = await db.WheelItems
            .Include(i => i.Action)
            .Where(i => i.WheelId == _activeWheel.Id)
            .OrderBy(i => i.Index)
            .ToListAsync();

        _activeWheel.WheelItems = items;

        foreach (WheelItem item in items)
            ItemsStack.Children.Add(BuildItemRow(item));

        RefreshRowHighlights();
        RaiseWheelDataChanged();
    }

    private Grid BuildItemRow(WheelItem item) {
        var row = new Grid {
            Margin = new Thickness(4, 0, 4, 4),
            Tag = item,
            MinHeight = 30,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            ColumnDefinitions = new ColumnDefinitions("34,*,50,82,80,36")
        };
        row.Tapped += (_, e) => {
            if (!UiHelpers.IsInteractiveSource(e.Source, row)) SelectItem(item, row);
        };

        var enabledCheck = new CheckBox {
            IsChecked = item.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        ToolTip.SetTip(enabledCheck, "Enable / disable this item");
        enabledCheck.IsCheckedChanged +=
            (_, _) => OnRowEnabledToggled(item, enabledCheck, enabledCheck.IsChecked ?? false);
        Grid.SetColumn(enabledCheck, 0);

        var textLabel = new TextBlock {
            Text = item.Text,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0, 4, 0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(textLabel, item.Text);
        Grid.SetColumn(textLabel, 1);

        var weightLabel = new TextBlock {
            Text = item.Weight.ToString(),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Brushes.CornflowerBlue
        };
        Grid.SetColumn(weightLabel, 2);

        Control qtyElement;
        if (item.IsInfinite) {
            qtyElement = new TextBlock {
                Text = "∞",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }
        else {
            var qtyLabel = new TextBlock {
                Text = item.Quantity.ToString(),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                MinWidth = 24,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0)
            };
            var minusBtn = new Button {
                Content = new SymIcon { Glyph = "Subtract12" },
                Width = 20, Height = 20, Padding = new Thickness(1),
                IsEnabled = item.Quantity > 0
            };
            ToolTip.SetTip(minusBtn, "Decrease quantity");
            var plusBtn = new Button {
                Content = new SymIcon { Glyph = "Add12" },
                Width = 20, Height = 20, Padding = new Thickness(1)
            };
            ToolTip.SetTip(plusBtn, "Increase quantity");
            minusBtn.Click += async (_, _) => {
                if (item.Quantity <= 0) return;
                item.Quantity = Math.Max(0, item.Quantity - 1);
                qtyLabel.Text = item.Quantity.ToString();
                minusBtn.IsEnabled = item.Quantity > 0;
                if (_selectedItem?.Id == item.Id) {
                    _selectedItem.Quantity = item.Quantity;
                    SuppressChanges(() => ItemQuantityBox.Text = item.Quantity.ToString());
                }

                await using AppDbContext db = await _factory.CreateDbContextAsync();
                WheelItem? tracked = await db.WheelItems.FindAsync(item.Id);
                if (tracked != null) {
                    tracked.Quantity = item.Quantity;
                    await db.SaveChangesAsync();
                }

                RaiseWheelDataChanged();
            };
            plusBtn.Click += async (_, _) => {
                item.Quantity++;
                qtyLabel.Text = item.Quantity.ToString();
                minusBtn.IsEnabled = true;
                if (_selectedItem?.Id == item.Id) {
                    _selectedItem.Quantity = item.Quantity;
                    SuppressChanges(() => ItemQuantityBox.Text = item.Quantity.ToString());
                }

                await using AppDbContext db = await _factory.CreateDbContextAsync();
                WheelItem? tracked = await db.WheelItems.FindAsync(item.Id);
                if (tracked != null) {
                    tracked.Quantity = item.Quantity;
                    await db.SaveChangesAsync();
                }

                RaiseWheelDataChanged();
            };
            var qtyPanel = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            qtyPanel.Children.Add(minusBtn);
            qtyPanel.Children.Add(qtyLabel);
            qtyPanel.Children.Add(plusBtn);
            qtyElement = qtyPanel;
        }

        Grid.SetColumn(qtyElement, 3);

        string actionText = item.Action == null
            ? "M"
            : item.Action.ActionType switch {
                WheelSpinActionType.AddTime => "+Time",
                WheelSpinActionType.SubtractTime => "-Time",
                WheelSpinActionType.SetMultiplier => "Mult",
                WheelSpinActionType.Reroll => "Reroll",
                _ => "M"
            };
        var actionLabel = new TextBlock {
            Text = actionText,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = item.Action != null ? Brushes.MediumSeaGreen : Brushes.Gray
        };
        ToolTip.SetTip(actionLabel, item.Action != null
            ? $"{item.Action.ActionType}: {item.Action.Parameter}"
            : "No action");
        Grid.SetColumn(actionLabel, 4);

        var deleteBtn = new Button {
            Content = new SymIcon { Glyph = "Delete20" },
            Width = 30, Height = 30, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = Brushes.Red,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(deleteBtn, "Delete item");
        deleteBtn.Click += (_, _) => DeleteItem_Click(item);
        Grid.SetColumn(deleteBtn, 5);

        row.Children.Add(enabledCheck);
        row.Children.Add(textLabel);
        row.Children.Add(weightLabel);
        row.Children.Add(qtyElement);
        row.Children.Add(actionLabel);
        row.Children.Add(deleteBtn);

        return row;
    }

    private async void OnRowEnabledToggled(WheelItem item, CheckBox checkBox, bool enabled) {
        if (_suppressCount > 0) return;

        if (enabled && !IsItemActionValid(item, out string err)) {
            SuppressChanges(() => checkBox.IsChecked = false);
            StatusText.Text = $"Cannot enable: {err}";
            return;
        }

        item.Enabled = enabled;
        if (_selectedItem?.Id == item.Id) _selectedItem.Enabled = enabled;

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        WheelItem? tracked = await db.WheelItems.FindAsync(item.Id);
        if (tracked == null) return;
        tracked.Enabled = enabled;
        await db.SaveChangesAsync();
        RaiseWheelDataChanged();
    }

    private void SelectItem(WheelItem item, Grid? clickedRow = null) {
        _selectedItem = item;
        StatusText.Text = "";
        ItemDetailBorder.IsVisible = true;

        SuppressChanges(() => {
            ItemTextBox.Text = item.Text;
            ItemWeightBox.Text = item.Weight.ToString();
            ItemQuantityBox.Text = item.Quantity.ToString();
            ItemInfiniteCheck.IsChecked = item.IsInfinite;
            ItemQuantityBox.IsEnabled = !item.IsInfinite;

            WheelSpinActionType? actionType = item.Action?.ActionType;
            ComboBoxItem? actionItem = ActionTypeBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (WheelSpinActionType?)i.Tag == actionType);
            ActionTypeBox.SelectedItem = actionItem ?? ActionTypeBox.Items.OfType<ComboBoxItem>().FirstOrDefault();

            bool showParams = actionType.HasValue && actionType.Value.HasAction();
            ActionParameterPanel.IsVisible = showParams;
            if (showParams) {
                ShowActionPanelsFor(actionType!.Value);
                switch (actionType.Value) {
                    case WheelSpinActionType.SetMultiplier:
                        ParseMultiplierParameter(item.Action!.Parameter);
                        break;
                    case WheelSpinActionType.Reroll:
                        ParseRerollParameter(item.Action!.Parameter);
                        break;
                    case WheelSpinActionType.VTubeStudio:
                        ParseVtsParameter(item.Action!.Parameter);
                        break;
                    default:
                        ActionParameterBox.Text = item.Action!.Parameter;
                        break;
                }
            }
            else {
                ActionParameterBox.Text = "";
                MultiplierAmountBox.Text = "";
                MultiplierDurationBox.Text = "";
                MultiplierTimeCheck.IsChecked = false;
                MultiplierPointsCheck.IsChecked = false;
                RerollCountBox.Text = "";
                ResetVtsBoxes();
            }
        });

        RefreshRowHighlights(clickedRow);
    }

    private void RefreshRowHighlights(Grid? clickedRow = null) {
        foreach (Grid child in ItemsStack.Children.OfType<Grid>()) {
            bool isSelected = child == clickedRow || (child.Tag is WheelItem wi && wi.Id == _selectedItem?.Id);
            child.Background = isSelected ? SelectedRowBrush : Brushes.Transparent;
        }
    }

    private void ActionType_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        bool realChange = DirtySaveGuard.Consume(sender);
        if (_suppressCount > 0 || !realChange) return;
        var selected = (ActionTypeBox.SelectedItem as ComboBoxItem)?.Tag as WheelSpinActionType?;
        bool showParams = selected.HasValue && selected.Value.HasAction();
        ActionParameterPanel.IsVisible = showParams;
        if (showParams) {
            ShowActionPanelsFor(selected!.Value);
            if (selected.Value == WheelSpinActionType.SetMultiplier
                && !(MultiplierTimeCheck.IsChecked ?? false) && !(MultiplierPointsCheck.IsChecked ?? false))
                SuppressChanges(() => MultiplierTimeCheck.IsChecked = true);
        }

        MarkPendingChanges();
    }

    private void ShowActionPanelsFor(WheelSpinActionType type) {
        bool isTime = type is WheelSpinActionType.AddTime or WheelSpinActionType.SubtractTime;
        bool isMult = type == WheelSpinActionType.SetMultiplier;
        bool isReroll = type == WheelSpinActionType.Reroll;
        bool isVts = type == WheelSpinActionType.VTubeStudio;
        TimeParamPanel.IsVisible = isTime;
        MultiplierParamPanel.IsVisible = isMult;
        RerollParamPanel.IsVisible = isReroll;
        VtsParamPanel.IsVisible = isVts;
        if (isTime) UpdateActionHint(type);
        if (isVts) {
            RefreshVtsKindDependentBoxes();
            LoadVtsTargetSuggestions();
        }
    }

    private void UpdateActionHint(WheelSpinActionType type) {
        ActionHintText.Text = type switch {
            WheelSpinActionType.AddTime => "Duration to add. e.g. \"5m\", \"300s\", \"1h30m\".",
            WheelSpinActionType.SubtractTime => "Duration to subtract. e.g. \"5m\", \"300s\", \"1h30m\".",
            _ => ""
        };
    }

    private void ParseMultiplierParameter(string parameter) {
        string[] parts = parameter.Split('|');
        if (parts.Length < 4) return;
        MultiplierAmountBox.Text = parts[0];
        MultiplierDurationBox.Text = parts[1] == "xs" ? "" : parts[1];
        MultiplierPointsCheck.IsChecked = parts[2].Equals("True", StringComparison.OrdinalIgnoreCase);
        MultiplierTimeCheck.IsChecked = parts[3].Equals("True", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildMultiplierParameter() {
        if (!double.TryParse(MultiplierAmountBox.Text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture, out double amount))
            amount = 1.0;
        TimeSpan duration = Utils.ParseDurationString((MultiplierDurationBox.Text ?? "").Trim());
        string durationStr = duration == TimeSpan.Zero ? "x" : ((int)duration.TotalSeconds).ToString();
        bool applyPoints = MultiplierPointsCheck.IsChecked ?? false;
        bool applyTime = MultiplierTimeCheck.IsChecked ?? false;
        return $"{amount.ToString(CultureInfo.InvariantCulture)}|{durationStr}s|{applyPoints}|{applyTime}";
    }

    private void Multiplier_Changed(object? sender, RoutedEventArgs e) {
        bool realChange = DirtySaveGuard.Consume(sender);
        if (_suppressCount > 0 || !realChange) return;
        MarkPendingChanges();
    }

    private void ParseRerollParameter(string parameter) {
        string[] parts = parameter.Split('|');
        RerollCountBox.Text = parts.Length >= 1 && int.TryParse(parts[0], out int c) && c >= 1 ? parts[0] : "1";
    }

    private string BuildRerollParameter() {
        if (!int.TryParse((RerollCountBox.Text ?? "").Trim(), out int count) || count < 1) count = 1;
        return count.ToString();
    }

    private void ItemInfinite_Changed(object? sender, RoutedEventArgs e) {
        bool realChange = DirtySaveGuard.Consume(sender);
        if (_suppressCount > 0 || !realChange) return;
        ItemQuantityBox.IsEnabled = !(ItemInfiniteCheck.IsChecked ?? false);
        MarkPendingChanges();
    }

    private async void AddItem_Click(object? sender, RoutedEventArgs e) {
        if (_activeWheel == null) return;
        await SaveAsync(null, null);

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        int nextIndex = (await db.WheelItems
            .Where(i => i.WheelId == _activeWheel.Id)
            .Select(i => (int?)i.Index)
            .MaxAsync() ?? 0) + 1;

        var newItem = new WheelItem {
            WheelId = _activeWheel.Id,
            Text = "New Item",
            Enabled = false,
            Index = nextIndex
        };
        db.WheelItems.Add(newItem);
        await db.SaveChangesAsync();

        await Dispatcher.UIThread.InvokeAsync(() => {
            LoadItemRows();
            SelectItem(newItem);
        });
    }

    private async void DeleteItem_Click(WheelItem item) {
        if (_selectedItem?.Id == item.Id) {
            _selectedItem = null;
            ItemDetailBorder.IsVisible = false;
        }

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        WheelItem? tracked = await db.WheelItems.FindAsync(item.Id);
        if (tracked != null)
            db.WheelItems.Remove(tracked);

        await db.SaveChangesAsync();
        await Dispatcher.UIThread.InvokeAsync(LoadItemRows);
    }

    private async void Save_Click(object? sender, RoutedEventArgs? e) {
        await SaveAsync(sender, e);
    }

    private async Task SaveAsync(object? sender, RoutedEventArgs? e) {
        if (_activeWheel == null) return;

        _activeWheel.Name = (WheelNameTextBox.Text ?? "").Trim();

        if (_selectedItem != null)
            WriteDetailToItem(_selectedItem);

        await using AppDbContext db = await _factory.CreateDbContextAsync();

        WheelSet? trackedWheel = await db.WheelSets.FindAsync(_activeWheel.Id);
        if (trackedWheel != null) {
            trackedWheel.Name = _activeWheel.Name;
            db.Update(trackedWheel);
        }

        if (_selectedItem != null) {
            WheelItem? trackedItem = await db.WheelItems
                .Include(i => i.Action)
                .FirstOrDefaultAsync(i => i.Id == _selectedItem.Id);

            if (trackedItem != null) {
                CopyItemToTracked(_selectedItem, trackedItem);

                var desiredActionType = (ActionTypeBox.SelectedItem as ComboBoxItem)?.Tag as WheelSpinActionType?;
                bool isCommandAction = desiredActionType.HasValue && desiredActionType.Value.HasAction();

                if (isCommandAction) {
                    string param = desiredActionType!.Value switch {
                        WheelSpinActionType.SetMultiplier => BuildMultiplierParameter(),
                        WheelSpinActionType.Reroll => BuildRerollParameter(),
                        WheelSpinActionType.VTubeStudio => BuildVtsParameter(),
                        _ => (ActionParameterBox.Text ?? "").Trim()
                    };

                    if (trackedItem.Action == null) {
                        db.WheelSpinActions.Add(new WheelSpinAction {
                            WheelItemId = trackedItem.Id,
                            ActionType = desiredActionType.Value,
                            Parameter = param
                        });
                    }
                    else {
                        trackedItem.Action.ActionType = desiredActionType.Value;
                        trackedItem.Action.Parameter = param;
                    }
                }
                else if (trackedItem.Action != null) {
                    db.WheelSpinActions.Remove(trackedItem.Action);
                }

                if (!IsCurrentUiActionValid(out string saveValidErr)) {
                    trackedItem.Enabled = false;
                    _selectedItem?.Enabled = false;
                    UpdateRowEnabledCheckbox(trackedItem.Id, false);
                    StatusText.Text = $"Item disabled: {saveValidErr}";
                }
                else {
                    StatusText.Text = "";
                }

                db.Update(trackedItem);
            }
        }

        await db.SaveChangesAsync();

        bool isExplicitSave = sender != null;
        if (isExplicitSave)
            await Dispatcher.UIThread.InvokeAsync(LoadItemRows);

        UpdateSaveButtonBorder(false);
        await Dispatcher.UIThread.InvokeAsync(() => SaveBtn.Content = "Saved!");
        await Task.Delay(isExplicitSave ? 1500 : 100);
        await Dispatcher.UIThread.InvokeAsync(() => SaveBtn.Content = "Save Changes");
    }

    private void WriteDetailToItem(WheelItem item) {
        item.Text = (ItemTextBox.Text ?? "").Trim();
        item.IsInfinite = ItemInfiniteCheck.IsChecked ?? false;

        if (int.TryParse(ItemWeightBox.Text, out int weight))
            item.Weight = Math.Max(1, weight);
        if (int.TryParse(ItemQuantityBox.Text, out int qty))
            item.Quantity = Math.Max(0, qty);
    }

    private static void CopyItemToTracked(WheelItem source, WheelItem tracked) {
        tracked.Text = source.Text;
        tracked.Weight = source.Weight;
        tracked.Quantity = source.Quantity;
        tracked.IsInfinite = source.IsInfinite;
        tracked.Enabled = source.Enabled;
    }

    private void SpinDelayBox_LostFocus(object? sender, RoutedEventArgs e) {
        if (!int.TryParse(SpinDelayBox.Text, out int delay)) delay = 4;
        delay = Math.Max(0, delay);
        _config.Set("WheelSpin", "SpinDelaySeconds", delay.ToString());
        _config.Save();
        SuppressChanges(() => SpinDelayBox.Text = delay.ToString());
    }

    private void RefreshSpinsOwed() {
        using AppDbContext db = _factory.CreateDbContext();
        var stored = StateValueHelper.Get<int>(db, StateKeys.WheelSpinsOwed);
        if (stored == _spinsOwed) return;
        _spinsOwed = stored;
        SuppressChanges(() => SpinsOwedBox.Text = stored.ToString());
        RaiseWheelDataChanged();
    }

    private void AdjustSpinsBoxByEvent(int val) {
        Dispatcher.UIThread.Post(() => {
            _spinsOwed = val;
            SuppressChanges(() => SpinsOwedBox.Text = val.ToString());
            RaiseWheelDataChanged();
        });
    }

    private async void SpinsOwedBox_LostFocus(object? sender, RoutedEventArgs e) {
        if (!int.TryParse(SpinsOwedBox.Text, out int newOwed)) return;
        newOwed = Math.Max(0, newOwed);
        _spinsOwed = newOwed;
        await StateValueHelper.SetAsync(_factory, StateKeys.WheelSpinsOwed, newOwed);
        SuppressChanges(() => SpinsOwedBox.Text = newOwed.ToString());
        RaiseWheelDataChanged();
    }

    private async void SpinsOwedDecrement_Click(object? sender, RoutedEventArgs e) {
        if (_spinsOwed <= 0) return;
        _spinsOwed = Math.Max(0, _spinsOwed - 1);
        await StateValueHelper.SetAsync(_factory, StateKeys.WheelSpinsOwed, _spinsOwed);
        SuppressChanges(() => SpinsOwedBox.Text = _spinsOwed.ToString());
        RaiseWheelDataChanged();
    }

    private async void SpinsOwedIncrement_Click(object? sender, RoutedEventArgs e) {
        _spinsOwed++;
        await StateValueHelper.SetAsync(_factory, StateKeys.WheelSpinsOwed, _spinsOwed);
        SuppressChanges(() => SpinsOwedBox.Text = _spinsOwed.ToString());
        RaiseWheelDataChanged();
    }

    private async void SpinWheel_Click(object? sender, RoutedEventArgs e) {
        await PerformSpinAsync();
    }

    private void OnWheelSpinRequested() {
        Dispatcher.UIThread.Post(async void () => {
            if (_activeWheel == null || _isSpinning || !SpinWheelBtn.IsEnabled) return;
            await PerformSpinAsync();
        });
    }

    private async Task PerformSpinAsync() {
        if (_activeWheel == null || _isSpinning) return;
        _isSpinning = true;
        SpinWheelBtn.IsEnabled = false;

        await using (AppDbContext db = await _factory.CreateDbContextAsync()) {
            WheelSet? tracked = await db.WheelSets.FindAsync(_activeWheel.Id);
            if (tracked != null) {
                tracked.SpinCount++;
                _activeWheel.SpinCount = tracked.SpinCount;
                await db.SaveChangesAsync();
            }
        }

        SuppressChanges(() => SpinCountBox.Text = _activeWheel.SpinCount.ToString());

        if (_spinsOwed > 0) {
            _spinsOwed--;
            await StateValueHelper.SetAsync(_factory, StateKeys.WheelSpinsOwed, _spinsOwed);
            SuppressChanges(() => SpinsOwedBox.Text = _spinsOwed.ToString());
        }

        int.TryParse(SpinDelayBox.Text, out int delay);
        delay = Math.Max(0, delay);
        WheelEvents.RaiseWheelSpinStarted(_activeWheel, delay);
        if (delay > 0)
            await Task.Delay(TimeSpan.FromSeconds(delay));

        WheelItem? item = PickWeightedItem();
        if (item != null) {
            string actionDesc = item.Action != null
                ? $"ActionType={item.Action.ActionType}, Param=\"{item.Action.Parameter}\""
                : "Action=Manual/Other";
            _logger?.LogInformation("[WheelSpin] Rolled: Id={Id}, Name=\"{Name}\", {ActionDesc}", item.Id, item.Text,
                actionDesc);
        }
        else {
            _logger?.LogWarning("[WheelSpin] No spinnable items available.");
        }

        if (item != null) {
            var histEntry = new WheelSpinHistory {
                WheelId = _activeWheel.Id,
                WheelItemId = item.Id,
                Status = item.Action?.ActionType.IsDoneImmediately() ?? false
                    ? WheelSpinHistoryStatus.Done
                    : WheelSpinHistoryStatus.Pending,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            await using AppDbContext dbH = await _factory.CreateDbContextAsync();
            dbH.WheelSpinHistories.Add(histEntry);
            await dbH.SaveChangesAsync();
            histEntry.LinkedItem = item;
            histEntry.LinkedWheel = _activeWheel;
            await Dispatcher.UIThread.InvokeAsync(() => PrependHistoryRow(histEntry));
            WheelEvents.RaiseWheelSpinResult(_activeWheel, item, histEntry, _spinsOwed);

            if (item.Action is { ActionType: WheelSpinActionType.VTubeStudio } vtsAction)
                await TryRunVtsActionAsync(histEntry, vtsAction);

            if (item.Action?.ActionType.IsDoneImmediately() ?? false)
                switch (item.Action.ActionType) {
                    case WheelSpinActionType.AddTime:
                    case WheelSpinActionType.SubtractTime: {
                        TimeSpan duration = Utils.ParseDurationString(item.Action.Parameter);
                        if (duration == TimeSpan.Zero) return;
                        SubathonCommandType cmd = item.Action.ActionType.ToCommandType();
                        SubathonEvents.RaiseSubathonEventCreated(new SubathonEvent {
                            Source = SubathonEventSource.WheelSpin,
                            EventTimestamp = DateTime.Now,
                            Command = cmd,
                            EventType = SubathonEventType.Command,
                            User = "WheelSpin",
                            Value = $"{cmd} {item.Action.Parameter}",
                            SecondsValue = duration.TotalSeconds,
                            PointsValue = 0
                        });
                        break;
                    }
                }
        }

        if (item is { IsInfinite: false }) {
            item.Quantity = Math.Max(0, item.Quantity - 1);
            await using AppDbContext dbQ = await _factory.CreateDbContextAsync();
            WheelItem? trackedQ = await dbQ.WheelItems.FindAsync(item.Id);
            if (trackedQ != null) {
                trackedQ.Quantity = item.Quantity;
                await dbQ.SaveChangesAsync();
            }

            await Dispatcher.UIThread.InvokeAsync(LoadItemRows);
        }

        if (item?.Action?.ActionType == WheelSpinActionType.Reroll)
            if (int.TryParse(item.Action.Parameter, out int rerollCount) && rerollCount >= 1) {
                _spinsOwed += rerollCount;
                await StateValueHelper.SetAsync(_factory, StateKeys.WheelSpinsOwed, _spinsOwed);
                SuppressChanges(() => SpinsOwedBox.Text = _spinsOwed.ToString());
            }

        RaiseWheelDataChanged();
        _isSpinning = false;
        await Dispatcher.UIThread.InvokeAsync(async () => {
            await Task.Delay(1500);
            SpinWheelBtn.IsEnabled = true;
        });
    }

    private WheelItem? PickWeightedItem() {
        if (_activeWheel == null) return null;
        List<WheelItem> spinnable = _activeWheel.WheelItems.Where(i => i.IsSpinnable()).ToList();
        if (spinnable.Count == 0) return null;
        int totalWeight = spinnable.Sum(i => i.Weight);
        if (totalWeight <= 0) return spinnable.First();
        int roll = Random.Shared.Next(totalWeight);
        var cumulative = 0;
        foreach (WheelItem item in spinnable) {
            cumulative += item.Weight;
            if (roll < cumulative) return item;
        }

        return spinnable.Last();
    }

    private void MarkPendingChanges() {
        if (_suppressCount > 0 || _activeWheel == null) return;
        UpdateSaveButtonBorder(true);
    }

    private void UpdateSaveButtonBorder(bool hasPendingChanges) {
        Dispatcher.UIThread.Post(() => UiHelpers.UpdateButtonPendingBorder(SaveButtonBorder, hasPendingChanges));
    }

    private void SuppressChanges(Action action) {
        _suppressCount++;
        try {
            action();
        }
        finally {
            _suppressCount--;
        }
    }

    private void AttachChangeHandlers() {
        foreach (TextBox? box in new[] {
                     ItemTextBox, ItemWeightBox, ItemQuantityBox, ActionParameterBox,
                     MultiplierAmountBox, MultiplierDurationBox, RerollCountBox,
                     VtsValueBox, VtsDurationBox, VtsAfterValueBox
                 }) {
            box.TextChanged += OnFieldChanged;
            DirtySaveGuard.Rebase(box);
        }

        VtsTargetBox.TextChanged += (s, _) => {
            if (!DirtySaveGuard.Consume(s)) return;
            MarkPendingChanges();
        };
        DirtySaveGuard.Rebase(VtsTargetBox);

        DirtySaveGuard.Rebase(ActionTypeBox);
        DirtySaveGuard.Rebase(VtsKindBox);
        DirtySaveGuard.Rebase(VtsToggleActionBox);
        DirtySaveGuard.Rebase(VtsAfterBox);
        DirtySaveGuard.Rebase(MultiplierTimeCheck);
        DirtySaveGuard.Rebase(MultiplierPointsCheck);
        DirtySaveGuard.Rebase(ItemInfiniteCheck);
    }

    private void OnFieldChanged(object? sender, TextChangedEventArgs e) {
        if (!DirtySaveGuard.Consume(sender)) return;
        if (sender is TextBox { IsFocused: false }) return;
        MarkPendingChanges();
    }

    private bool IsCurrentUiActionValid(out string error) {
        if ((ActionTypeBox.SelectedItem as ComboBoxItem)?.Tag is not WheelSpinActionType selected
            || !selected.HasAction()) {
            error = "";
            return true;
        }

        if (selected is WheelSpinActionType.AddTime or WheelSpinActionType.SubtractTime) {
            string paramText = (ActionParameterBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(paramText) || Utils.ParseDurationString(paramText) == TimeSpan.Zero) {
                error = "Duration must be non-zero (e.g. 5m, 300s, 1h30m).";
                return false;
            }

            error = "";
            return true;
        }

        if (selected == WheelSpinActionType.Reroll) {
            if (!int.TryParse((RerollCountBox.Text ?? "").Trim(), out int count) || count < 1) {
                error = "Reroll count must be at least 1";
                return false;
            }

            error = "";
            return true;
        }

        if (selected == WheelSpinActionType.VTubeStudio) {
            VTSWheelAction action = ReadVtsBoxes();
            if (!action.IsValid(out string vtsError)) {
                error = vtsError;
                return false;
            }

            error = "";
            return true;
        }

        if (selected == WheelSpinActionType.SetMultiplier) {
            if (!double.TryParse((MultiplierAmountBox.Text ?? "").Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double amt) || amt == 0) {
                error = "Multiplier amount must be a non-zero number";
                return false;
            }

            string durText = (MultiplierDurationBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(durText) || Utils.ParseDurationString(durText) == TimeSpan.Zero) {
                error = "Multiplier duration is required and must be non-zero (e.g. 30m or 1h)";
                return false;
            }

            if (!(MultiplierTimeCheck.IsChecked ?? false) && !(MultiplierPointsCheck.IsChecked ?? false)) {
                error = "At least one of Time or Points must be selected";
                return false;
            }

            error = "";
            return true;
        }

        error = "";
        return true;
    }

    private static bool IsItemActionValid(WheelItem item, out string error) {
        if (item.Action == null) {
            error = "";
            return true;
        }

        WheelSpinActionType type = item.Action.ActionType;
        string param = item.Action.Parameter;

        if (type is WheelSpinActionType.AddTime or WheelSpinActionType.SubtractTime) {
            if (string.IsNullOrEmpty(param) || Utils.ParseDurationString(param) == TimeSpan.Zero) {
                error = "Time parameter must be a non-zero duration";
                return false;
            }

            error = "";
            return true;
        }

        if (type == WheelSpinActionType.Reroll) {
            string[] parts = param.Split('|');
            if (parts.Length < 1 || !int.TryParse(parts[0], out int count) || count < 1) {
                error = "Reroll count must be at least 1";
                return false;
            }

            error = "";
            return true;
        }

        if (type == WheelSpinActionType.VTubeStudio) {
            if (!VTSWheelAction.TryParse(param, out VTSWheelAction? vtsAction)) {
                error = "VTube Studio action parameters are incomplete";
                return false;
            }

            return vtsAction.IsValid(out error);
        }

        if (type == WheelSpinActionType.SetMultiplier) {
            string[] parts = param.Split('|');
            if (parts.Length < 4) {
                error = "Multiplier parameters are incomplete";
                return false;
            }

            if (!double.TryParse(parts[0], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double amt) || amt == 0) {
                error = "Multiplier amount is missing or zero";
                return false;
            }

            string durStr = parts[1].EndsWith("s") ? parts[1][..^1] : parts[1];
            if (parts[1] == "xs" || string.IsNullOrEmpty(durStr) || !int.TryParse(durStr, out int ds) || ds <= 0) {
                error = "Multiplier duration is required and must be non-zero";
                return false;
            }

            bool applyPoints = parts[2].Equals("True", StringComparison.OrdinalIgnoreCase);
            bool applyTime = parts[3].Equals("True", StringComparison.OrdinalIgnoreCase);
            if (!applyPoints && !applyTime) {
                error = "At least one of Time or Points must be selected";
                return false;
            }

            error = "";
            return true;
        }

        error = "";
        return true;
    }

    private void UpdateRowEnabledCheckbox(Guid itemId, bool enabled) {
        Grid? row = ItemsStack.Children.OfType<Grid>()
            .FirstOrDefault(g => g.Tag is WheelItem wi && wi.Id == itemId);
        CheckBox? cb = row?.Children.OfType<CheckBox>().FirstOrDefault();
        if (cb != null) SuppressChanges(() => cb.IsChecked = enabled);
    }

    private async Task LoadHistoryAsync(bool append = false) {
        if (_historyLoading) return;
        _historyLoading = true;
        try {
            if (!append) {
                _historyOffset = 0;
                await Dispatcher.UIThread.InvokeAsync(() => HistoryStack.Children.Clear());
            }

            if (_activeWheel == null) return;

            await using AppDbContext db = await _factory.CreateDbContextAsync();
            List<WheelSpinHistory> entries = await db.WheelSpinHistories
                .Include(h => h.LinkedItem).ThenInclude(i => i!.Action)
                .Where(h => h.WheelId == _activeWheel.Id
                            && (_historyFilter == null || h.Status == _historyFilter))
                .OrderByDescending(h => h.CreatedAt)
                .Skip(_historyOffset)
                .Take(HistoryPageSize)
                .AsNoTracking()
                .ToListAsync();

            _historyOffset += entries.Count;

            await Dispatcher.UIThread.InvokeAsync(() => {
                foreach (WheelSpinHistory entry in entries)
                    HistoryStack.Children.Add(BuildHistoryRow(entry));
            });
        }
        finally {
            _historyLoading = false;
        }
    }

    private void PrependHistoryRow(WheelSpinHistory h) {
        _historyOffset++;
        HistoryStack.Children.Insert(0, BuildHistoryRow(h));
    }

    private Grid BuildHistoryRow(WheelSpinHistory h) {
        var row = new Grid {
            Margin = new Thickness(2, 1, 2, 1),
            MinHeight = 26,
            Tag = h,
            ColumnDefinitions = new ColumnDefinitions("78,1.7*,*,70,80")
        };

        var tsLabel = new TextBlock {
            Text = h.CreatedAt.ToString("MM/dd HH:mm"),
            FontSize = 10,
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(tsLabel, h.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));

        var itemLabel = new TextBlock {
            Text = h.LinkedItem?.Text ?? "(deleted)",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0, 4, 0)
        };
        ToolTip.SetTip(itemLabel, h.LinkedItem?.Text);

        var statusLabel = new TextBlock {
            Text = h.Status.ToString(),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = HistoryStatusBrush(h.Status),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var hoverBtns = new StackPanel {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 12, 0)
        };

        void AddBtn(WheelSpinHistoryStatus target, string glyph, string tip) {
            var btn = new Button {
                Content = new SymIcon { Glyph = glyph },
                Width = 22, Height = 22,
                Padding = new Thickness(1),
                Margin = new Thickness(1, 0, 0, 0),
                IsEnabled = h.Status != target,
                Tag = target
            };
            ToolTip.SetTip(btn, tip);
            btn.Click += async (_, _) => await SetHistoryStatus(h, target);
            hoverBtns.Children.Add(btn);
        }

        AddBtn(WheelSpinHistoryStatus.Done, "Checkmark16", "Mark Done");
        AddBtn(WheelSpinHistoryStatus.Pending, "Clock16", "Mark Pending");
        AddBtn(WheelSpinHistoryStatus.Cancelled, "Dismiss16", "Mark Cancelled");

        WheelSpinActionType? actionType = h.LinkedItem?.Action?.ActionType;
        bool hasPlayBtn = h.Status == WheelSpinHistoryStatus.Pending
                          && actionType.HasValue && actionType.Value.HasPlayAction();

        string actionStr = DescribeHistoryAction(h.LinkedItem?.Action);

        var actionCell = new Grid {
            Margin = new Thickness(4, 0, 2, 0),
            ColumnDefinitions = hasPlayBtn ? new ColumnDefinitions("*,Auto") : new ColumnDefinitions("*")
        };

        var actionLabel = new TextBlock {
            Text = actionStr,
            FontSize = 10,
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ToolTip.SetTip(actionLabel, actionStr);

        Grid.SetColumn(actionLabel, 0);
        actionCell.Children.Add(actionLabel);

        if (hasPlayBtn) {
            bool isMultiplier = actionType == WheelSpinActionType.SetMultiplier;
            bool isVts = actionType == WheelSpinActionType.VTubeStudio;
            var playBtn = new Button {
                Content = new SymIcon { Glyph = "Play16" },
                Width = 20, Height = 20,
                Padding = new Thickness(1),
                Margin = new Thickness(3, 0, 0, 0),
                IsEnabled = !isMultiplier || !_multiplierActive,
                Tag = isMultiplier ? "MultiplierPlayBtn" : null
            };
            ToolTip.SetTip(playBtn, isMultiplier
                ? "Apply multiplier (disabled while one is active)"
                : isVts
                    ? "Run the VTube Studio action (needs VTube Studio connected)"
                    : "Apply command");
            playBtn.Click += async (_, _) => await ExecuteHistoryAction(h, playBtn);
            Grid.SetColumn(playBtn, 1);
            actionCell.Children.Add(playBtn);
        }

        Grid.SetColumn(tsLabel, 0);
        Grid.SetColumn(itemLabel, 1);
        Grid.SetColumn(actionCell, 2);
        Grid.SetColumn(statusLabel, 3);
        Grid.SetColumn(hoverBtns, 4);

        row.Children.Add(tsLabel);
        row.Children.Add(itemLabel);
        row.Children.Add(actionCell);
        row.Children.Add(statusLabel);
        row.Children.Add(hoverBtns);

        return row;
    }

    private static IBrush HistoryStatusBrush(WheelSpinHistoryStatus status) {
        return status switch {
            WheelSpinHistoryStatus.Done => Brushes.MediumSeaGreen,
            WheelSpinHistoryStatus.Pending => Brushes.CornflowerBlue,
            WheelSpinHistoryStatus.Cancelled => Brushes.IndianRed,
            _ => Brushes.Gray
        };
    }

    private async Task SetHistoryStatus(WheelSpinHistory h, WheelSpinHistoryStatus newStatus) {
        h.Status = newStatus;
        h.UpdatedAt = DateTime.Now;

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        WheelSpinHistory? tracked = await db.WheelSpinHistories.FindAsync(h.Id);
        if (tracked == null) return;
        tracked.Status = newStatus;
        tracked.UpdatedAt = h.UpdatedAt;
        await db.SaveChangesAsync();

        h.LinkedWheel ??= _activeWheel;
        WheelEvents.RaiseWheelSpinStatusChanged(h, _spinsOwed);
    }

    private void OnWheelSpinStatusChanged(WheelSpinHistory history, int spinsOwed) {
        Dispatcher.UIThread.Post(() => {
            Grid? row = HistoryStack.Children.OfType<Grid>()
                .FirstOrDefault(g => g.Tag is WheelSpinHistory h && h.Id == history.Id);
            if (row == null) return;

            if (row.Tag is WheelSpinHistory rowHistory)
                rowHistory.Status = history.Status;

            TextBlock? statusLabel = row.Children.OfType<TextBlock>()
                .FirstOrDefault(c => Grid.GetColumn(c) == 3);
            StackPanel? hoverBtns = row.Children.OfType<StackPanel>()
                .FirstOrDefault(c => Grid.GetColumn(c) == 4);

            if (statusLabel != null) {
                statusLabel.Text = history.Status.ToString();
                statusLabel.Foreground = HistoryStatusBrush(history.Status);
            }

            if (hoverBtns != null)
                foreach (Button btn in hoverBtns.Children.OfType<Button>())
                    btn.IsEnabled = btn.Tag is WheelSpinHistoryStatus s && s != history.Status;

            Grid? actionCell = row.Children.OfType<Grid>().FirstOrDefault(c => Grid.GetColumn(c) == 2);
            if (actionCell == null) return;
            foreach (Button playBtn in actionCell.Children.OfType<Button>())
                playBtn.IsVisible = history.Status == WheelSpinHistoryStatus.Pending;
        });
    }

    private void RaiseWheelDataChanged() {
        if (_activeWheel == null) return;
        WheelEvents.RaiseWheelDataChanged(_activeWheel, _spinsOwed);
    }

    private void OnSubathonDataUpdate(SubathonData data, DateTime _) {
        _multiplierActive = data.Multiplier?.IsRunning() ?? false;

        if (Interlocked.CompareExchange(ref _multiplierRefreshQueued, 1, 0) != 0) return;
        Dispatcher.UIThread.Post(() => {
            Interlocked.Exchange(ref _multiplierRefreshQueued, 0);
            RefreshMultiplierButtons(_multiplierActive);
        }, DispatcherPriority.Background);
    }

    private void RefreshMultiplierButtons(bool multiplierActive) {
        foreach (Grid row in HistoryStack.Children.OfType<Grid>()) {
            if (row.Tag is not WheelSpinHistory h) continue;
            bool isPending = h.Status == WheelSpinHistoryStatus.Pending;
            foreach (Grid cell in row.Children.OfType<Grid>())
            foreach (Button btn in cell.Children.OfType<Button>())
                if (btn.Tag?.ToString() == "MultiplierPlayBtn")
                    btn.IsEnabled = isPending && !multiplierActive;
        }
    }

    private async Task ExecuteHistoryAction(WheelSpinHistory h, Button playBtn) {
        await Dispatcher.UIThread.InvokeAsync(() => {
            playBtn.IsEnabled = false;
            playBtn.IsVisible = false;
        });

        WheelSpinAction? action = h.LinkedItem?.Action;
        if (action == null) return;

        switch (action.ActionType) {
            case WheelSpinActionType.AddTime:
            case WheelSpinActionType.SubtractTime: {
                TimeSpan duration = Utils.ParseDurationString(action.Parameter);
                if (duration == TimeSpan.Zero) return;
                SubathonCommandType cmd = action.ActionType.ToCommandType();
                SubathonEvents.RaiseSubathonEventCreated(new SubathonEvent {
                    Source = SubathonEventSource.WheelSpin,
                    EventTimestamp = DateTime.Now,
                    Command = cmd,
                    EventType = SubathonEventType.Command,
                    User = "WheelSpin",
                    Value = $"{cmd} {action.Parameter}",
                    SecondsValue = duration.TotalSeconds,
                    PointsValue = 0
                });
                break;
            }
            case WheelSpinActionType.SetMultiplier: {
                SubathonEvents.RaiseSubathonEventCreated(new SubathonEvent {
                    Source = SubathonEventSource.WheelSpin,
                    EventTimestamp = DateTime.Now,
                    Command = SubathonCommandType.SetMultiplier,
                    EventType = SubathonEventType.Command,
                    User = "WheelSpin",
                    Value = action.Parameter
                });

                _multiplierActive = true;
                await Dispatcher.UIThread.InvokeAsync(() => RefreshMultiplierButtons(true));
                break;
            }
            case WheelSpinActionType.VTubeStudio: {
                bool ran = await TryRunVtsActionAsync(h, action);
                if (!ran)
                    await Dispatcher.UIThread.InvokeAsync(() => {
                        playBtn.IsVisible = true;
                        playBtn.IsEnabled = true;
                    });
                return;
            }
        }

        if (h.Status != WheelSpinHistoryStatus.Done)
            await SetHistoryStatus(h, WheelSpinHistoryStatus.Done);
    }

    private void HistoryFilter_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        if (_activeWheel == null) return;
        _historyFilter = (HistoryFilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch {
            "Pending" => WheelSpinHistoryStatus.Pending,
            "Done" => WheelSpinHistoryStatus.Done,
            "Cancelled" => WheelSpinHistoryStatus.Cancelled,
            _ => null
        };
        _ = LoadHistoryAsync();
    }

    private void HistoryScroller_ScrollChanged(object? sender, ScrollChangedEventArgs e) {
        if (_historyLoading) return;
        double scrollable = HistoryScroller.Extent.Height - HistoryScroller.Viewport.Height;
        if (scrollable > 0 && scrollable - HistoryScroller.Offset.Y < 100)
            _ = LoadHistoryAsync(true);
    }

    private async void ExportHistory_Click(object? sender, RoutedEventArgs e) {
        await using AppDbContext db = await _factory.CreateDbContextAsync();
        List<WheelSpinHistory> histories = await db.WheelSpinHistories
            .Include(h => h.LinkedWheel)
            .Include(h => h.LinkedItem).ThenInclude(i => i!.Action)
            .OrderByDescending(h => h.CreatedAt)
            .AsNoTracking()
            .ToListAsync();

        string exportDir = Path.Combine(Config.DataFolder, "exports");
        Directory.CreateDirectory(exportDir);
        string filepath = Path.Combine(exportDir, $"wheel-history-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        var sb = new StringBuilder();
        sb.AppendLine("Id,Wheel Id,Wheel Name,Item Id,Item Text,Action Type,Parameter,Status,Created At,Updated At");
        foreach (WheelSpinHistory h in histories)
            sb.AppendLine(string.Join(",",
                h.Id,
                h.WheelId,
                Utils.EscapeCsv(h.LinkedWheel?.Name ?? ""),
                h.WheelItemId,
                Utils.EscapeCsv(h.LinkedItem?.Text ?? ""),
                Utils.EscapeCsv(h.LinkedItem?.Action?.ActionType.ToString() ?? "Manual"),
                Utils.EscapeCsv(h.LinkedItem?.Action?.Parameter ?? ""),
                Utils.EscapeCsv(h.Status.ToString()),
                h.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                h.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss")));

        await File.WriteAllTextAsync(filepath, sb.ToString(), Encoding.UTF8);

        try {
            UiHelpers.OpenFolder(exportDir);
        }
        catch {
            /**/
        }
    }

    private async void DeleteAllSpinHistory_Click(object? sender, RoutedEventArgs e) {
        if (_activeWheel == null) return;
        Guid wheelId = _activeWheel.Id;

        var dialog = new FAContentDialog {
            Title = "Delete Spin History",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            Content = new TextBlock {
                Text = $"Are you sure you want to delete all spin history for \"{_activeWheel.Name}\"?",
                TextWrapping = TextWrapping.Wrap,
                Width = 320,
                Margin = new Thickness(4)
            }
        };

        if (await dialog.ShowAsync() != FAContentDialogResult.Primary) return;

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        await db.WheelSpinHistories.Where(h => h.WheelId == wheelId).ExecuteDeleteAsync();

        await Dispatcher.UIThread.InvokeAsync(async () => await LoadHistoryAsync());
    }

    private async void ExportWheel_Click(object? sender, RoutedEventArgs e) {
        if (_activeWheel == null) return;

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        WheelSet? wheel = await db.WheelSets
            .Include(w => w.WheelItems).ThenInclude(i => i.Action)
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == _activeWheel.Id);
        if (wheel == null) return;

        string exportDir = Path.Combine(Config.DataFolder, "exports");
        Directory.CreateDirectory(exportDir);

        string safeName = SafeFileName.Sanitize(wheel.Name, string.Empty, "wheel");
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string filepath = Path.Combine(exportDir, $"{safeName}-{timestamp}.csv");

        var sb = new StringBuilder();
        sb.AppendLine("Text,Weight,Quantity,Infinite,Enabled,ActionType,ActionParameter");
        foreach (WheelItem item in wheel.WheelItems.OrderBy(i => i.Index))
            sb.AppendLine(string.Join(",",
                Utils.EscapeCsv(item.Text),
                item.Weight,
                item.Quantity,
                item.IsInfinite,
                item.Enabled,
                item.Action?.ActionType.ToString() ?? $"{WheelSpinActionType.Manual}",
                Utils.EscapeCsv(item.Action?.Parameter ?? "")));

        await File.WriteAllTextAsync(filepath, sb.ToString(), Encoding.UTF8);

        try {
            UiHelpers.OpenFolder(exportDir);
        }
        catch {
            /**/
        }
    }

    private async void ImportWheel_Click(object? sender, RoutedEventArgs e) {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        IReadOnlyList<IStorageFile> picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = "Import Wheel",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("CSV Files") { Patterns = new[] { "*.csv" } } }
        });
        if (picked.Count == 0) return;
        string filePath = picked[0].Path.LocalPath;

        string[] lines;
        try {
            lines = await File.ReadAllLinesAsync(filePath, Encoding.UTF8);
        }
        catch {
            await ShowInvalidWheelCsvPopup();
            return;
        }

        if (lines.Length < 1) {
            await ShowInvalidWheelCsvPopup();
            return;
        }

        string[] headerCols = ParseWheelCsvLine(lines[0]);
        if (headerCols.Length < 5) {
            await ShowInvalidWheelCsvPopup();
            return;
        }

        var items =
            new List<(string Text, int Weight, int Qty, bool Infinite, bool Enabled, WheelSpinActionType ActionType,
                string ActionParam)>();
        for (var i = 1; i < lines.Length; i++) {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            string[] cols = ParseWheelCsvLine(lines[i]);
            if (cols.Length < 5
                || !int.TryParse(cols[1].Trim(), out int weight)
                || !int.TryParse(cols[2].Trim(), out int qty)
                || !bool.TryParse(cols[3].Trim(), out bool infinite)
                || !bool.TryParse(cols[4].Trim(), out bool enabled)) {
                await ShowInvalidWheelCsvPopup();
                return;
            }

            string actionTypeStr = cols.Length > 5 ? cols[5].Trim() : "";
            var actionType = WheelSpinActionType.Manual;
            if (!string.IsNullOrEmpty(actionTypeStr) && !Enum.TryParse(actionTypeStr, out actionType)) {
                await ShowInvalidWheelCsvPopup();
                return;
            }

            string actionParam = cols.Length > 6 ? cols[6] : "";
            items.Add((cols[0], weight, qty, infinite, enabled, actionType, actionParam));
        }

        string wheelName = Path.GetFileNameWithoutExtension(filePath);

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        foreach (WheelSet w in db.WheelSets)
            w.IsActive = false;

        var newWheel = new WheelSet { Name = wheelName, IsActive = true };
        db.WheelSets.Add(newWheel);
        await db.SaveChangesAsync();

        for (var idx = 0; idx < items.Count; idx++) {
            (string text, int weight, int qty, bool infinite, bool enabled, WheelSpinActionType actionType,
                string actionParam) = items[idx];
            var newItem = new WheelItem {
                Text = text,
                Weight = weight,
                Quantity = qty,
                IsInfinite = infinite,
                Enabled = enabled,
                Index = idx,
                WheelId = newWheel.Id
            };
            db.WheelItems.Add(newItem);
            await db.SaveChangesAsync();

            db.WheelSpinActions.Add(new WheelSpinAction {
                ActionType = actionType,
                Parameter = actionParam,
                WheelItemId = newItem.Id
            });
        }

        await db.SaveChangesAsync();

        LoadActiveWheel();
    }

    private static string[] ParseWheelCsvLine(string line) {
        var result = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++) {
            char c = line[i];
            if (inQuotes)
                switch (c) {
                    case '"' when i + 1 < line.Length && line[i + 1] == '"':
                        field.Append('"');
                        i++;
                        break;
                    case '"':
                        inQuotes = false;
                        break;
                    default:
                        field.Append(c);
                        break;
                }
            else
                switch (c) {
                    case '"':
                        inQuotes = true;
                        break;
                    case ',':
                        result.Add(field.ToString());
                        field.Clear();
                        break;
                    default:
                        field.Append(c);
                        break;
                }
        }

        result.Add(field.ToString());
        return result.ToArray();
    }

    private static async Task ShowInvalidWheelCsvPopup() {
        var dialog = new FAContentDialog {
            Title = "Invalid CSV",
            CloseButtonText = "OK",
            Content = new TextBlock {
                Text = "The selected file is not a valid wheel CSV and could not be imported.",
                TextWrapping = TextWrapping.Wrap,
                Width = 300,
                Margin = new Thickness(4)
            }
        };
        await dialog.ShowAsync();
    }

    // VTS stuff, maybe move out?
    private void PopulateVtsComboBoxes() {
        VtsKindBox.Items.Clear();
        VtsKindBox.Items.Add(new ComboBoxItem { Content = "Expression", Tag = VtsTargetKind.Expression });
        VtsKindBox.Items.Add(new ComboBoxItem { Content = "Parameter", Tag = VtsTargetKind.Parameter });
        VtsKindBox.Items.Add(new ComboBoxItem { Content = "Hotkey", Tag = VtsTargetKind.Hotkey });
        VtsKindBox.SelectedIndex = 0;

        VtsToggleActionBox.Items.Clear();
        foreach (VtsToggleAction a in new[] { VtsToggleAction.On, VtsToggleAction.Off, VtsToggleAction.Toggle })
            VtsToggleActionBox.Items.Add(new ComboBoxItem { Content = a.ToString(), Tag = a });
        VtsToggleActionBox.SelectedIndex = 0;

        RefreshVtsKindDependentBoxes();
    }

    private void VtsKind_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        bool realChange = DirtySaveGuard.Consume(sender);
        RefreshVtsKindDependentBoxes();
        LoadVtsTargetSuggestions();
        if (_suppressCount > 0 || !realChange) return;
        MarkPendingChanges();
    }

    private void VtsAfter_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        bool realChange = DirtySaveGuard.Consume(sender);
        RefreshVtsAfterValueVisibility();
        UpdateVtsHint();
        if (_suppressCount > 0 || !realChange) return;
        MarkPendingChanges();
    }

    private void RefreshVtsKindDependentBoxes() {
        VtsTargetKind kind = SelectedVtsKind;

        SuppressChanges(() => {
            VtsToggleActionPanel.IsVisible = kind == VtsTargetKind.Expression;
            VtsValuePanel.IsVisible = kind == VtsTargetKind.Parameter;

            object? previous = (VtsAfterBox.SelectedItem as ComboBoxItem)?.Tag;
            VtsAfterBox.Items.Clear();

            switch (kind) {
                case VtsTargetKind.Expression:
                    foreach (VtsToggleAction a in Enum.GetValues<VtsToggleAction>())
                        VtsAfterBox.Items.Add(new ComboBoxItem { Content = LabelFor(a), Tag = a });
                    break;
                case VtsTargetKind.Parameter:
                    foreach (VtsParameterAfterAction a in Enum.GetValues<VtsParameterAfterAction>())
                        VtsAfterBox.Items.Add(new ComboBoxItem { Content = LabelFor(a), Tag = a });
                    break;
                case VtsTargetKind.Hotkey:
                    foreach (VtsHotkeyAfterAction a in Enum.GetValues<VtsHotkeyAfterAction>())
                        VtsAfterBox.Items.Add(new ComboBoxItem { Content = LabelFor(a), Tag = a });
                    break;
            }

            ComboBoxItem? restored = VtsAfterBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => Equals(i.Tag, previous));
            VtsAfterBox.SelectedItem = restored ?? VtsAfterBox.Items.OfType<ComboBoxItem>().FirstOrDefault();

            VtsTargetBox.PlaceholderText = kind switch {
                VtsTargetKind.Expression => "e.g. cat_ears.exp3.json",
                VtsTargetKind.Parameter => "e.g. EyeOpenRight or custom parameter",
                _ => "hotkey name or id"
            };
        });

        RefreshVtsAfterValueVisibility();
        UpdateVtsHint();
    }

    private void RefreshVtsAfterValueVisibility() {
        VtsAfterValuePanel.IsVisible = SelectedVtsKind == VtsTargetKind.Parameter
                                       && (VtsAfterBox.SelectedItem as ComboBoxItem)?.Tag
                                       as VtsParameterAfterAction? == VtsParameterAfterAction.SetNewValue;
    }

    private static string LabelFor(VtsToggleAction action) {
        return action switch {
            VtsToggleAction.DoNothing => "Do Nothing",
            _ => action.ToString()
        };
    }

    private static string LabelFor(VtsParameterAfterAction action) {
        return action switch {
            VtsParameterAfterAction.DoNothing => "Do Nothing / Release",
            VtsParameterAfterAction.ResetToOriginal => "Reset to Original",
            VtsParameterAfterAction.SetNewValue => "Change to New Value",
            _ => action.ToString()
        };
    }

    private static string LabelFor(VtsHotkeyAfterAction action) {
        return action switch {
            VtsHotkeyAfterAction.DoNothing => "Do Nothing",
            VtsHotkeyAfterAction.TriggerAgain => "Trigger Again",
            _ => action.ToString()
        };
    }

    private void LoadVtsTargetSuggestions() {
        VTSService vts = ServiceManager.VTubeStudio;
        VtsTargetKind kind = SelectedVtsKind;

        if (!vts.Connected) {
            VtsTargetBox.ItemsSource = null;
            VtsConnectionHint.Text = "VTube Studio not connected - type the value by hand";
            return;
        }

        List<string> suggestions = kind switch {
            VtsTargetKind.Expression => vts.CachedExpressions.Select(e => e.File).ToList(),
            VtsTargetKind.Parameter => vts.CachedParameters.Select(pa => pa.Name).ToList(),
            VtsTargetKind.Hotkey => BuildHotkeySuggestions(vts.CachedHotkeys),
            _ => []
        };

        VtsTargetBox.ItemsSource = suggestions;
        VtsConnectionHint.Text = suggestions.Count > 0
            ? $"{suggestions.Count} available on \"{vts.CurrentModelName ?? "current model"}\""
            : "Nothing found on current model";
    }

    private List<string> BuildHotkeySuggestions(IReadOnlyList<VtsHotkey> hotkeys) {
        _vtsHotkeyIdsByDisplay.Clear();

        HashSet<string> duplicates = hotkeys
            .GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var display = new List<string>(hotkeys.Count);
        foreach (VtsHotkey hotkey in hotkeys) {
            string name = string.IsNullOrWhiteSpace(hotkey.Name) ? hotkey.Id : hotkey.Name;
            string label = duplicates.Contains(name) && hotkey.Id.Length >= 6
                ? $"{name} ({hotkey.Id[^6..]})"
                : name;
            _vtsHotkeyIdsByDisplay[label] = hotkey.Id;
            display.Add(label);
        }

        return display;
    }

    private string DisplayForHotkeyId(string hotkeyId) {
        foreach (KeyValuePair<string, string> pair in _vtsHotkeyIdsByDisplay)
            if (string.Equals(pair.Value, hotkeyId, StringComparison.OrdinalIgnoreCase))
                return pair.Key;

        return ResolveHotkeyName(hotkeyId) ?? hotkeyId;
    }

    private static string? ResolveHotkeyName(string? hotkeyId) {
        if (string.IsNullOrWhiteSpace(hotkeyId)) return null;
        return ServiceManager.VTubeStudio.CachedHotkeys
            .FirstOrDefault(h => string.Equals(h.Id, hotkeyId, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    private string ResolveHotkeyTargetFromBox() {
        string text = (VtsTargetBox.Text ?? "").Trim();
        if (text.Length == 0) return "";
        if (_vtsHotkeyIdsByDisplay.TryGetValue(text, out string? mapped)) return mapped;

        VtsHotkey? byName = ServiceManager.VTubeStudio.CachedHotkeys
            .FirstOrDefault(h => string.Equals(h.Name, text, StringComparison.OrdinalIgnoreCase));
        return byName?.Id ?? text;
    }

    private void OnVtsModelDataChanged() {
        Dispatcher.UIThread.Post(() => {
            if (!VtsParamPanel.IsVisible) return;
            SuppressChanges(LoadVtsTargetSuggestions);
            UpdateVtsHint();
        });
    }

    private void OnVtsConnectionUpdated(IntegrationConnection connection) {
        if (connection.Source != SubathonEventSource.VTubeStudio) return;
        OnVtsModelDataChanged();
    }

    private async void VtsRefreshTargets_Click(object? sender, RoutedEventArgs e) {
        VTSService vts = ServiceManager.VTubeStudio;
        if (!vts.Connected) {
            LoadVtsTargetSuggestions();
            VtsHintText.Text = "Connect VTubeStudio under Settings -> External Software";
            return;
        }

        await vts.RefreshAsync();
        await Dispatcher.UIThread.InvokeAsync(() => {
            LoadVtsTargetSuggestions();
            UpdateVtsHint();
        });
    }

    private void UpdateVtsHint() {
        VTSWheelAction action = ReadVtsBoxes();

        if (!action.IsValid(out string error)) {
            VtsHintText.Text = error;
            return;
        }

        string summary = action.Describe();
        if (action.Kind == VtsTargetKind.Hotkey && ResolveHotkeyName(action.Target) is { } hotkeyName)
            summary = $"\"{hotkeyName}\" {summary[action.Target.Length..].TrimStart()}";

        VtsHintText.Text = action.HasRevert
            ? summary
            : $"{summary} (no duration set)";
    }

    private VTSWheelAction ReadVtsBoxes() {
        VtsTargetKind kind = SelectedVtsKind;
        var action = new VTSWheelAction {
            Kind = kind,
            Target = kind == VtsTargetKind.Hotkey
                ? ResolveHotkeyTargetFromBox()
                : (VtsTargetBox.Text ?? "").Trim(),
            Duration = Utils.ParseDurationString((VtsDurationBox.Text ?? "").Trim())
        };

        switch (kind) {
            case VtsTargetKind.Expression:
                action.ToggleAction = (VtsToggleActionBox.SelectedItem as ComboBoxItem)?.Tag as VtsToggleAction?
                                      ?? VtsToggleAction.On;
                action.AfterToggle = (VtsAfterBox.SelectedItem as ComboBoxItem)?.Tag as VtsToggleAction?
                                     ?? VtsToggleAction.DoNothing;
                break;
            case VtsTargetKind.Parameter:
                action.Value = ParseDoubleOrZero(VtsValueBox.Text);
                action.AfterParameter = (VtsAfterBox.SelectedItem as ComboBoxItem)?.Tag as VtsParameterAfterAction?
                                        ?? VtsParameterAfterAction.DoNothing;
                action.AfterValue = ParseDoubleOrZero(VtsAfterValueBox.Text);
                break;
            case VtsTargetKind.Hotkey:
                action.AfterHotkey = (VtsAfterBox.SelectedItem as ComboBoxItem)?.Tag as VtsHotkeyAfterAction?
                                     ?? VtsHotkeyAfterAction.DoNothing;
                break;
        }

        return action;
    }

    private static double ParseDoubleOrZero(string? text) {
        return double.TryParse((text ?? "").Trim(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v
            : 0d;
    }

    private string BuildVtsParameter() {
        return ReadVtsBoxes().ToParameterString();
    }

    private void ParseVtsParameter(string parameter) {
        if (!VTSWheelAction.TryParse(parameter, out VTSWheelAction? action)) {
            ResetVtsBoxes();
            return;
        }

        SuppressChanges(() => {
            ComboBoxItem? kindItem = VtsKindBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (VtsTargetKind?)i.Tag == action.Kind);
            VtsKindBox.SelectedItem = kindItem ?? VtsKindBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        });

        RefreshVtsKindDependentBoxes();
        LoadVtsTargetSuggestions();

        SuppressChanges(() => {
            VtsTargetBox.Text = action.Kind == VtsTargetKind.Hotkey
                ? DisplayForHotkeyId(action.Target)
                : action.Target;
            VtsDurationBox.Text = action.Duration > TimeSpan.Zero
                ? $"{(int)action.Duration.TotalSeconds}s"
                : "";
            VtsValueBox.Text = action.Kind == VtsTargetKind.Parameter
                ? action.Value.ToString(CultureInfo.InvariantCulture)
                : "";
            VtsAfterValueBox.Text = action.Kind == VtsTargetKind.Parameter
                ? action.AfterValue.ToString(CultureInfo.InvariantCulture)
                : "";

            ComboBoxItem? toggleItem = VtsToggleActionBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (VtsToggleAction?)i.Tag == action.ToggleAction);
            if (toggleItem != null) VtsToggleActionBox.SelectedItem = toggleItem;

            object afterTag = action.Kind switch {
                VtsTargetKind.Expression => action.AfterToggle,
                VtsTargetKind.Parameter => action.AfterParameter,
                _ => action.AfterHotkey
            };
            ComboBoxItem? afterItem = VtsAfterBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => Equals(i.Tag, afterTag));
            if (afterItem != null) VtsAfterBox.SelectedItem = afterItem;
        });

        RefreshVtsAfterValueVisibility();
        UpdateVtsHint();
    }

    private void ResetVtsBoxes() {
        SuppressChanges(() => {
            VtsKindBox.SelectedIndex = 0;
            VtsTargetBox.Text = "";
            VtsValueBox.Text = "";
            VtsDurationBox.Text = "";
            VtsAfterValueBox.Text = "";
            if (VtsToggleActionBox.Items.Count > 0) VtsToggleActionBox.SelectedIndex = 0;
        });

        RefreshVtsKindDependentBoxes();
    }

    private async Task<bool> TryRunVtsActionAsync(WheelSpinHistory history, WheelSpinAction action) {
        if (!VTSWheelAction.TryParse(action.Parameter, out VTSWheelAction? parsed)) {
            _logger?.LogWarning("[WheelSpin] VTubeStudio action parameter could not be parsed: \"{Parameter}\"",
                action.Parameter);
            return false;
        }

        VTSService vts = ServiceManager.VTubeStudio;
        if (!vts.Connected) {
            _logger?.LogWarning(
                "[WheelSpin] VTubeStudio is not connected; leaving \"{Item}\" pending", history.LinkedItem?.Text ?? "item");
            return false;
        }

        bool ok;
        try {
            ok = await vts.ExecuteWheelActionAsync(parsed);
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[WheelSpin] VTubeStudio action failed");
            return false;
        }

        if (!ok) {
            _logger?.LogWarning("[WheelSpin] VTubeStudio rejected the action; leaving the spin pending");
            return false;
        }

        _logger?.LogDebug("[WheelSpin] VTubeStudio action applied: {Summary}", parsed.Describe());
        if (history.Status != WheelSpinHistoryStatus.Done)
            await SetHistoryStatus(history, WheelSpinHistoryStatus.Done);
        return true;
    }

    private static string DescribeHistoryAction(WheelSpinAction? action) {
        if (action == null) return "Manual";
        if (action.ActionType == WheelSpinActionType.VTubeStudio
            && VTSWheelAction.TryParse(action.Parameter, out VTSWheelAction? parsed)) {
            string described = parsed.Describe();
            if (parsed.Kind == VtsTargetKind.Hotkey && ResolveHotkeyName(parsed.Target) is { } hotkeyName)
                described = $"\"{hotkeyName}\" {described[parsed.Target.Length..].TrimStart()}";

            return $"VTubeStudio: {described}";
        }

        return $"{action.ActionType}: {action.Parameter}";
    }
}