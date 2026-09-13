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
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.UI.Controls;
using SubathonManager.UI.Services;
using SubathonManager.UI.UiUtils;

namespace SubathonManager.UI.Views.WheelSpin;

public partial class WheelTriggerEditor : UserControl {
    private const int HistoryPageSize = 20;

    private static readonly SolidColorBrush SelectedRowBrush = new(Color.FromArgb(30, 100, 149, 237));

    private static readonly HashSet<SubathonEventSubType> ValidSubTypes = [
        SubathonEventSubType.SubLike,
        SubathonEventSubType.GiftSubLike,
        SubathonEventSubType.TokenLike,
        SubathonEventSubType.DonationLike,
        SubathonEventSubType.OrderLike
    ];

    private static readonly HashSet<SubathonEventType> TwitchTierTypes = [
        SubathonEventType.TwitchSub,
        SubathonEventType.TwitchGiftSub
    ];

    private static readonly HashSet<SubathonEventType> PicartoTierTypes = [
        SubathonEventType.PicartoSub,
        SubathonEventType.PicartoGiftSub
    ];

    private readonly IConfig _config;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private bool _historyLoading;
    private int _historyOffset;
    private bool _initialized;
    private bool _isDirty;
    private bool _isNewTrigger;
    private object? _selectedEventTag;
    private WheelSpinTrigger? _selectedTrigger;
    private int _suppressCount;

    public WheelTriggerEditor() {
        _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        _config = AppServices.Provider.GetRequiredService<IConfig>();
        InitializeComponent();

        Loaded += (_, _) => {
            LoadCurrencies();
            if (!_initialized) {
                WireDirtyHandlers();
                _initialized = true;
            }

            Dispatcher.UIThread.Post(LoadTriggerRows);
            Dispatcher.UIThread.Post(async () => await LoadHistoryAsync(true));
            WheelEvents.WheelSpinTriggerFired += OnTriggerFired;
            WheelEvents.WheelSpinTriggersChanged += OnTriggersChanged;
        };
        Unloaded += (_, _) => {
            WheelEvents.WheelSpinTriggerFired -= OnTriggerFired;
            WheelEvents.WheelSpinTriggersChanged -= OnTriggersChanged;
        };
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

    private void SuppressChangesDeferred(Action action) {
        _suppressCount++;
        try {
            action();
        }
        finally {
            Dispatcher.UIThread.Post(() => _suppressCount--, DispatcherPriority.Background);
        }
    }

    private void MarkDirty() {
        if (_suppressCount > 0) return;
        _isDirty = true;
        UpdateSaveButtonState();
    }

    private void MarkDirty(object? sender) {
        if (!DirtySaveGuard.Consume(sender)) return;
        MarkDirty();
    }

    private void UpdateSaveButtonState() {
        bool showGlow;
        if (_isNewTrigger) {
            EditorTitle.Text = "New Trigger";
            SaveTriggerBtn.Content = "Add";
            SaveTriggerBtn.IsEnabled = true;
            showGlow = true;
        }
        else if (_selectedTrigger != null) {
            EditorTitle.Text = "Trigger Editor";
            SaveTriggerBtn.Content = "Save Changes";
            SaveTriggerBtn.IsEnabled = true;
            showGlow = _isDirty;
        }
        else {
            SaveTriggerBtn.IsEnabled = false;
            showGlow = false;
        }

        UiHelpers.UpdateButtonPendingBorder(SaveButtonBorder, showGlow);
    }

    private void LoadCurrencies() {
        if (OrderCurrencyBox.ItemsSource != null) return;

        List<string> currencies = ServiceManager.Events.ValidEventCurrencies().OrderBy(x => x).ToList();
        SuppressChanges(() => {
            OrderCurrencyBox.ItemsSource = currencies;
            DonationCurrencyBox.ItemsSource = currencies;
        });
    }

    private void WireDirtyHandlers() {
        TriggerEnabledCheck.IsCheckedChanged += (s, _) => MarkDirty(s);
        TierComboBox.SelectionChanged += (s, _) => MarkDirty(s);
        TierTextBox.TextChanged += (s, _) => MarkDirty(s);
        GiftCountBox.TextChanged += (s, _) => MarkDirty(s);
        TokenCountBox.TextChanged += (s, _) => MarkDirty(s);
        OrderByItemsRadio.IsCheckedChanged += (s, _) => MarkDirty(s);
        OrderByMoneyRadio.IsCheckedChanged += (s, _) => MarkDirty(s);
        OrderByOrderRadio.IsCheckedChanged += (s, _) => MarkDirty(s);
        OrderItemCountBox.TextChanged += (s, _) => MarkDirty(s);
        OrderMoneyBox.TextChanged += (s, _) => MarkDirty(s);
        OrderCurrencyBox.SelectionChanged += (s, _) => MarkDirty(s);
        DonationMoneyBox.TextChanged += (s, _) => MarkDirty(s);
        DonationCurrencyBox.SelectionChanged += (s, _) => MarkDirty(s);
        SpinsToAddBox.TextChanged += (s, _) => MarkDirty(s);

        foreach (Control control in new Control[] {
                     TriggerEnabledCheck, TierComboBox, TierTextBox, GiftCountBox, TokenCountBox,
                     OrderByItemsRadio, OrderByMoneyRadio, OrderByOrderRadio, OrderItemCountBox,
                     OrderMoneyBox, OrderCurrencyBox, DonationMoneyBox, DonationCurrencyBox, SpinsToAddBox
                 })
            DirtySaveGuard.Rebase(control);

        EnterKeyCommit.Attach(this, () => {
            if (!SaveTriggerBtn.IsEnabled) return;
            SaveTrigger_Click(this, new RoutedEventArgs());
        });
    }

    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e) {
        (sender as Control)?.Focus();
    }

