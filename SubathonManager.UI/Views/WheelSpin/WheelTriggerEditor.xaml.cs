using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.UI.Services;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views.WheelSpin
{
    public partial class WheelTriggerEditor
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly IConfig _config;
        private WheelSpinTrigger? _selectedTrigger;
        private bool _isNewTrigger;
        private bool _isDirty;
        private int _suppressCount;
        private int _historyOffset;
        private const int HistoryPageSize = 20;
        private bool _historyLoading;
        private bool _initialized;

        private static readonly System.Windows.Media.SolidColorBrush SelectedRowBrush =
            new(System.Windows.Media.Color.FromArgb(30, 100, 149, 237));

        static WheelTriggerEditor() => SelectedRowBrush.Freeze();

        // Valid event subtypes for triggers
        private static readonly HashSet<SubathonEventSubType> ValidSubTypes =
        [
            SubathonEventSubType.SubLike,
            SubathonEventSubType.GiftSubLike,
            SubathonEventSubType.TokenLike,
            SubathonEventSubType.DonationLike,
            SubathonEventSubType.OrderLike
        ];

        // has hardset tiers
        private static readonly HashSet<SubathonEventType> TwitchTierTypes =
        [
            SubathonEventType.TwitchSub,
            SubathonEventType.TwitchGiftSub
        ];

        // has hardset tiers
        private static readonly HashSet<SubathonEventType> PicartoTierTypes =
        [
            SubathonEventType.PicartoSub,
            SubathonEventType.PicartoGiftSub
        ];

        public WheelTriggerEditor()
        {
            _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            _config = AppServices.Provider.GetRequiredService<IConfig>();
            InitializeComponent();

            Loaded += (_, _) =>
            {
                if (!_initialized)
                {
                    PopulateEventTypeComboBox();
                    LoadCurrencies();
                    WireDirtyHandlers();
                    _initialized = true;
                }
                Dispatcher.InvokeAsync(LoadTriggerRows);
                Dispatcher.InvokeAsync(async () => await LoadHistoryAsync(reset: true));
                WheelEvents.WheelSpinTriggerFired += OnTriggerFired;
                WheelEvents.WheelSpinTriggersChanged += OnTriggersChanged;
            };
            Unloaded += (_, _) =>
            {
                WheelEvents.WheelSpinTriggerFired -= OnTriggerFired;
                WheelEvents.WheelSpinTriggersChanged -= OnTriggersChanged;
            };
        }

        private void SuppressChanges(Action action)
        {
            _suppressCount++;
            try { action(); }
            finally { _suppressCount--; }
        }

        private void MarkDirty()
        {
            if (_suppressCount > 0) return;
            _isDirty = true;
            UpdateSaveButtonState();
        }

        private void UpdateSaveButtonState()
        {
            bool showGlow;
            if (_isNewTrigger)
            {
                EditorTitle.Text = "New Trigger";
                SaveTriggerBtn.Content = "Add";
                SaveTriggerBtn.IsEnabled = true;
                showGlow = true;
            }
            else if (_selectedTrigger != null)
            {
                EditorTitle.Text = "Trigger Editor";
                SaveTriggerBtn.Content = "Save Changes";
                SaveTriggerBtn.IsEnabled = true;
                showGlow = _isDirty;
            }
            else
            {
                SaveTriggerBtn.IsEnabled = false;
                showGlow = false;
            }
            UiUtils.UiUtils.UpdateButtonPendingBorder(SaveButtonBorder, showGlow);
        }

        private void LoadCurrencies()
        {
            var currencies = ServiceManager.Events.ValidEventCurrencies().OrderBy(x => x).ToList();
            OrderCurrencyBox.ItemsSource = currencies;
            DonationCurrencyBox.ItemsSource = currencies;
        }

        private void WireDirtyHandlers()
        {
            TriggerEnabledCheck.Checked += (_, _) => MarkDirty();
            TriggerEnabledCheck.Unchecked += (_, _) => MarkDirty();
            EventTypeBox.SelectionChanged += (_, _) => MarkDirty();
            TierComboBox.SelectionChanged += (_, _) => MarkDirty();
            TierTextBox.TextChanged += (_, _) => MarkDirty();
            GiftCountBox.TextChanged += (_, _) => MarkDirty();
            TokenCountBox.TextChanged += (_, _) => MarkDirty();
            OrderByItemsRadio.Checked += (_, _) => MarkDirty();
            OrderByMoneyRadio.Checked += (_, _) => MarkDirty();
            OrderByOrderRadio.Checked += (_, _) => MarkDirty();
            OrderItemCountBox.TextChanged += (_, _) => MarkDirty();
            OrderMoneyBox.TextChanged += (_, _) => MarkDirty();
            OrderCurrencyBox.SelectionChanged += (_, _) => MarkDirty();
            OrderCurrencyBox.KeyUp += (_, _) => MarkDirty();
            DonationMoneyBox.TextChanged += (_, _) => MarkDirty();
            DonationCurrencyBox.SelectionChanged += (_, _) => MarkDirty();
            DonationCurrencyBox.KeyUp += (_, _) => MarkDirty();
            SpinsToAddBox.TextChanged += (_, _) => MarkDirty();
        }

        private void Grid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Keyboard.ClearFocus();
            (sender as Grid)?.Focus();
        }

        private void PopulateEventTypeComboBox()
        {
            EventTypeBox.Items.Clear();

            var groups = Enum.GetValues<SubathonEventType>()
                .Where(et => et.IsEnabled() &&
                             et.GetSubType() is { } st && ValidSubTypes.Contains(st))
                .GroupBy(et => et.GetSource())
                .OrderBy(g => g.Key.GetGroupLabelOrder());

            foreach (var group in groups)
            {
                var header = new ComboBoxItem
                {
                    Content = group.Key.ToString(),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = System.Windows.Media.Brushes.CornflowerBlue,
                    IsEnabled = false, // trick to make groups :)
                    Focusable = false,
                    FontSize = 11,
                    Padding = new Thickness(6, 4, 6, 2)
                };
                EventTypeBox.Items.Add(header);

                foreach (var et in group.OrderBy(et => et.GetLabel()))
                {
                    EventTypeBox.Items.Add(new ComboBoxItem
                    {
                        Content = $"  {et.GetLabel()}", // fake indent
                        Tag = et,
                        Padding = new Thickness(6, 2, 6, 2)
                    });
                }
            }
        }

        private void LoadTriggerRows()
        {
            TriggersStack.Children.Clear();
            using var db = _factory.CreateDbContext();
            var triggers = db.WheelSpinTriggers
                .ToList()
                .OrderBy(t => t.EventType.GetSource().ToString())
                .ThenBy(t => t.EventType.GetLabel())
                .ThenBy(t => t.TierValue)
                .ToList();

            foreach (var trigger in triggers)
                TriggersStack.Children.Add(BuildTriggerRow(trigger));

            RefreshTriggerRowHighlight(_selectedTrigger);
            UpdateEditorState();
        }

        private Grid BuildTriggerRow(WheelSpinTrigger trigger)
        {
            var row = new Grid
            {
                Margin = new Thickness(4, 0, 4, 4),
                Tag = trigger,
                MinHeight = 30
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

            var enabledCheck = new CheckBox
            {
                IsChecked = trigger.IsEnabled,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(-12, 0, 0, 0),
                ToolTip = "Enable / disable this trigger"
            };
            enabledCheck.Checked += (_, _) => OnTriggerEnabledToggled(trigger, true);
            enabledCheck.Unchecked += (_, _) => OnTriggerEnabledToggled(trigger, false);
            Grid.SetColumn(enabledCheck, 0);

            var eventLabel = new TextBlock
            {
                Text = BuildTriggerEventLabel(trigger),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 4, 0),
                Cursor = Cursors.Hand,
                ToolTip = BuildTriggerEventLabel(trigger)
            };
            eventLabel.MouseLeftButtonUp += (_, _) => SelectTrigger(trigger, row);
            Grid.SetColumn(eventLabel, 1);

            var conditionLabel = new TextBlock
            {
                Text = BuildTriggerConditionLabel(trigger),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = System.Windows.Media.Brushes.CornflowerBlue,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = BuildTriggerConditionLabel(trigger)
            };
            Grid.SetColumn(conditionLabel, 2);

            var spinsLabel = new TextBlock
            {
                Text = $"+{trigger.SpinsToAdd}",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = System.Windows.Media.Brushes.MediumSeaGreen
            };
            Grid.SetColumn(spinsLabel, 3);

            var deleteBtn = new Wpf.Ui.Controls.Button
            {
                Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24 },
                Width = 30, Height = 30, Padding = new Thickness(2),
                Foreground = System.Windows.Media.Brushes.Red,
                Cursor = Cursors.Hand,
                ToolTip = "Delete trigger"
            };
            deleteBtn.Click += (_, _) => DeleteTrigger_Click(trigger);
            Grid.SetColumn(deleteBtn, 4);

            row.Children.Add(enabledCheck);
            row.Children.Add(eventLabel);
            row.Children.Add(conditionLabel);
            row.Children.Add(spinsLabel);
            row.Children.Add(deleteBtn);

            return row;
        }

        private static string BuildTriggerEventLabel(WheelSpinTrigger t)
        {
            var label = $"{t.EventType.GetSource()} - {t.EventType.GetLabel()}";
            if (!string.IsNullOrEmpty(t.TierValue))
            {
                var display = t.EventType is SubathonEventType.TwitchSub or SubathonEventType.TwitchGiftSub
                    ? TwitchTierDisplay(t.TierValue)
                    : t.TierValue;
                label += $" ({display})";
            }
            return label;
        }

        private static string TwitchTierDisplay(string tierValue) => tierValue switch
        {
            // if i had a dollar for every time im doing this instead of making a dang helper...
            "1000" => "T1",
            "2000" => "T2",
            "3000" => "T3",
            _ => tierValue
        };

        private static string BuildTriggerConditionLabel(WheelSpinTrigger t)
        {
            var subType = t.EventType.GetSubType();
            return subType switch
            {
                SubathonEventSubType.SubLike => "per sub",
                SubathonEventSubType.GiftSubLike when t.CountThreshold.HasValue
                    => $"{t.CountThreshold} gifts",
                SubathonEventSubType.GiftSubLike => "per gift",
                SubathonEventSubType.TokenLike when t.CountThreshold.HasValue
                    => $"{t.CountThreshold} tokens",
                SubathonEventSubType.TokenLike => "per token",
                SubathonEventSubType.DonationLike when t.MoneyThreshold.HasValue
                    => $"{t.MoneyThreshold:F2} {t.Currency}",
                SubathonEventSubType.OrderLike when t.CountThreshold.HasValue
                    => $"{t.CountThreshold} items",
                SubathonEventSubType.OrderLike when t.MoneyThreshold.HasValue
                    => $"{t.MoneyThreshold:F2} {t.Currency}",
                SubathonEventSubType.OrderLike => "per order",
                _ => "-"
            };
        }

        private void RefreshTriggerRowHighlight(WheelSpinTrigger? selected)
        {
            foreach (var child in TriggersStack.Children.OfType<Grid>())
            {
                var t = child.Tag as WheelSpinTrigger;
                child.Background = (t?.Id == selected?.Id) ? SelectedRowBrush : System.Windows.Media.Brushes.Transparent;
            }
        }

        private void SelectTrigger(WheelSpinTrigger trigger, Grid? row = null)
        {
            _selectedTrigger = trigger;
            _isNewTrigger = false;
            _isDirty = false;
            TriggerStatusText.Text = "";
            ShowEditor(true);
            PopulateEditor(trigger);
            UpdateSaveButtonState();
            RefreshTriggerRowHighlight(trigger);
        }

        private void ShowEditor(bool show)
        {
            TriggerDetailBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TriggerPlaceholderText.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        }

        private void PopulateEditor(WheelSpinTrigger trigger)
        {
            SuppressChanges(() =>
            {
                TriggerEnabledCheck.IsChecked = trigger.IsEnabled;

                var match = EventTypeBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => i.Tag is SubathonEventType et && et == trigger.EventType);
                EventTypeBox.SelectedItem = match;
                UpdateEditorPanels(trigger.EventType);
            });
            EventTypeSourceLabel.Text = trigger.EventType.GetSource().ToString();

            SuppressChanges(() =>
            {
                var subType = trigger.EventType.GetSubType();
                bool isTwitchTier = TwitchTierTypes.Contains(trigger.EventType);
                bool isPicartoTier = PicartoTierTypes.Contains(trigger.EventType);

                if (isTwitchTier || isPicartoTier)
                {
                    var tierItem = TierComboBox.Items.OfType<ComboBoxItem>()
                        .FirstOrDefault(i => (string)i.Tag == (trigger.TierValue ?? ""));
                    TierComboBox.SelectedItem = tierItem ?? TierComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
                }
                else
                {
                    TierTextBox.Text = trigger.TierValue ?? "";
                }

                if (subType == SubathonEventSubType.GiftSubLike)
                    GiftCountBox.Text = trigger.CountThreshold?.ToString() ?? "";

                if (subType == SubathonEventSubType.TokenLike)
                    TokenCountBox.Text = trigger.CountThreshold?.ToString() ?? "";

                if (subType == SubathonEventSubType.OrderLike)
                {
                    bool byItems = trigger.CountThreshold.HasValue;
                    bool byMoney = !byItems && trigger.MoneyThreshold.HasValue;
                    OrderByItemsRadio.IsChecked = byItems;
                    OrderByMoneyRadio.IsChecked = byMoney;
                    OrderByOrderRadio.IsChecked = !byItems && !byMoney;
                    UpdateOrderModePanel(trigger.EventType);
                    OrderItemCountBox.Text = trigger.CountThreshold?.ToString() ?? "";
                    OrderMoneyBox.Text = trigger.MoneyThreshold?.ToString("F2") ?? "";
                    OrderCurrencyBox.Text = trigger.Currency ?? "";
                }

                if (subType == SubathonEventSubType.DonationLike)
                {
                    DonationMoneyBox.Text = trigger.MoneyThreshold?.ToString("F2") ?? "";
                    DonationCurrencyBox.Text = trigger.Currency ?? "";
                }

                SpinsToAddBox.Text = trigger.SpinsToAdd.ToString();
            });
        }

        private void UpdateEditorState()
        {
            bool hasSelection = _selectedTrigger != null || _isNewTrigger;
            ShowEditor(hasSelection);
            UpdateSaveButtonState();
        }

        private void UpdateEditorPanels(SubathonEventType? eventType)
        {
            if (eventType == null)
            {
                TierPanel.Visibility = Visibility.Collapsed;
                GiftCountPanel.Visibility = Visibility.Collapsed;
                TokenCountPanel.Visibility = Visibility.Collapsed;
                OrderModePanel.Visibility = Visibility.Collapsed;
                OrderByItemsRadio.IsEnabled = true;
                OrderByOrderRadio.IsEnabled = true;
                OrderItemCountBox.IsEnabled = true;
                DonationPanel.Visibility = Visibility.Collapsed;
                return;
            }

            var subType = eventType.GetSubType();
            bool isSubLike = subType is SubathonEventSubType.SubLike or SubathonEventSubType.GiftSubLike;
            bool isGift = eventType.IsGift();
            bool isTwitchTier = TwitchTierTypes.Contains(eventType.Value);
            bool isPicartoTier = PicartoTierTypes.Contains(eventType.Value);

            TierPanel.Visibility = isSubLike ? Visibility.Visible : Visibility.Collapsed;
            if (isSubLike)
            {
                bool useCombo = isTwitchTier || isPicartoTier; // hardset ones
                TierComboPanel.Visibility = useCombo ? Visibility.Visible : Visibility.Collapsed;
                TierTextPanel.Visibility = useCombo ? Visibility.Collapsed : Visibility.Visible;

                if (useCombo)
                    PopulateTierComboBox(eventType.Value);
            }

            GiftCountPanel.Visibility = isGift ? Visibility.Visible : Visibility.Collapsed;

            TokenCountPanel.Visibility = subType == SubathonEventSubType.TokenLike
                ? Visibility.Visible : Visibility.Collapsed;

            if (subType == SubathonEventSubType.OrderLike)
            {
                OrderModePanel.Visibility = Visibility.Visible;
                UpdateOrderModePanel(eventType.Value);
            }
            else
            {
                OrderModePanel.Visibility = Visibility.Collapsed;
                OrderByItemsRadio.IsEnabled = true;
                OrderByOrderRadio.IsEnabled = true;
                OrderItemCountBox.IsEnabled = true;
            }

            DonationPanel.Visibility = subType == SubathonEventSubType.DonationLike
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PopulateTierComboBox(SubathonEventType eventType)
        {
            TierComboBox.Items.Clear();

            if (TwitchTierTypes.Contains(eventType))
            {
                TierComboBox.Items.Add(new ComboBoxItem { Content = "T1", Tag = "1000" });
                TierComboBox.Items.Add(new ComboBoxItem { Content = "T2", Tag = "2000" });
                TierComboBox.Items.Add(new ComboBoxItem { Content = "T3", Tag = "3000" });
            }
            else if (eventType == SubathonEventType.PicartoSub)
            {
                TierComboBox.Items.Add(new ComboBoxItem { Content = "T1", Tag = "T1" });
                TierComboBox.Items.Add(new ComboBoxItem { Content = "T2", Tag = "T2" });
                TierComboBox.Items.Add(new ComboBoxItem { Content = "T3", Tag = "T3" });
            }
            else if (eventType == SubathonEventType.PicartoGiftSub)
            {
                TierComboBox.Items.Add(new ComboBoxItem { Content = "T1", Tag = "T1" });
            }

            if (TierComboBox.Items.Count > 0)
                TierComboBox.SelectedIndex = 0;
        }

        private void UpdateOrderModePanel(SubathonEventType? eventType = null)
        {
            if (OrderItemPanel == null || OrderMoneyPanel == null) return;

            eventType ??= (EventTypeBox.SelectedItem as ComboBoxItem)?.Tag as SubathonEventType?;

            bool forceByMoney = eventType == SubathonEventType.ThroneGiftContribution;
            bool noItemCount = eventType is SubathonEventType.ThroneGiftPurchase
                                         or SubathonEventType.KoFiCommissionOrder;

            if (forceByMoney)
            {
                SuppressChanges(() => OrderByMoneyRadio.IsChecked = true);
                OrderByItemsRadio.IsEnabled = false;
                OrderByOrderRadio.IsEnabled = false;
            }
            else if (noItemCount)
            {
                OrderByItemsRadio.IsEnabled = false;
                OrderByOrderRadio.IsEnabled = true;
                if (OrderByItemsRadio.IsChecked == true)
                    SuppressChanges(() => OrderByOrderRadio.IsChecked = true);
            }
            else
            {
                OrderByItemsRadio.IsEnabled = true;
                OrderByOrderRadio.IsEnabled = true;
            }

            bool byItems = OrderByItemsRadio.IsChecked == true;
            bool byMoney = OrderByMoneyRadio.IsChecked == true;

            OrderItemPanel.Visibility = byItems ? Visibility.Visible : Visibility.Collapsed;
            OrderMoneyPanel.Visibility = byMoney ? Visibility.Visible : Visibility.Collapsed;
        }
        

        private void AddTrigger_Click(object sender, RoutedEventArgs e)
        {
            _selectedTrigger = null;
            _isNewTrigger = true;
            _isDirty = false;
            TriggerStatusText.Text = "";
            ShowEditor(true);
            RefreshTriggerRowHighlight(null);

            EventTypeSourceLabel.Text = "";
            SuppressChanges(() =>
            {
                TriggerEnabledCheck.IsChecked = true;
                EventTypeBox.SelectedItem = null;
                TierTextBox.Text = "";
                GiftCountBox.Text = "";
                TokenCountBox.Text = "";
                OrderItemCountBox.Text = "";
                OrderMoneyBox.Text = "";
                var defaultCurrency = _config.Get("Currency", "Primary", "USD") ?? "USD";
                OrderCurrencyBox.Text = defaultCurrency;
                DonationMoneyBox.Text = "";
                DonationCurrencyBox.Text = defaultCurrency;
                SpinsToAddBox.Text = "1";
            });
            UpdateEditorPanels(null);
            UpdateSaveButtonState();
        }

        private async void DeleteTrigger_Click(WheelSpinTrigger trigger)
        {
            var msgBox = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Delete Trigger",
                Content = new TextBlock
                {
                    Text = "Delete this trigger? All associated trigger history will also be permanently deleted.",
                    TextWrapping = TextWrapping.Wrap,
                    Width = 320,
                    Margin = new Thickness(4, 4, 4, 8)
                },
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var result = await msgBox.ShowDialogAsync();
            if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

            await using var db = await _factory.CreateDbContextAsync();
            var tracked = await db.WheelSpinTriggers.FindAsync(trigger.Id);
            if (tracked != null)
                db.WheelSpinTriggers.Remove(tracked);
            await db.SaveChangesAsync();

            if (_selectedTrigger?.Id == trigger.Id)
            {
                _selectedTrigger = null;
                _isNewTrigger = false;
            }

            await Dispatcher.InvokeAsync(LoadTriggerRows);
            await Dispatcher.InvokeAsync(async () => await LoadHistoryAsync(reset: true));
            WheelEvents.RaiseWheelSpinTriggersChanged();
        }

        private async void SaveTrigger_Click(object sender, RoutedEventArgs e)
        {
            TriggerStatusText.Text = "";

            var eventTypeItem = EventTypeBox.SelectedItem as ComboBoxItem;
            if (eventTypeItem?.Tag is not SubathonEventType eventType)
            {
                TriggerStatusText.Text = "Select an event type";
                return;
            }

            if (!int.TryParse(SpinsToAddBox.Text, out int spinsToAdd) || spinsToAdd < 1)
            {
                TriggerStatusText.Text = "Spins to Add must be a whole number ≥ 1";
                return;
            }

            var subType = eventType.GetSubType();
            bool isTwitchTier = TwitchTierTypes.Contains(eventType);
            bool isPicartoTier = PicartoTierTypes.Contains(eventType);
            bool isSubLike = subType is SubathonEventSubType.SubLike or SubathonEventSubType.GiftSubLike;

            string? tierValue = null;
            if (isSubLike)
            {
                if (isTwitchTier || isPicartoTier)
                {
                    if (TierComboBox.SelectedItem is not ComboBoxItem tierItem)
                    {
                        TriggerStatusText.Text = "Select a tier";
                        return;
                    }
                    tierValue = tierItem.Tag as string;
                }
                else
                {
                    tierValue = TierTextBox.Text.Trim();
                    if (string.IsNullOrEmpty(tierValue))
                    {
                        TriggerStatusText.Text = "Enter a tier name (or DEFAULT for unknown memberships if not setup)";
                        return;
                    }
                }
            }

            // Build threshold fields
            int? countThreshold = null;
            double? moneyThreshold = null;
            string? currency = null;

            switch (subType)
            {
                case SubathonEventSubType.GiftSubLike:
                    if (!string.IsNullOrWhiteSpace(GiftCountBox.Text))
                    {
                        if (!int.TryParse(GiftCountBox.Text, out int gc) || gc < 1)
                        {
                            TriggerStatusText.Text = "Gift count must be a whole number ≥ 1 (or leave blank for 1 per gift)";
                            return;
                        }
                        countThreshold = gc;
                    }
                    break;

                case SubathonEventSubType.TokenLike:
                    if (string.IsNullOrWhiteSpace(TokenCountBox.Text) ||
                        !int.TryParse(TokenCountBox.Text, out int tc) || tc < 1)
                    {
                        TriggerStatusText.Text = "Token count must be a whole number ≥ 1";
                        return;
                    }
                    countThreshold = tc;
                    break;

                case SubathonEventSubType.OrderLike:
                    if (OrderByItemsRadio.IsChecked == true)
                    {
                        if (string.IsNullOrWhiteSpace(OrderItemCountBox.Text) ||
                            !int.TryParse(OrderItemCountBox.Text, out int ic) || ic < 1)
                        {
                            TriggerStatusText.Text = "Item count must be a whole number ≥ 1";
                            return;
                        }
                        countThreshold = ic;
                    }
                    else if (OrderByMoneyRadio.IsChecked == true)
                    {
                        if (string.IsNullOrWhiteSpace(OrderMoneyBox.Text) ||
                            !double.TryParse(OrderMoneyBox.Text, out double om) || om <= 0)
                        {
                            TriggerStatusText.Text = "Order amount must be a positive number";
                            return;
                        }
                        currency = OrderCurrencyBox.Text.Trim().ToUpperInvariant();
                        if (currency.Length < 2)
                        {
                            TriggerStatusText.Text = "Enter a valid currency code (e.g. USD)";
                            return;
                        }
                        moneyThreshold = om;
                    }
                    break;

                case SubathonEventSubType.DonationLike:
                    if (string.IsNullOrWhiteSpace(DonationMoneyBox.Text) ||
                        !double.TryParse(DonationMoneyBox.Text, out double dm) || dm <= 0)
                    {
                        TriggerStatusText.Text = "Donation amount must be a positive number";
                        return;
                    }
                    currency = DonationCurrencyBox.Text.Trim().ToUpperInvariant();
                    if (currency.Length < 2)
                    {
                        TriggerStatusText.Text = "Enter a valid currency code (e.g. USD)";
                        return;
                    }
                    moneyThreshold = dm;
                    break;
            }

            // unique check
            await using var db = await _factory.CreateDbContextAsync();
            bool isDuplicateTierEvent = subType is SubathonEventSubType.SubLike or SubathonEventSubType.GiftSubLike;

            var existing = await db.WheelSpinTriggers
                .Where(t => t.EventType == eventType && t.Id != (_selectedTrigger != null ? _selectedTrigger.Id : Guid.Empty))
                .ToListAsync();

            if (isDuplicateTierEvent)
            {
                bool tierConflict = existing.Any(t =>
                    string.Equals(t.TierValue, tierValue, StringComparison.OrdinalIgnoreCase));
                if (tierConflict)
                {
                    TriggerStatusText.Text = $"A trigger for {eventType.GetLabel()} ({tierValue}) already exists. Edit or delete it first";
                    return;
                }
            }
            else
            {
                if (existing.Count > 0)
                {
                    TriggerStatusText.Text = $"A trigger for {eventType.GetSource()} {eventType.GetLabel()} already exists. Only one trigger is allowed per event type";
                    return;
                }
            }

            if (_isNewTrigger)
            {
                var trigger = new WheelSpinTrigger
                {
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
            else if (_selectedTrigger != null)
            {
                var tracked = await db.WheelSpinTriggers.FindAsync(_selectedTrigger.Id);
                if (tracked == null)
                {
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
            await Dispatcher.InvokeAsync(LoadTriggerRows);
            WheelEvents.RaiseWheelSpinTriggersChanged();
            TriggerStatusText.Text = "";
            if (_selectedTrigger != null)
            {
                ShowEditor(true);
                PopulateEditor(_selectedTrigger);
                RefreshTriggerRowHighlight(_selectedTrigger);
            }
            UpdateSaveButtonState();
        }

        private async void OnTriggerEnabledToggled(WheelSpinTrigger trigger, bool enabled)
        {
            if (_suppressCount > 0) return;

            trigger.IsEnabled = enabled;
            await using var db = await _factory.CreateDbContextAsync();
            var tracked = await db.WheelSpinTriggers.FindAsync(trigger.Id);
            if (tracked == null) return;
            tracked.IsEnabled = enabled;
            await db.SaveChangesAsync();

            if (_selectedTrigger?.Id == trigger.Id)
                SuppressChanges(() => TriggerEnabledCheck.IsChecked = enabled);
        }

        private async Task LoadHistoryAsync(bool reset = false)
        {
            if (_historyLoading) return;
            _historyLoading = true;

            if (reset)
            {
                _historyOffset = 0;
                await Dispatcher.InvokeAsync(() => TriggerHistoryStack.Children.Clear());
            }

            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var rows = await db.WheelSpinTriggerHistories
                    .Include(h => h.Trigger)
                    .OrderByDescending(h => h.TriggeredAt)
                    .Skip(_historyOffset)
                    .Take(HistoryPageSize)
                    .ToListAsync();

                if (rows.Count == 0)
                {
                    _historyLoading = false;
                    return;
                }

                _historyOffset += rows.Count;

                await Dispatcher.InvokeAsync(() =>
                {
                    foreach (var h in rows)
                        TriggerHistoryStack.Children.Add(BuildHistoryRow(h));
                });
            }
            finally
            {
                _historyLoading = false;
            }
        }

        private static Grid BuildHistoryRow(WheelSpinTriggerHistory h)
        {
            var row = new Grid { Margin = new Thickness(2, 0, 4, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(225) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });

            var timeLabel = new TextBlock
            {
                Text = h.TriggeredAt.ToString("yyyy-MM-dd HH:mm:ss"),
                FontSize = 11,
                Foreground = System.Windows.Media.Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(timeLabel, 0);

            string eventDesc = h.Trigger != null
                ? BuildTriggerEventLabel(h.Trigger)
                : h.SubathonEventType?.GetLabel() ?? "Unknown";
            var eventLabel = new TextBlock
            {
                Text = eventDesc,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 4, 0),
                ToolTip = eventDesc
            };
            Grid.SetColumn(eventLabel, 1);

            var userLabel = new TextBlock
            {
                Text = h.TriggerUser ?? "-",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 4, 0),
                ToolTip = h.TriggerUser
            };
            Grid.SetColumn(userLabel, 2);

            var spinsLabel = new TextBlock
            {
                Text = $"+{h.SpinsAdded}",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.MediumSeaGreen,
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

        private void TriggerHistoryScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_historyLoading) return;
            if (TriggerHistoryScroller.ScrollableHeight > 0 &&
                TriggerHistoryScroller.ScrollableHeight - TriggerHistoryScroller.VerticalOffset < 100)
                _ = LoadHistoryAsync();
        }

        private async void ExportHistoryToCsv_Click(object sender, RoutedEventArgs e)
        {
            await using var db = await _factory.CreateDbContextAsync();
            var rows = await db.WheelSpinTriggerHistories
                .Include(h => h.Trigger)
                .OrderByDescending(h => h.TriggeredAt)
                .ToListAsync();

            string exportDir = Path.Combine(Config.DataFolder, "exports");
            Directory.CreateDirectory(exportDir);
            string filepath = Path.Combine(exportDir, $"wheel-trigger-history-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

            var sb = new StringBuilder();
            sb.AppendLine("Id,TriggerId,TriggerEventType,TriggeredAt,TriggerUser,TriggerSource,SpinsAdded,SubathonEventId,SubathonEventType");
            foreach (var h in rows)
            {
                var eventLabel = h.Trigger?.EventType.GetLabel() ?? h.SubathonEventType?.GetLabel() ?? "";
                var user = h.TriggerUser?.Replace("\"", "\"\"") ?? "";
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

            try
            {
                Process.Start(new ProcessStartInfo { FileName = exportDir, UseShellExecute = true, Verb = "open" });
            }
            catch { /**/ }
        }

        private async void ExportTriggers_Click(object sender, RoutedEventArgs e)
        {
            await using var db = await _factory.CreateDbContextAsync();
            var triggers = await db.WheelSpinTriggers.AsNoTracking().ToListAsync();

            string exportDir = Path.Combine(Config.DataFolder, "exports");
            Directory.CreateDirectory(exportDir);
            string filepath = Path.Combine(exportDir, $"wheel-triggers-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

            var sb = new StringBuilder();
            sb.AppendLine("Enabled,SpinsToAdd,EventType,TierValue,CountThreshold,MoneyThreshold,Currency");
            foreach (var t in triggers)
            {
                sb.AppendLine(string.Join(",",
                    t.IsEnabled,
                    t.SpinsToAdd,
                    t.EventType,
                    Utils.EscapeCsv(t.TierValue ?? ""),
                    t.CountThreshold?.ToString() ?? "",
                    t.MoneyThreshold?.ToString("G") ?? "",
                    Utils.EscapeCsv(t.Currency ?? "")
                ));
            }

            await File.WriteAllTextAsync(filepath, sb.ToString(), Encoding.UTF8);

            try
            {
                Process.Start(new ProcessStartInfo { FileName = exportDir, UseShellExecute = true, Verb = "open" });
            }
            catch { /**/ }
        }

        private async void ImportTriggers_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import Triggers",
                Filter = "CSV Files (*.csv)|*.csv",
                DefaultExt = "csv"
            };

            if (dlg.ShowDialog() != true) return;

            string[] lines;
            try { lines = await File.ReadAllLinesAsync(dlg.FileName, Encoding.UTF8); }
            catch { await ShowInvalidTriggerCsvPopup(); return; }

            if (lines.Length < 1) { await ShowInvalidTriggerCsvPopup(); return; }

            var headerCols = ParseTriggerCsvLine(lines[0]);
            if (headerCols.Length < 3) { await ShowInvalidTriggerCsvPopup(); return; }

            // validate ALL rows
            var parsed = new List<WheelSpinTrigger>();
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var cols = ParseTriggerCsvLine(lines[i]);

                if (cols.Length < 3
                    || !bool.TryParse(cols[0].Trim(), out bool enabled)
                    || !int.TryParse(cols[1].Trim(), out int spins)
                    || !Enum.TryParse<SubathonEventType>(cols[2].Trim(), out var eventType))
                { await ShowInvalidTriggerCsvPopup(); return; }

                string? tierValue = cols.Length > 3 && !string.IsNullOrWhiteSpace(cols[3]) ? cols[3].Trim() : null;

                int? countThreshold = null;
                if (cols.Length > 4 && !string.IsNullOrWhiteSpace(cols[4]))
                {
                    if (!int.TryParse(cols[4].Trim(), out int ct)) { await ShowInvalidTriggerCsvPopup(); return; }
                    countThreshold = ct;
                }

                double? moneyThreshold = null;
                if (cols.Length > 5 && !string.IsNullOrWhiteSpace(cols[5]))
                {
                    if (!double.TryParse(cols[5].Trim(), System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double mt))
                    { await ShowInvalidTriggerCsvPopup(); return; }
                    moneyThreshold = mt;
                }

                string? currency = cols.Length > 6 && !string.IsNullOrWhiteSpace(cols[6]) ? cols[6].Trim() : null;

                parsed.Add(new WheelSpinTrigger
                {
                    IsEnabled = enabled,
                    SpinsToAdd = spins,
                    EventType = eventType,
                    TierValue = tierValue,
                    CountThreshold = countThreshold,
                    MoneyThreshold = moneyThreshold,
                    Currency = currency
                });
            }

            await using var db = await _factory.CreateDbContextAsync();
            await db.WheelSpinTriggerHistories.ExecuteDeleteAsync();
            await db.WheelSpinTriggers.ExecuteDeleteAsync();

            db.WheelSpinTriggers.AddRange(parsed);
            await db.SaveChangesAsync();

            _selectedTrigger = null;
            _isNewTrigger = false;
            await Dispatcher.InvokeAsync(LoadTriggerRows);
            await Dispatcher.InvokeAsync(async () => await LoadHistoryAsync(reset: true));
            WheelEvents.RaiseWheelSpinTriggersChanged();
        }

        private static string[] ParseTriggerCsvLine(string line)
        {
            var result = new List<string>();
            var field = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    switch (c)
                    {
                        case '"' when i + 1 < line.Length && line[i + 1] == '"':
                            field.Append('"'); i++;
                            break;
                        case '"':
                            inQuotes = false;
                            break;
                        default:
                            field.Append(c);
                            break;
                    }
                }
                else
                {
                    switch (c)
                    {
                        case '"':
                            inQuotes = true;
                            break;
                        case ',':
                            result.Add(field.ToString()); field.Clear();
                            break;
                        default:
                            field.Append(c);
                            break;
                    }
                }
            }
            result.Add(field.ToString());
            return result.ToArray();
        }

        private static async Task ShowInvalidTriggerCsvPopup()
        {
            var msgBox = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Invalid CSV",
                Content = new TextBlock
                {
                    Text = "The selected file is not a valid trigger CSV and could not be imported.",
                    TextWrapping = TextWrapping.Wrap,
                    Width = 300,
                    Margin = new Thickness(4, 4, 4, 4)
                },
                CloseButtonText = "OK",
                Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            await msgBox.ShowDialogAsync();
        }

        private async void DeleteAllTriggerHistory_Click(object sender, RoutedEventArgs e)
        {
            var msgBox = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Delete All Trigger History",
                Content = new TextBlock
                {
                    Text = "Are you sure you want to delete all trigger history?",
                    TextWrapping = TextWrapping.Wrap,
                    Width = 320,
                    Margin = new Thickness(4, 4, 4, 8)
                },
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var result = await msgBox.ShowDialogAsync();
            if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

            await using var db = await _factory.CreateDbContextAsync();
            await db.WheelSpinTriggerHistories.ExecuteDeleteAsync();

            await Dispatcher.InvokeAsync(async () => await LoadHistoryAsync(reset: true));
        }

        private void OnTriggerFired(WheelSpinTrigger trigger, WheelSpinTriggerHistory history, int newSpinsOwed)
        {
            Dispatcher.InvokeAsync(() =>
            {
                TriggerHistoryStack.Children.Insert(0, BuildHistoryRow(history));
                _historyOffset++;
                while (TriggerHistoryStack.Children.Count > HistoryPageSize * 10)
                    TriggerHistoryStack.Children.RemoveAt(TriggerHistoryStack.Children.Count - 1);
            });
        }

        private void OnTriggersChanged()
        {
            Dispatcher.InvokeAsync(LoadTriggerRows);
        }

        private void EventTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCount > 0) return;
            var et = (EventTypeBox.SelectedItem as ComboBoxItem)?.Tag as SubathonEventType?;
            UpdateEditorPanels(et);
            TriggerStatusText.Text = "";
            EventTypeSourceLabel.Text = et.HasValue ? et.Value.GetSource().ToString() : "";
        }

        private void OrderMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressCount > 0) return;
            UpdateOrderModePanel();
        }

        private void NumberOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
            => e.Handled = !e.Text.All(char.IsDigit);

        private void FloatOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
            => e.Handled = !e.Text.All(c => char.IsDigit(c) || c == '.');
    }
}