    private void EventTypePickerBtn_Click(object? sender, RoutedEventArgs e) {
        var entries = new List<EventTypeMenuEntry>();

        IOrderedEnumerable<IGrouping<SubathonEventSource, SubathonEventType>> groups = Enum
            .GetValues<SubathonEventType>()
            .Where(et => et.IsEnabled() &&
                         et.GetSubType() is { } st && ValidSubTypes.Contains(st))
            .GroupBy(et => et.GetSource())
            .OrderBy(g => g.Key.GetGroupLabelOrder());

        foreach (IGrouping<SubathonEventSource, SubathonEventType> group in groups)
        foreach (SubathonEventType et in group.OrderBy(et => et.GetLabel())) {
            if (et == SubathonEventType.GoAffProOrder) {
                foreach (GoAffProStore store in GoAffProStoreRegistry.All().Where(s => s.Enabled)
                             .OrderBy(s => s.EventName)) {
                    GoAffProStore capturedStore = store;
                    entries.Add(new EventTypeMenuEntry(
                        group.Key,
                        store.EventName,
                        _selectedEventTag is GoAffProStore sel && sel.SiteId == store.SiteId,
                        () => OnEventTypeSelected(capturedStore, capturedStore.EventName)));
                }

                continue;
            }

            if (et == SubathonEventType.JuniperMerchSale) {
                foreach (JuniperStore store in JuniperStoreRegistry.AllStores().Where(s => s.Enabled)) {
                    JuniperStore capturedStore = store;
                    entries.Add(new EventTypeMenuEntry(
                        group.Key,
                        "Any Sale",
                        _selectedEventTag is JuniperStore selStore && selStore.RowId == store.RowId,
                        () => OnEventTypeSelected(capturedStore, $"{capturedStore.StoreName} - Any Sale"),
                        store.StoreName));

                    foreach (JuniperProduct product in store.Products.OrderBy(p => p.ProductName,
                                 StringComparer.OrdinalIgnoreCase)) {
                        JuniperProduct capturedProduct = product;
                        entries.Add(new EventTypeMenuEntry(
                            group.Key,
                            product.ProductName,
                            _selectedEventTag is JuniperProduct sel && sel.ProductId == product.ProductId,
                            () => OnEventTypeSelected(capturedProduct,
                                $"{JuniperStoreRegistry.GetStoreName(capturedProduct.StoreId)} - {capturedProduct.ProductName}"),
                            store.StoreName));
                    }
                }

                continue;
            }

            if (et is SubathonEventType.MakeShipPledge or SubathonEventType.MakeShipSale) {
                bool isPledge = et == SubathonEventType.MakeShipPledge;
                string category = isPledge ? "Pledges" : "Campaign Sales";
                SubathonEventType capturedAny = et;
                entries.Add(new EventTypeMenuEntry(
                    group.Key,
                    isPledge ? "Any Pledge" : "Any Sale",
                    _selectedEventTag is SubathonEventType selAny && selAny == et,
                    () => OnEventTypeSelected(capturedAny, capturedAny.GetLabel()),
                    category));

                MakeShipProductType wantedType = isPledge ? MakeShipProductType.Petition : MakeShipProductType.Campaign;
                foreach (MakeShipTracking tracking in MakeShipTrackingRegistry.All()
                             .Where(t => MakeShipTrackingRegistry.ClassifyUrl(t.Url) == wantedType)
                             .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)) {
                    MakeShipTracking capturedTracking = tracking;
                    entries.Add(new EventTypeMenuEntry(
                        group.Key,
                        tracking.Name,
                        _selectedEventTag is MakeShipTracking selT && selT.Id == tracking.Id,
                        () => OnEventTypeSelected(capturedTracking, capturedTracking.Name),
                        category));
                }

                continue;
            }

            SubathonEventType captured = et;
            entries.Add(new EventTypeMenuEntry(
                group.Key,
                et.GetLabel(),
                _selectedEventTag is SubathonEventType s && s == et,
                () => OnEventTypeSelected(captured, captured.GetLabel())));
        }

        EventTypeMenu.Show(EventTypePickerBtn, entries);
    }

    private void OnEventTypeSelected(object tag, string label) {
        _selectedEventTag = tag;
        EventTypePickerLabel.Text = label;
        SubathonEventType? et = SelectedEventTypeFromTag(tag);
        EventTypeSourceLabel.Text = et.HasValue ? et.Value.GetSource().ToString() : "";
        TriggerStatusText.Text = "";
        UpdateEditorPanels(et);
        MarkDirty();
    }

    private void LoadTriggerRows() {
        TriggersStack.Children.Clear();
        using AppDbContext db = _factory.CreateDbContext();
        List<WheelSpinTrigger> triggers = db.WheelSpinTriggers
            .ToList()
            .OrderBy(t => t.EventType.GetSource().ToString())
            .ThenBy(t => t.EventType.GetLabel())
            .ThenBy(t => t.TierValue)
            .ToList();

        foreach (WheelSpinTrigger trigger in triggers)
            TriggersStack.Children.Add(BuildTriggerRow(trigger));

        RefreshTriggerRowHighlight(_selectedTrigger);
        UpdateEditorState();
    }

    private Grid BuildTriggerRow(WheelSpinTrigger trigger) {
        var row = new Grid {
            Margin = new Thickness(4, 0, 4, 4),
            Tag = trigger,
            MinHeight = 30,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            ColumnDefinitions = new ColumnDefinitions("30,*,100,46,34")
        };
        row.Tapped += (_, e) => {
            if (!UiHelpers.IsInteractiveSource(e.Source, row)) SelectTrigger(trigger, row);
        };

        var enabledCheck = new CheckBox {
            IsChecked = trigger.IsEnabled,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        ToolTip.SetTip(enabledCheck, "Enable / disable this trigger");
        enabledCheck.IsCheckedChanged += (_, _) => OnTriggerEnabledToggled(trigger, enabledCheck.IsChecked ?? false);
        Grid.SetColumn(enabledCheck, 0);

        var eventLabel = new TextBlock {
            Text = BuildTriggerEventLabel(trigger),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0, 4, 0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(eventLabel, BuildTriggerEventLabel(trigger));
        Grid.SetColumn(eventLabel, 1);

        var conditionLabel = new TextBlock {
            Text = BuildTriggerConditionLabel(trigger),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Brushes.CornflowerBlue,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ToolTip.SetTip(conditionLabel, BuildTriggerConditionLabel(trigger));
        Grid.SetColumn(conditionLabel, 2);

        var spinsLabel = new TextBlock {
            Text = $"+{trigger.SpinsToAdd}",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Brushes.MediumSeaGreen
        };
        Grid.SetColumn(spinsLabel, 3);

        var deleteBtn = new Button {
            Content = new SymIcon { Glyph = "Delete20" },
            Width = 30, Height = 30, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = Brushes.Red,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(deleteBtn, "Delete trigger");
        deleteBtn.Click += (_, _) => DeleteTrigger_Click(trigger);
        Grid.SetColumn(deleteBtn, 4);

        row.Children.Add(enabledCheck);
        row.Children.Add(eventLabel);
        row.Children.Add(conditionLabel);
        row.Children.Add(spinsLabel);
        row.Children.Add(deleteBtn);

        return row;
    }

    private static string BuildTriggerEventLabel(WheelSpinTrigger t) {
        if (t.EventType == SubathonEventType.GoAffProOrder)
            return
                $"{t.EventType.GetSource()} - {GoAffProOrderHelper.GetOrderEventDisplayLabel(t.EventType, t.TierValue)}";
        if (t.EventType == SubathonEventType.JuniperMerchSale)
            return $"{t.EventType.GetSource()} - {OrderMetaFilter.Describe(t.EventType, t.TierValue)}";
        var label = $"{t.EventType.GetSource()} - {t.EventType.GetLabel()}";
        if (!string.IsNullOrEmpty(t.TierValue)) {
            string? display = t.EventType is SubathonEventType.TwitchSub or SubathonEventType.TwitchGiftSub
                ? TwitchTierDisplay(t.TierValue)
                : t.TierValue;
            label += $" ({display})";
        }

        return label;
    }

    private static string TwitchTierDisplay(string tierValue) {
        return tierValue switch {
            "1000" => "T1",
            "2000" => "T2",
            "3000" => "T3",
            _ => tierValue
        };
    }

    private static string BuildTriggerConditionLabel(WheelSpinTrigger t) {
        SubathonEventSubType? subType = t.EventType.GetSubType();
        return subType switch {
            SubathonEventSubType.SubLike => "per sub",
            SubathonEventSubType.GiftSubLike when t.CountThreshold.HasValue => $"{t.CountThreshold} gifts",
            SubathonEventSubType.GiftSubLike => "per gift",
            SubathonEventSubType.TokenLike when t.CountThreshold.HasValue => $"{t.CountThreshold} tokens",
            SubathonEventSubType.TokenLike => "per token",
            SubathonEventSubType.DonationLike when t.MoneyThreshold.HasValue => $"{t.MoneyThreshold:F2} {t.Currency}",
            SubathonEventSubType.OrderLike when t.CountThreshold.HasValue => $"{t.CountThreshold} items",
            SubathonEventSubType.OrderLike when t.MoneyThreshold.HasValue => $"{t.MoneyThreshold:F2} {t.Currency}",
            SubathonEventSubType.OrderLike => "per order",
            _ => "-"
        };
    }

    private void RefreshTriggerRowHighlight(WheelSpinTrigger? selected) {
        foreach (Grid child in TriggersStack.Children.OfType<Grid>()) {
            var t = child.Tag as WheelSpinTrigger;
            child.Background = t?.Id == selected?.Id ? SelectedRowBrush : Brushes.Transparent;
        }
    }

    private void SelectTrigger(WheelSpinTrigger trigger, Grid? row = null) {
        _selectedTrigger = trigger;
        _isNewTrigger = false;
        _isDirty = false;
        TriggerStatusText.Text = "";
        ShowEditor(true);
        PopulateEditor(trigger);
        UpdateSaveButtonState();
        RefreshTriggerRowHighlight(trigger);
    }

    private void ShowEditor(bool show) {
        TriggerDetailBorder.IsVisible = show;
        TriggerPlaceholderText.IsVisible = !show;
    }

    private void PopulateEditor(WheelSpinTrigger trigger) {
        SuppressChangesDeferred(() => {
            TriggerEnabledCheck.IsChecked = trigger.IsEnabled;

            if (trigger.EventType == SubathonEventType.GoAffProOrder) {
                GoAffProStore? store = GoAffProStoreRegistry.All()
                    .FirstOrDefault(s => s.SiteId.ToString() == trigger.TierValue);
                _selectedEventTag = store;
                EventTypePickerLabel.Text = store?.EventName ?? "- select -";
            }
            else if (trigger.EventType == SubathonEventType.JuniperMerchSale) {
                if (Guid.TryParse(trigger.TierValue, out Guid storeId)
                    && JuniperStoreRegistry.TryGetStore(storeId, out JuniperStore? jStore)) {
                    _selectedEventTag = jStore;
                    EventTypePickerLabel.Text = $"{jStore.StoreName} - Any Sale";
                }
                else {
                    JuniperOrderHelper.TryGetProduct(trigger.TierValue, out JuniperProduct? product);
                    _selectedEventTag = product;
                    EventTypePickerLabel.Text = product != null
                        ? $"{JuniperStoreRegistry.GetStoreName(product.StoreId)} - {product.ProductName}"
                        : "- select -";
                }
            }
            else if (trigger.EventType is SubathonEventType.MakeShipPledge or SubathonEventType.MakeShipSale
                     && !string.IsNullOrEmpty(trigger.TierValue)) {
                MakeShipTracking? tracking = MakeShipTrackingRegistry.All().FirstOrDefault(t =>
                    string.Equals(t.Name, trigger.TierValue, StringComparison.OrdinalIgnoreCase));
                _selectedEventTag = (object?)tracking ?? trigger.EventType;
                EventTypePickerLabel.Text = tracking?.Name ?? $"{trigger.EventType.GetLabel()} ({trigger.TierValue})";
            }
            else {
                _selectedEventTag = trigger.EventType;
                EventTypePickerLabel.Text = trigger.EventType.GetLabel();
            }

            UpdateEditorPanels(trigger.EventType);
        });
        EventTypeSourceLabel.Text = trigger.EventType.GetSource().ToString();

        SuppressChangesDeferred(() => {
            SubathonEventSubType? subType = trigger.EventType.GetSubType();
            bool isTwitchTier = TwitchTierTypes.Contains(trigger.EventType);
            bool isPicartoTier = PicartoTierTypes.Contains(trigger.EventType);

            if (isTwitchTier || isPicartoTier) {
                ComboBoxItem? tierItem = TierComboBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => (string)i.Tag! == (trigger.TierValue ?? ""));
                TierComboBox.SelectedItem = tierItem ?? TierComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
            }
            else {
                TierTextBox.Text = trigger.TierValue ?? "";
            }

            if (subType == SubathonEventSubType.GiftSubLike)
                GiftCountBox.Text = trigger.CountThreshold?.ToString() ?? "";

            if (subType == SubathonEventSubType.TokenLike)
                TokenCountBox.Text = trigger.CountThreshold?.ToString() ?? "";

            if (subType == SubathonEventSubType.OrderLike) {
                bool byItems = trigger.CountThreshold.HasValue;
                bool byMoney = !byItems && trigger.MoneyThreshold.HasValue;
                OrderByItemsRadio.IsChecked = byItems;
                OrderByMoneyRadio.IsChecked = byMoney;
                OrderByOrderRadio.IsChecked = !byItems && !byMoney;
                UpdateOrderModePanel(trigger.EventType);
                OrderItemCountBox.Text = trigger.CountThreshold?.ToString() ?? "";
                OrderMoneyBox.Text = trigger.MoneyThreshold?.ToString("F2") ?? "";
                OrderCurrencyBox.SelectedItem = trigger.Currency;
            }

            if (subType == SubathonEventSubType.DonationLike) {
                DonationMoneyBox.Text = trigger.MoneyThreshold?.ToString("F2") ?? "";
                DonationCurrencyBox.SelectedItem = trigger.Currency;
            }

            SpinsToAddBox.Text = trigger.SpinsToAdd.ToString();
        });
    }

    private void UpdateEditorState() {
        bool hasSelection = _selectedTrigger != null || _isNewTrigger;
        ShowEditor(hasSelection);
        UpdateSaveButtonState();
    }

    private void UpdateEditorPanels(SubathonEventType? eventType) {
        if (eventType == null) {
            TierPanel.IsVisible = false;
            GiftCountPanel.IsVisible = false;
            TokenCountPanel.IsVisible = false;
            OrderModePanel.IsVisible = false;
            OrderByItemsRadio.IsEnabled = true;
            OrderByOrderRadio.IsEnabled = true;
            OrderItemCountBox.IsEnabled = true;
            DonationPanel.IsVisible = false;
            return;
        }

        SubathonEventSubType subType = eventType.GetSubType();
        bool isGift = eventType.IsGift();
        bool isYtGiftMembership = isGift && eventType == SubathonEventType.YouTubeGiftMembership; // no tier select
        bool isSubLike = subType is SubathonEventSubType.SubLike or SubathonEventSubType.GiftSubLike
                         && !isYtGiftMembership;
        bool isTwitchTier = TwitchTierTypes.Contains(eventType.Value);
        bool isPicartoTier = PicartoTierTypes.Contains(eventType.Value);

        TierPanel.IsVisible = isSubLike;
        if (isSubLike) {
            bool useCombo = isTwitchTier || isPicartoTier;
            TierComboPanel.IsVisible = useCombo;
            TierTextPanel.IsVisible = !useCombo;

            if (useCombo)
                PopulateTierComboBox(eventType.Value);
        }

        GiftCountPanel.IsVisible = isGift;

        TokenCountPanel.IsVisible = subType == SubathonEventSubType.TokenLike;

        if (subType == SubathonEventSubType.OrderLike) {
            OrderModePanel.IsVisible = true;
            UpdateOrderModePanel(eventType.Value);
        }
        else {
            OrderModePanel.IsVisible = false;
            OrderByItemsRadio.IsEnabled = true;
            OrderByOrderRadio.IsEnabled = true;
            OrderItemCountBox.IsEnabled = true;
        }

        DonationPanel.IsVisible = subType == SubathonEventSubType.DonationLike;
    }

    private void PopulateTierComboBox(SubathonEventType eventType) {
        TierComboBox.Items.Clear();

        if (TwitchTierTypes.Contains(eventType)) {
            TierComboBox.Items.Add(new ComboBoxItem { Content = "T1", Tag = "1000" });
            TierComboBox.Items.Add(new ComboBoxItem { Content = "T2", Tag = "2000" });
            TierComboBox.Items.Add(new ComboBoxItem { Content = "T3", Tag = "3000" });
        }
        else if (eventType == SubathonEventType.PicartoSub) {
            TierComboBox.Items.Add(new ComboBoxItem { Content = "T1", Tag = "T1" });
            TierComboBox.Items.Add(new ComboBoxItem { Content = "T2", Tag = "T2" });
            TierComboBox.Items.Add(new ComboBoxItem { Content = "T3", Tag = "T3" });
        }
        else if (eventType == SubathonEventType.PicartoGiftSub) {
            TierComboBox.Items.Add(new ComboBoxItem { Content = "T1", Tag = "T1" });
        }

        if (TierComboBox.Items.Count > 0)
            TierComboBox.SelectedIndex = 0;
    }

    private void UpdateOrderModePanel(SubathonEventType? eventType = null) {
        if (OrderItemPanel == null || OrderMoneyPanel == null) return;

        eventType ??= SelectedEventTypeFromTag(_selectedEventTag);

        bool forceByMoney = eventType == SubathonEventType.ThroneGiftContribution;
        bool forceByItems = eventType == SubathonEventType.JuniperMerchSale;
        bool noItemCount = eventType is SubathonEventType.ThroneGiftPurchase
            or SubathonEventType.KoFiCommissionOrder
            or SubathonEventType.TreatStreamOrder;
        bool noMoney = eventType is SubathonEventType.TreatStreamOrder
            or SubathonEventType.MakeShipPledge or SubathonEventType.MakeShipSale;
        bool noOrder = eventType is SubathonEventType.MakeShipSale or SubathonEventType.MakeShipPledge;

        if (forceByItems) {
            SuppressChanges(() => OrderByItemsRadio.IsChecked = true);
            OrderByItemsRadio.IsEnabled = true;
            OrderByOrderRadio.IsEnabled = false;
            OrderByMoneyRadio.IsEnabled = false;
        }
        else if (forceByMoney) {
            SuppressChanges(() => OrderByMoneyRadio.IsChecked = true);
            OrderByItemsRadio.IsEnabled = false;
            OrderByOrderRadio.IsEnabled = false;
            OrderByMoneyRadio.IsEnabled = true;
        }
        else if (noItemCount) {
            OrderByItemsRadio.IsEnabled = false;
            OrderByOrderRadio.IsEnabled = true;
            OrderByMoneyRadio.IsEnabled = !noMoney;
            if (OrderByItemsRadio.IsChecked == true)
                SuppressChanges(() => OrderByOrderRadio.IsChecked = true);
        }
        else {
            OrderByItemsRadio.IsEnabled = true;
            OrderByOrderRadio.IsEnabled = !noOrder;
            OrderByMoneyRadio.IsEnabled = !noMoney;
        }

        bool byItems = OrderByItemsRadio.IsChecked == true;
        bool byMoney = OrderByMoneyRadio.IsChecked == true;

        OrderItemPanel.IsVisible = byItems;
        OrderMoneyPanel.IsVisible = byMoney;
    }

    private void AddTrigger_Click(object? sender, RoutedEventArgs e) {
        _selectedTrigger = null;
        _isNewTrigger = true;
        _isDirty = false;
        TriggerStatusText.Text = "";
        ShowEditor(true);
        RefreshTriggerRowHighlight(null);

        EventTypeSourceLabel.Text = "";
        SuppressChangesDeferred(() => {
            TriggerEnabledCheck.IsChecked = true;
            _selectedEventTag = null;
            EventTypePickerLabel.Text = "- select -";
            TierTextBox.Text = "";
            GiftCountBox.Text = "";
            TokenCountBox.Text = "";
            OrderItemCountBox.Text = "";
            OrderMoneyBox.Text = "";
            string defaultCurrency = _config.Get("Currency", "Primary", "USD") ?? "USD";
            OrderCurrencyBox.SelectedItem = defaultCurrency;
            DonationMoneyBox.Text = "";
            DonationCurrencyBox.SelectedItem = defaultCurrency;
            SpinsToAddBox.Text = "1";
        });
        UpdateEditorPanels(null);
        UpdateSaveButtonState();
    }

    private async void DeleteTrigger_Click(WheelSpinTrigger trigger) {
        var dialog = new FAContentDialog {
            Title = "Delete Trigger",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            Content = new TextBlock {
                Text = "Delete this trigger? All associated trigger history will also be permanently deleted.",
                TextWrapping = TextWrapping.Wrap,
                Width = 320,
                Margin = new Thickness(4)
            }
        };

        if (await dialog.ShowAsync() != FAContentDialogResult.Primary) return;

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        WheelSpinTrigger? tracked = await db.WheelSpinTriggers.FindAsync(trigger.Id);
        if (tracked != null)
            db.WheelSpinTriggers.Remove(tracked);
        await db.SaveChangesAsync();

        if (_selectedTrigger?.Id == trigger.Id) {
            _selectedTrigger = null;
            _isNewTrigger = false;
        }

        await Dispatcher.UIThread.InvokeAsync(LoadTriggerRows);
        await Dispatcher.UIThread.InvokeAsync(async () => await LoadHistoryAsync(true));
        WheelEvents.RaiseWheelSpinTriggersChanged();
    }

    private async void SaveTrigger_Click(object? sender, RoutedEventArgs e) {
        TriggerStatusText.Text = "";

        var goAffProStore = _selectedEventTag as GoAffProStore;
        var juniperProduct = _selectedEventTag as JuniperProduct;
        var juniperStore = _selectedEventTag as JuniperStore;
        var makeShipTracking = _selectedEventTag as MakeShipTracking;
        if (SelectedEventTypeFromTag(_selectedEventTag) is not { } eventType) {
            TriggerStatusText.Text = "Select an event type";
            return;
        }

        if (!int.TryParse(SpinsToAddBox.Text, out int spinsToAdd) || spinsToAdd < 1) {
            TriggerStatusText.Text = "Spins to Add must be a whole number ≥ 1";
            return;
        }

        SubathonEventSubType? subType = eventType.GetSubType();
        bool isTwitchTier = TwitchTierTypes.Contains(eventType);
        bool isPicartoTier = PicartoTierTypes.Contains(eventType);

        bool isYtGiftMembership = eventType == SubathonEventType.YouTubeGiftMembership; // no tiers
        bool isSubLike = subType is SubathonEventSubType.SubLike or SubathonEventSubType.GiftSubLike
                         && !isYtGiftMembership;

        string? tierValue = null;
        if (goAffProStore != null) {
            tierValue = goAffProStore.SiteId.ToString();
        }
        else if (juniperProduct != null) {
            tierValue = juniperProduct.ProductId.ToString();
        }
        else if (juniperStore != null) {
            tierValue = juniperStore.RowId.ToString();
        }
        else if (makeShipTracking != null) {
            tierValue = makeShipTracking.Name;
        }
        else if (isSubLike) {
            if (isTwitchTier || isPicartoTier) {
                if (TierComboBox.SelectedItem is not ComboBoxItem tierItem) {
                    TriggerStatusText.Text = "Select a tier";
                    return;
                }

                tierValue = tierItem.Tag as string;
            }
            else {
                tierValue = (TierTextBox.Text ?? "").Trim();
                if (string.IsNullOrEmpty(tierValue)) {
                    TriggerStatusText.Text = "Enter a tier name (or DEFAULT for unknown memberships if not setup)";
                    return;
                }
            }
        }

        int? countThreshold = null;
        double? moneyThreshold = null;
        string? currency = null;

        switch (subType) {
            case SubathonEventSubType.GiftSubLike:
                if (!string.IsNullOrWhiteSpace(GiftCountBox.Text)) {
                    if (!int.TryParse(GiftCountBox.Text, out int gc) || gc < 1) {
                        TriggerStatusText.Text =
                            "Gift count must be a whole number ≥ 1 (or leave blank for 1 per gift)";
                        return;
                    }

                    countThreshold = gc;
                }

                break;

            case SubathonEventSubType.TokenLike:
                if (string.IsNullOrWhiteSpace(TokenCountBox.Text) ||
                    !int.TryParse(TokenCountBox.Text, out int tc) || tc < 1) {
                    TriggerStatusText.Text = "Token count must be a whole number ≥ 1";
                    return;
                }

                countThreshold = tc;
                break;

            case SubathonEventSubType.OrderLike:
                if (OrderByItemsRadio.IsChecked == true) {
                    if (string.IsNullOrWhiteSpace(OrderItemCountBox.Text) ||
                        !int.TryParse(OrderItemCountBox.Text, out int ic) || ic < 1) {
                        TriggerStatusText.Text = "Item count must be a whole number ≥ 1";
                        return;
                    }

                    countThreshold = ic;
                }
                else if (OrderByMoneyRadio.IsChecked == true) {
                    if (string.IsNullOrWhiteSpace(OrderMoneyBox.Text) ||
                        !double.TryParse(OrderMoneyBox.Text, out double om) || om <= 0) {
                        TriggerStatusText.Text = "Order amount must be a positive number";
                        return;
                    }

                    currency = (OrderCurrencyBox.Text ?? "").Trim().ToUpperInvariant();
                    if (currency.Length < 2) {
                        TriggerStatusText.Text = "Select a valid currency code (e.g. USD)";
                        return;
                    }

                    moneyThreshold = om;
                }

                break;

            case SubathonEventSubType.DonationLike:
                if (string.IsNullOrWhiteSpace(DonationMoneyBox.Text) ||
                    !double.TryParse(DonationMoneyBox.Text, out double dm) || dm <= 0) {
                    TriggerStatusText.Text = "Donation amount must be a positive number";
                    return;
                }

                currency = (DonationCurrencyBox.Text ?? "").Trim().ToUpperInvariant();
                if (currency.Length < 2) {
                    TriggerStatusText.Text = "Select a valid currency code (e.g. USD)";
                    return;
                }

                moneyThreshold = dm;
                break;
        }

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        bool isDuplicateTierEvent = subType is SubathonEventSubType.SubLike or SubathonEventSubType.GiftSubLike
                                    || eventType is SubathonEventType.GoAffProOrder
                                        or SubathonEventType.JuniperMerchSale
                                        or SubathonEventType.MakeShipPledge
                                        or SubathonEventType.MakeShipSale;

        List<WheelSpinTrigger> existing = await db.WheelSpinTriggers
            .Where(t => t.EventType == eventType &&
                        t.Id != (_selectedTrigger != null ? _selectedTrigger.Id : Guid.Empty))
            .ToListAsync();

        if (isDuplicateTierEvent) {
            bool tierConflict = existing.Any(t =>
                string.Equals(t.TierValue, tierValue, StringComparison.OrdinalIgnoreCase));
            if (tierConflict) {
                TriggerStatusText.Text = eventType switch {
                    SubathonEventType.GoAffProOrder =>
                        $"A trigger for {GoAffProOrderHelper.GetOrderEventDisplayLabel(eventType, tierValue)} already exists. Edit or delete it first",
                    SubathonEventType.JuniperMerchSale or SubathonEventType.MakeShipPledge
                        or SubathonEventType.MakeShipSale =>
                        $"A trigger for {eventType.GetLabel()} ({OrderMetaFilter.Describe(eventType, tierValue)}) already exists. Edit or delete it first",
                    _ => $"A trigger for {eventType.GetLabel()} ({tierValue}) already exists. Edit or delete it first"
                };
                return;
            }
        }
        else {
            if (existing.Count > 0) {
                TriggerStatusText.Text =
                    $"A trigger for {eventType.GetSource()} {eventType.GetLabel()} already exists. Only one trigger is allowed per event type";
                return;
            }
        }

        if (_isNewTrigger) {
            var trigger = new WheelSpinTrigger {
                IsEnabled = TriggerEnabledCheck.IsChecked == true,
                SpinsToAdd = spinsToAdd,
                EventType = eventType,
                TierValue = tierValue,
                CountThreshold = countThreshold,
                MoneyThreshold = moneyThreshold,
                Currency = currency
            };
            db.WheelSpinTriggers.Add(trigger);
            await db.SaveChangesAsync();
            _selectedTrigger = trigger;
            _isNewTrigger = false;
        }
        else if (_selectedTrigger != null) {
            WheelSpinTrigger? tracked = await db.WheelSpinTriggers.FindAsync(_selectedTrigger.Id);
            if (tracked == null) {
                TriggerStatusText.Text = "Trigger not found. It may be deleted";
                return;
            }

            tracked.IsEnabled = TriggerEnabledCheck.IsChecked == true;
            tracked.SpinsToAdd = spinsToAdd;
            tracked.EventType = eventType;
            tracked.TierValue = tierValue;
            tracked.CountThreshold = countThreshold;
            tracked.MoneyThreshold = moneyThreshold;
            tracked.Currency = currency;
            await db.SaveChangesAsync();
            _selectedTrigger = tracked;
        }

        _isDirty = false;
        _isNewTrigger = false;
        await Dispatcher.UIThread.InvokeAsync(LoadTriggerRows);
        WheelEvents.RaiseWheelSpinTriggersChanged();
        TriggerStatusText.Text = "";
        if (_selectedTrigger != null) {
            ShowEditor(true);
            PopulateEditor(_selectedTrigger);
            RefreshTriggerRowHighlight(_selectedTrigger);
        }

        UpdateSaveButtonState();
    }

    private async void OnTriggerEnabledToggled(WheelSpinTrigger trigger, bool enabled) {
        if (_suppressCount > 0) return;

        trigger.IsEnabled = enabled;
        await using AppDbContext db = await _factory.CreateDbContextAsync();
        WheelSpinTrigger? tracked = await db.WheelSpinTriggers.FindAsync(trigger.Id);
        if (tracked == null) return;
        tracked.IsEnabled = enabled;
        await db.SaveChangesAsync();

        if (_selectedTrigger?.Id == trigger.Id)
            SuppressChanges(() => TriggerEnabledCheck.IsChecked = enabled);
    }

    private async Task LoadHistoryAsync(bool reset = false) {
        if (_historyLoading) return;
        _historyLoading = true;

        if (reset) {
            _historyOffset = 0;
            await Dispatcher.UIThread.InvokeAsync(() => TriggerHistoryStack.Children.Clear());
        }

        try {
            await using AppDbContext db = await _factory.CreateDbContextAsync();
            List<WheelSpinTriggerHistory> rows = await db.WheelSpinTriggerHistories
                .Include(h => h.Trigger)
                .OrderByDescending(h => h.TriggeredAt)
                .Skip(_historyOffset)
                .Take(HistoryPageSize)
                .ToListAsync();

            if (rows.Count == 0) {
                _historyLoading = false;
                return;
            }

            _historyOffset += rows.Count;

            await Dispatcher.UIThread.InvokeAsync(() => {
                foreach (WheelSpinTriggerHistory h in rows)
                    TriggerHistoryStack.Children.Add(BuildHistoryRow(h));
            });
        }
        finally {
            _historyLoading = false;
        }
    }

    private static Grid BuildHistoryRow(WheelSpinTriggerHistory h) {
        var row = new Grid {
            Margin = new Thickness(2, 0, 4, 3),
            ColumnDefinitions = new ColumnDefinitions("145,*,225,50")
        };

        var timeLabel = new TextBlock {
            Text = h.TriggeredAt.ToString("yyyy-MM-dd HH:mm:ss"),
            FontSize = 11,
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(timeLabel, 0);

        string eventDesc = h.Trigger != null
            ? BuildTriggerEventLabel(h.Trigger)
            : h.SubathonEventType?.GetLabel() ?? "Unknown";
        var eventLabel = new TextBlock {
            Text = eventDesc,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0, 4, 0)
        };
        ToolTip.SetTip(eventLabel, eventDesc);
        Grid.SetColumn(eventLabel, 1);

        var userLabel = new TextBlock {
            Text = h.TriggerUser ?? "-",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0, 4, 0)
        };
        ToolTip.SetTip(userLabel, h.TriggerUser);
        Grid.SetColumn(userLabel, 2);

        var spinsLabel = new TextBlock {
            Text = $"+{h.SpinsAdded}",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.MediumSeaGreen,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(spinsLabel, 3);

        row.Children.Add(timeLabel);
        row.Children.Add(eventLabel);
        row.Children.Add(userLabel);
        row.Children.Add(spinsLabel);

        return row;
    }

    private void TriggerHistoryScroller_ScrollChanged(object? sender, ScrollChangedEventArgs e) {
        if (_historyLoading) return;
        double scrollable = TriggerHistoryScroller.Extent.Height - TriggerHistoryScroller.Viewport.Height;
        if (scrollable > 0 && scrollable - TriggerHistoryScroller.Offset.Y < 100)
            _ = LoadHistoryAsync();
    }

    private async void ExportHistoryToCsv_Click(object? sender, RoutedEventArgs e) {
        await using AppDbContext db = await _factory.CreateDbContextAsync();
        List<WheelSpinTriggerHistory> rows = await db.WheelSpinTriggerHistories
            .Include(h => h.Trigger)
            .OrderByDescending(h => h.TriggeredAt)
            .ToListAsync();

        string exportDir = Path.Combine(Config.DataFolder, "exports");
        Directory.CreateDirectory(exportDir);
        string filepath = Path.Combine(exportDir, $"wheel-trigger-history-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        var sb = new StringBuilder();
        sb.AppendLine(
            "Id,TriggerId,TriggerEventType,TriggeredAt,TriggerUser,TriggerSource,SpinsAdded,SubathonEventId,SubathonEventType");
        foreach (WheelSpinTriggerHistory h in rows) {
            string eventLabel = h.Trigger != null
                ? BuildTriggerEventLabel(h.Trigger)
                : h.SubathonEventType?.GetLabel() ?? "";
            string user = h.TriggerUser?.Replace("\"", "\"\"") ?? "";
            sb.AppendLine(
                $"{h.Id}," +
                $"{h.TriggerId}," +
                $"{eventLabel}," +
                $"{h.TriggeredAt:yyyy-MM-dd HH:mm:ss}," +
                $"\"{user}\"," +
                $"{h.TriggerSource}," +
                $"{h.SpinsAdded}," +
                $"{h.SubathonEventId?.ToString() ?? ""}," +
                $"{h.SubathonEventType?.ToString() ?? ""}");
        }

        await File.WriteAllTextAsync(filepath, sb.ToString(), Encoding.UTF8);

        try {
            UiHelpers.OpenFolder(exportDir);
        }
        catch {
            /**/
        }
    }

    private async void ExportTriggers_Click(object? sender, RoutedEventArgs e) {
        await using AppDbContext db = await _factory.CreateDbContextAsync();
        List<WheelSpinTrigger> triggers = await db.WheelSpinTriggers.AsNoTracking().ToListAsync();

        string exportDir = Path.Combine(Config.DataFolder, "exports");
        Directory.CreateDirectory(exportDir);
        string filepath = Path.Combine(exportDir, $"wheel-triggers-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        var sb = new StringBuilder();
        sb.AppendLine("Enabled,SpinsToAdd,EventType,TierValue,CountThreshold,MoneyThreshold,Currency");
        foreach (WheelSpinTrigger t in triggers)
            sb.AppendLine(string.Join(",",
                t.IsEnabled,
                t.SpinsToAdd,
                t.EventType,
                Utils.EscapeCsv(t.TierValue ?? ""),
                t.CountThreshold?.ToString() ?? "",
                t.MoneyThreshold?.ToString("G") ?? "",
                Utils.EscapeCsv(t.Currency ?? "")));

        await File.WriteAllTextAsync(filepath, sb.ToString(), Encoding.UTF8);

        try {
            UiHelpers.OpenFolder(exportDir);
        }
        catch {
            /**/
        }
    }

    private async void ImportTriggers_Click(object? sender, RoutedEventArgs e) {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        IReadOnlyList<IStorageFile> picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = "Import Triggers",
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
            await ShowInvalidTriggerCsvPopup();
            return;
        }

        if (lines.Length < 1) {
            await ShowInvalidTriggerCsvPopup();
            return;
        }

        string[] headerCols = ParseTriggerCsvLine(lines[0]);
        if (headerCols.Length < 3) {
            await ShowInvalidTriggerCsvPopup();
            return;
        }

        var parsed = new List<WheelSpinTrigger>();
        for (var i = 1; i < lines.Length; i++) {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            string[] cols = ParseTriggerCsvLine(lines[i]);

            if (cols.Length < 3
                || !bool.TryParse(cols[0].Trim(), out bool enabled)
                || !int.TryParse(cols[1].Trim(), out int spins)) {
                await ShowInvalidTriggerCsvPopup();
                return;
            }

            string typeStr = cols[2].Trim();
            string? goAffProMeta = null;
            if (!Enum.TryParse<SubathonEventType>(typeStr, out SubathonEventType eventType)) {
                if (!GoAffProOrderHelper.TryGetStoreByOrderKey(typeStr, out GoAffProStore? keyStore)) {
                    await ShowInvalidTriggerCsvPopup();
                    return;
                }

                eventType = SubathonEventType.GoAffProOrder;
                goAffProMeta = keyStore.SiteId.ToString();
            }
            else if (eventType.GetLegacyGoAffProSiteId() > 0) {
                goAffProMeta = eventType.GetLegacyGoAffProSiteId().ToString();
                eventType = SubathonEventType.GoAffProOrder;
            }

            string? tierValue = cols.Length > 3 && !string.IsNullOrWhiteSpace(cols[3]) ? cols[3].Trim() : null;
            if (goAffProMeta != null) tierValue = goAffProMeta;

            int? countThreshold = null;
            if (cols.Length > 4 && !string.IsNullOrWhiteSpace(cols[4])) {
                if (!int.TryParse(cols[4].Trim(), out int ct)) {
                    await ShowInvalidTriggerCsvPopup();
                    return;
                }

                countThreshold = ct;
            }

            double? moneyThreshold = null;
            if (cols.Length > 5 && !string.IsNullOrWhiteSpace(cols[5])) {
                if (!double.TryParse(cols[5].Trim(), NumberStyles.Any,
                        CultureInfo.InvariantCulture, out double mt)) {
                    await ShowInvalidTriggerCsvPopup();
                    return;
                }

                moneyThreshold = mt;
            }

            string? currency = cols.Length > 6 && !string.IsNullOrWhiteSpace(cols[6]) ? cols[6].Trim() : null;

            parsed.Add(new WheelSpinTrigger {
                IsEnabled = enabled,
                SpinsToAdd = spins,
                EventType = eventType,
                TierValue = tierValue,
                CountThreshold = countThreshold,
                MoneyThreshold = moneyThreshold,
                Currency = currency
            });
        }

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        await db.WheelSpinTriggerHistories.ExecuteDeleteAsync();
        await db.WheelSpinTriggers.ExecuteDeleteAsync();

        db.WheelSpinTriggers.AddRange(parsed);
        await db.SaveChangesAsync();

        _selectedTrigger = null;
        _isNewTrigger = false;
        await Dispatcher.UIThread.InvokeAsync(LoadTriggerRows);
        await Dispatcher.UIThread.InvokeAsync(async () => await LoadHistoryAsync(true));
        WheelEvents.RaiseWheelSpinTriggersChanged();
    }

    private static string[] ParseTriggerCsvLine(string line) {
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

    private static async Task ShowInvalidTriggerCsvPopup() {
        var dialog = new FAContentDialog {
            Title = "Invalid CSV",
            CloseButtonText = "OK",
            Content = new TextBlock {
                Text = "The selected file is not a valid trigger CSV and could not be imported.",
                TextWrapping = TextWrapping.Wrap,
                Width = 300,
                Margin = new Thickness(4)
            }
        };
        await dialog.ShowAsync();
    }

    private async void DeleteAllTriggerHistory_Click(object? sender, RoutedEventArgs e) {
        var dialog = new FAContentDialog {
            Title = "Delete All Trigger History",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            Content = new TextBlock {
                Text = "Are you sure you want to delete all trigger history?",
                TextWrapping = TextWrapping.Wrap,
                Width = 320,
                Margin = new Thickness(4)
            }
        };

        if (await dialog.ShowAsync() != FAContentDialogResult.Primary) return;

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        await db.WheelSpinTriggerHistories.ExecuteDeleteAsync();

        await Dispatcher.UIThread.InvokeAsync(async () => await LoadHistoryAsync(true));
    }

    private void OnTriggerFired(WheelSpinTrigger trigger, WheelSpinTriggerHistory history, int newSpinsOwed) {
        Dispatcher.UIThread.Post(() => {
            TriggerHistoryStack.Children.Insert(0, BuildHistoryRow(history));
            _historyOffset++;
            while (TriggerHistoryStack.Children.Count > HistoryPageSize * 10)
                TriggerHistoryStack.Children.RemoveAt(TriggerHistoryStack.Children.Count - 1);
        });
    }

    private void OnTriggersChanged() {
        Dispatcher.UIThread.Post(LoadTriggerRows);
    }

    private static SubathonEventType? SelectedEventTypeFromTag(object? tag) {
        return tag switch {
            SubathonEventType et => et,
            GoAffProStore => SubathonEventType.GoAffProOrder,
            JuniperProduct or JuniperStore => SubathonEventType.JuniperMerchSale,
            MakeShipTracking tracking =>
                MakeShipTrackingRegistry.ClassifyUrl(tracking.Url) == MakeShipProductType.Campaign
                    ? SubathonEventType.MakeShipSale
                    : SubathonEventType.MakeShipPledge,
            _ => null
        };
    }

    private void OrderMode_Changed(object? sender, RoutedEventArgs e) {
        if (_suppressCount > 0) return;
        UpdateOrderModePanel();
    }
}