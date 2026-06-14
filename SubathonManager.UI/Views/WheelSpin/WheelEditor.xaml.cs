using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views.WheelSpin
{
    public partial class WheelEditor
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly ILogger<WheelEditor> _logger;
        private readonly IConfig _config;
        private WheelSet? _activeWheel;
        private WheelItem? _selectedItem;
        private int _suppressCount = 0;
        private int _spinsOwed = 0;
        private bool _isSpinning = false;
        private int _historyOffset = 0;
        private const int HistoryPageSize = 10;
        private bool _historyLoading = false;
        private WheelSpinHistoryStatus? _historyFilter = null;
        private volatile bool _multiplierActive = false;
        private int _multiplierRefreshQueued = 0;

        public WheelEditor()
        {
            _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            _config = AppServices.Provider.GetRequiredService<IConfig>();
            _logger = AppServices.Provider.GetRequiredService<ILogger<WheelEditor>>();
            InitializeComponent();
            PopulateActionTypeComboBox();
            LoadActiveWheel();
            LoadGlobalState();
            Loaded += (_, _) => Dispatcher.Invoke(AttachChangeHandlers);
            SubathonEvents.SubathonDataUpdate += OnSubathonDataUpdate;
            Unloaded += (_, _) => SubathonEvents.SubathonDataUpdate -= OnSubathonDataUpdate;
        }

        private void LoadGlobalState()
        {
            int delay = int.TryParse(_config.Get("WheelSpin", "SpinDelaySeconds", "4"), out int d) ? Math.Max(0, d) : 4;
            SuppressChanges(() => SpinDelayBox.Text = delay.ToString());

            using var db = _factory.CreateDbContext();
            _spinsOwed = StateValueHelper.Get<int>(db, StateKeys.WheelSpinsOwed);
            SuppressChanges(() => SpinsOwedBox.Text = _spinsOwed.ToString());

            var subathon = db.SubathonDatas
                .Include(s => s.Multiplier)
                .AsNoTracking()
                .FirstOrDefault(s => s.IsActive);
            _multiplierActive = subathon?.Multiplier?.IsRunning() ?? false;
        }

        private void PopulateActionTypeComboBox()
        {
            ActionTypeBox.Items.Clear();
            foreach (var t in Enum.GetValues<WheelSpinActionType>())
            {
                ActionTypeBox.Items.Add(new ComboBoxItem
                {
                    Content = t.GetLabel(),
                    Tag = (WheelSpinActionType?)t
                });
            }
            ActionTypeBox.SelectedIndex = 0;
        }

        private void LoadActiveWheel()
        {
            using var db = _factory.CreateDbContext();
            var allWheels = db.WheelSets.OrderBy(w => w.Name).ToList();
            if (allWheels.Count == 0)
            {
                StatusText.Text = "No wheels found";
                return;
            }

            SuppressChanges(() =>
            {
                WheelSelectorBox.Items.Clear();
                foreach (var w in allWheels)
                    WheelSelectorBox.Items.Add(new ComboBoxItem { Content = w.Name, Tag = w.Id });
            });

            var activeWheel = allWheels.FirstOrDefault(w => w.IsActive) ?? allWheels.First();
            SuppressChanges(() =>
            {
                var item = WheelSelectorBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => (Guid)i.Tag == activeWheel.Id);
                WheelSelectorBox.SelectedItem = item;
            });

            LoadWheelById(activeWheel.Id);
        }

        private void LoadWheelById(Guid wheelId)
        {
            using var db = _factory.CreateDbContext();
            _activeWheel = db.WheelSets
                .Include(w => w.WheelItems)
                .ThenInclude(i => i.Action)
                .FirstOrDefault(w => w.Id == wheelId);

            if (_activeWheel == null)
            {
                StatusText.Text = "Wheel not found";
                return;
            }

            StatusText.Text = "";
            _selectedItem = null;
            ItemDetailBorder.Visibility = Visibility.Collapsed;

            SuppressChanges(() =>
            {
                WheelNameTextBox.Text = _activeWheel.Name;
                SpinCountBox.Text = _activeWheel.SpinCount.ToString();
            });

            using var dbActive = _factory.CreateDbContext();
            foreach (var w in dbActive.WheelSets.Where(w => w.Id != wheelId))
                w.IsActive = false;
            var activeTracked = dbActive.WheelSets.Find(wheelId);
            activeTracked?.IsActive = true;
            dbActive.SaveChanges();

            using var db2 = _factory.CreateDbContext();
            DeleteWheelBtn.IsEnabled = db2.WheelSets.Count() > 1;

            Dispatcher.InvokeAsync(LoadItemRows);
            Dispatcher.InvokeAsync(async () => await LoadHistoryAsync());
        }

        private void WheelSelectorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCount > 0) return;
            if (WheelSelectorBox.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not Guid wheelId) return;
            LoadWheelById(wheelId);
        }

        private async void WheelNameTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_activeWheel == null) return;
            var newName = WheelNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName) || newName == _activeWheel.Name) return;

            _activeWheel.Name = newName;

            await using var db = await _factory.CreateDbContextAsync();
            var tracked = await db.WheelSets.FindAsync(_activeWheel.Id);
            if (tracked == null) return;
            tracked.Name = newName;
            await db.SaveChangesAsync();

            SuppressChanges(() =>
            {
                var item = WheelSelectorBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => (Guid)i.Tag == _activeWheel.Id);
                item?.Content = newName;
            });
            RaiseWheelDataChanged();
        }

        private async void ResetSpinCount_Click(object sender, RoutedEventArgs e)
        {
            if (_activeWheel == null) return;

            await using var db = await _factory.CreateDbContextAsync();
            var tracked = await db.WheelSets.FindAsync(_activeWheel.Id);
            if (tracked == null) return;
            tracked.SpinCount = 0;
            await db.SaveChangesAsync();

            _activeWheel.SpinCount = 0;
            SuppressChanges(() => SpinCountBox.Text = "0");
            RaiseWheelDataChanged();
        }

        private async void SpinCountBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_activeWheel == null) return;
            if (!int.TryParse(SpinCountBox.Text, out int newCount)) return;
            newCount = Math.Max(0, newCount);
            if (newCount == _activeWheel.SpinCount) return;

            await using var db = await _factory.CreateDbContextAsync();
            var tracked = await db.WheelSets.FindAsync(_activeWheel.Id);
            if (tracked == null) return;
            tracked.SpinCount = newCount;
            await db.SaveChangesAsync();
            _activeWheel.SpinCount = newCount;
            SuppressChanges(() => SpinCountBox.Text = newCount.ToString());
            RaiseWheelDataChanged();
        }

        private void Grid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Keyboard.ClearFocus();
            (sender as Grid)?.Focus();
        }

        private async void NewWheel_Click(object sender, RoutedEventArgs e)
        {
            await using var db = await _factory.CreateDbContextAsync();
            var newWheel = new WheelSet
            {
                Name = "New Wheel",
                IsActive = false,
                SpinCount = 0
            };
            db.WheelSets.Add(newWheel);
            await db.SaveChangesAsync();

            var newItem = new ComboBoxItem { Content = newWheel.Name, Tag = newWheel.Id };
            SuppressChanges(() =>
            {
                WheelSelectorBox.Items.Add(newItem);
                WheelSelectorBox.SelectedItem = newItem;
            });

            LoadWheelById(newWheel.Id);
            DeleteWheelBtn.IsEnabled = true;
            WheelNameTextBox.Focus();
        }

        private async void DeleteWheel_Click(object sender, RoutedEventArgs e)
        {
            if (_activeWheel == null) return;

            await using var db = await _factory.CreateDbContextAsync();
            int total = await db.WheelSets.CountAsync();
            if (total <= 1) return;

            var deletingId = _activeWheel.Id;
            bool wasActive = _activeWheel.IsActive;

            var tracked = await db.WheelSets.FindAsync(deletingId);
            if (tracked != null)
                db.WheelSets.Remove(tracked);

            if (wasActive)
            {
                var next = await db.WheelSets
                    .Where(w => w.Id != deletingId)
                    .OrderBy(w => w.Name)
                    .FirstOrDefaultAsync();
                if (next != null) next.IsActive = true;
            }

            await db.SaveChangesAsync();

            SuppressChanges(() =>
            {
                var item = WheelSelectorBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => (Guid)i.Tag == deletingId);
                if (item != null) WheelSelectorBox.Items.Remove(item);
            });

            var remaining = db.WheelSets.OrderBy(w => w.Name).First();
            var selectItem = WheelSelectorBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (Guid)i.Tag == remaining.Id);
            SuppressChanges(() => WheelSelectorBox.SelectedItem = selectItem);

            LoadWheelById(remaining.Id);
            DeleteWheelBtn.IsEnabled = WheelSelectorBox.Items.Count > 1;
        }

        private async void LoadItemRows()
        {
            ItemsStack.Children.Clear();
            if (_activeWheel == null) return;

            await using var db = await _factory.CreateDbContextAsync();
            var items = await db.WheelItems
                .Include(i => i.Action)
                .Where(i => i.WheelId == _activeWheel.Id)
                .OrderBy(i => i.Index)
                .ToListAsync();

            _activeWheel.WheelItems = items;

            foreach (var item in items)
                ItemsStack.Children.Add(BuildItemRow(item));

            RefreshRowHighlights();
            RaiseWheelDataChanged();
        }

        private Grid BuildItemRow(WheelItem item)
        {
            var row = new Grid { Margin = new Thickness(4, 0, 4, 4), Tag = item, MinHeight = 30 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

            var enabledCheck = new CheckBox
            {
                IsChecked = item.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = "Enable / disable this item",
                Margin = new Thickness(-12, 0, 0, 0),
            };
            enabledCheck.Checked += (_, _) => OnRowEnabledToggled(item, enabledCheck, true);
            enabledCheck.Unchecked += (_, _) => OnRowEnabledToggled(item, enabledCheck, false);
            Grid.SetColumn(enabledCheck, 0);

            var textLabel = new TextBlock
            {
                Text = item.Text,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 4, 0),
                Cursor = Cursors.Hand,
                ToolTip = item.Text
            };
            textLabel.MouseLeftButtonUp += (_, _) => SelectItem(item, row);
            Grid.SetColumn(textLabel, 1);

            var weightLabel = new TextBlock
            {
                Text = item.Weight.ToString(),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = System.Windows.Media.Brushes.CornflowerBlue
            };
            Grid.SetColumn(weightLabel, 2);

            var qtyLabel = new TextBlock
            {
                Text = item.IsInfinite ? "∞" : item.Quantity.ToString(),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetColumn(qtyLabel, 3);

            string actionText = item.Action == null ? "M" : item.Action.ActionType switch
            {
                WheelSpinActionType.AddTime => "+Time",
                WheelSpinActionType.SubtractTime => "-Time",
                WheelSpinActionType.SetMultiplier => "Mult",
                WheelSpinActionType.Reroll => "Reroll",
                _ => "M"
            };
            var actionLabel = new TextBlock
            {
                Text = actionText,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = item.Action != null
                    ? System.Windows.Media.Brushes.MediumSeaGreen
                    : System.Windows.Media.Brushes.Gray,
                ToolTip = item.Action != null
                    ? $"{item.Action.ActionType}: {item.Action.Parameter}"
                    : "No action"
            };
            Grid.SetColumn(actionLabel, 4);

            var deleteBtn = new Wpf.Ui.Controls.Button
            {
                Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24 },
                Width = 30,
                Height = 30,
                Padding = new Thickness(2),
                Foreground = System.Windows.Media.Brushes.Red,
                Cursor = Cursors.Hand,
                ToolTip = "Delete item"
            };
            deleteBtn.Click += (_, _) => DeleteItem_Click(item);
            Grid.SetColumn(deleteBtn, 5);

            row.Children.Add(enabledCheck);
            row.Children.Add(textLabel);
            row.Children.Add(weightLabel);
            row.Children.Add(qtyLabel);
            row.Children.Add(actionLabel);
            row.Children.Add(deleteBtn);

            return row;
        }

        private async void OnRowEnabledToggled(WheelItem item, CheckBox checkBox, bool enabled)
        {
            if (_suppressCount > 0) return;

            if (enabled && !IsItemActionValid(item, out string err))
            {
                await Dispatcher.InvokeAsync(() => SuppressChanges(() => checkBox.IsChecked = false));
                StatusText.Text = $"Cannot enable: {err}";
                return;
            }

            item.Enabled = enabled;
            if (_selectedItem?.Id == item.Id) _selectedItem.Enabled = enabled;

            await using var db = await _factory.CreateDbContextAsync();
            var tracked = await db.WheelItems.FindAsync(item.Id);
            if (tracked == null) return;
            tracked.Enabled = enabled;
            await db.SaveChangesAsync();
            RaiseWheelDataChanged();
        }

        private void SelectItem(WheelItem item, Grid? clickedRow = null)
        {
            _selectedItem = item;
            StatusText.Text = "";
            ItemDetailBorder.Visibility = Visibility.Visible;

            SuppressChanges(() =>
            {
                ItemTextBox.Text = item.Text;
                ItemWeightBox.Text = item.Weight.ToString();
                ItemQuantityBox.Text = item.Quantity.ToString();
                ItemInfiniteCheck.IsChecked = item.IsInfinite;
                ItemQuantityBox.IsEnabled = !item.IsInfinite;

                var actionType = item.Action?.ActionType;
                var actionItem = ActionTypeBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => (WheelSpinActionType?)i.Tag == actionType);
                ActionTypeBox.SelectedItem = actionItem ?? ActionTypeBox.Items[0];

                bool showParams = actionType.HasValue && actionType.Value.HasAction();
                ActionParameterPanel.Visibility = showParams ? Visibility.Visible : Visibility.Collapsed;
                if (showParams)
                {
                    ShowActionPanelsFor(actionType!.Value);
                    switch (actionType.Value)
                    {
                        case WheelSpinActionType.SetMultiplier:
                            ParseMultiplierParameter(item.Action!.Parameter);
                            break;
                        case WheelSpinActionType.Reroll:
                            ParseRerollParameter(item.Action!.Parameter);
                            break;
                        default:
                            ActionParameterBox.Text = item.Action!.Parameter;
                            break;
                    }
                }
                else
                {
                    ActionParameterBox.Text = "";
                    MultiplierAmountBox.Text = "";
                    MultiplierDurationBox.Text = "";
                    MultiplierTimeCheck.IsChecked = false;
                    MultiplierPointsCheck.IsChecked = false;
                    RerollCountBox.Text = "";
                }
            });

            RefreshRowHighlights(clickedRow);
        }

        private void RefreshRowHighlights(Grid? clickedRow = null)
        {
            foreach (var child in ItemsStack.Children.OfType<Grid>())
            {
                bool isSelected = child == clickedRow || (child.Tag is WheelItem wi && wi.Id == _selectedItem?.Id);
                child.Background = isSelected
                    ? new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(30, 100, 149, 237))
                    : System.Windows.Media.Brushes.Transparent;
            }
        }

        private void ActionType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCount > 0) return;
            var selected = (ActionTypeBox.SelectedItem as ComboBoxItem)?.Tag as WheelSpinActionType?;
            bool showParams = selected.HasValue && selected.Value.HasAction();
            ActionParameterPanel.Visibility = showParams ? Visibility.Visible : Visibility.Collapsed;
            if (showParams)
            {
                ShowActionPanelsFor(selected!.Value);
                if (selected.Value == WheelSpinActionType.SetMultiplier
                    && !(MultiplierTimeCheck.IsChecked ?? false) && !(MultiplierPointsCheck.IsChecked ?? false))
                    SuppressChanges(() => MultiplierTimeCheck.IsChecked = true);
            }
            MarkPendingChanges();
        }

        private void ShowActionPanelsFor(WheelSpinActionType type)
        {
            bool isTime = type is WheelSpinActionType.AddTime or WheelSpinActionType.SubtractTime;
            bool isMult = type == WheelSpinActionType.SetMultiplier;
            bool isReroll = type == WheelSpinActionType.Reroll;
            TimeParamPanel.Visibility = isTime ? Visibility.Visible : Visibility.Collapsed;
            MultiplierParamPanel.Visibility = isMult ? Visibility.Visible : Visibility.Collapsed;
            RerollParamPanel.Visibility = isReroll ? Visibility.Visible : Visibility.Collapsed;
            if (isTime) UpdateActionHint(type);
        }

        private void UpdateActionHint(WheelSpinActionType type)
        {
            ActionHintText.Text = type switch
            {
                WheelSpinActionType.AddTime => "Duration to add. e.g. \"5m\", \"300s\", \"1h30m\".",
                WheelSpinActionType.SubtractTime => "Duration to subtract. e.g. \"5m\", \"300s\", \"1h30m\".",
                _ => ""
            };
        }

        private void ParseMultiplierParameter(string parameter)
        {
            var parts = parameter.Split('|');
            if (parts.Length < 4) return;
            MultiplierAmountBox.Text = parts[0];
            MultiplierDurationBox.Text = parts[1] == "xs" ? "" : parts[1];
            MultiplierPointsCheck.IsChecked = parts[2].Equals("True", StringComparison.OrdinalIgnoreCase);
            MultiplierTimeCheck.IsChecked = parts[3].Equals("True", StringComparison.OrdinalIgnoreCase);
        }

        private string BuildMultiplierParameter()
        {
            if (!double.TryParse(MultiplierAmountBox.Text,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double amount))
                amount = 1.0;
            TimeSpan duration = Utils.ParseDurationString(MultiplierDurationBox.Text.Trim());
            string durationStr = duration == TimeSpan.Zero ? "x" : ((int)duration.TotalSeconds).ToString();
            bool applyPoints = MultiplierPointsCheck.IsChecked ?? false;
            bool applyTime = MultiplierTimeCheck.IsChecked ?? false;
            return $"{amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{durationStr}s|{applyPoints}|{applyTime}";
        }

        private void Multiplier_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressCount > 0) return;
            MarkPendingChanges();
        }

        private void ParseRerollParameter(string parameter)
        {
            var parts = parameter.Split('|');
            RerollCountBox.Text = parts.Length >= 1 && int.TryParse(parts[0], out int c) && c >= 1 ? parts[0] : "1";
        }

        private string BuildRerollParameter()
        {
            if (!int.TryParse(RerollCountBox.Text.Trim(), out int count) || count < 1) count = 1;
            return count.ToString();
        }

        private void ItemInfinite_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressCount > 0) return;
            ItemQuantityBox.IsEnabled = !(ItemInfiniteCheck.IsChecked ?? false);
            MarkPendingChanges();
        }

        private async void AddItem_Click(object sender, RoutedEventArgs e)
        {
            if (_activeWheel == null) return;
            await SaveAsync(null, null);

            await using var db = await _factory.CreateDbContextAsync();
            int nextIndex = await db.WheelItems
                .Where(i => i.WheelId == _activeWheel.Id)
                .Select(i => (int?)i.Index)
                .MaxAsync() ?? 0;
            nextIndex++;

            var newItem = new WheelItem
            {
                WheelId = _activeWheel.Id,
                Text = "New Item",
                Enabled = false,
                Index = nextIndex
            };
            db.WheelItems.Add(newItem);
            await db.SaveChangesAsync();

            await Dispatcher.InvokeAsync(() =>
            {
                LoadItemRows();
                SelectItem(newItem);
            });
        }

        private async void DeleteItem_Click(WheelItem item)
        {
            if (_selectedItem?.Id == item.Id)
            {
                _selectedItem = null;
                ItemDetailBorder.Visibility = Visibility.Collapsed;
            }

            await using var db = await _factory.CreateDbContextAsync();
            var tracked = await db.WheelItems.FindAsync(item.Id);
            if (tracked != null)
                db.WheelItems.Remove(tracked);

            await db.SaveChangesAsync();
            await Dispatcher.InvokeAsync(LoadItemRows);
        }

        private async void Save_Click(object? sender, RoutedEventArgs? e)
            => await SaveAsync(sender, e);

        private async Task SaveAsync(object? sender, RoutedEventArgs? e)
        {
            if (_activeWheel == null) return;

            _activeWheel.Name = WheelNameTextBox.Text.Trim();

            if (_selectedItem != null)
                WriteDetailToItem(_selectedItem);

            await using var db = await _factory.CreateDbContextAsync();

            var trackedWheel = await db.WheelSets.FindAsync(_activeWheel.Id);
            if (trackedWheel != null)
            {
                trackedWheel.Name = _activeWheel.Name;
                db.Update(trackedWheel);
            }

            if (_selectedItem != null)
            {
                var trackedItem = await db.WheelItems
                    .Include(i => i.Action)
                    .FirstOrDefaultAsync(i => i.Id == _selectedItem.Id);

                if (trackedItem != null)
                {
                    CopyItemToTracked(_selectedItem, trackedItem);

                    var desiredActionType = (ActionTypeBox.SelectedItem as ComboBoxItem)?.Tag as WheelSpinActionType?;
                    bool isCommandAction = desiredActionType.HasValue && desiredActionType.Value.HasAction();

                    if (isCommandAction)
                    {
                        string param = desiredActionType!.Value switch
                        {
                            WheelSpinActionType.SetMultiplier => BuildMultiplierParameter(),
                            WheelSpinActionType.Reroll => BuildRerollParameter(),
                            _ => ActionParameterBox.Text.Trim()
                        };

                        if (trackedItem.Action == null)
                        {
                            db.WheelSpinActions.Add(new WheelSpinAction
                            {
                                WheelItemId = trackedItem.Id,
                                ActionType = desiredActionType.Value,
                                Parameter = param
                            });
                        }
                        else
                        {
                            trackedItem.Action.ActionType = desiredActionType.Value;
                            trackedItem.Action.Parameter = param;
                        }
                    }
                    else if (trackedItem.Action != null)
                    {
                        db.WheelSpinActions.Remove(trackedItem.Action);
                    }

                    if (!IsCurrentUiActionValid(out string saveValidErr))
                    {
                        trackedItem.Enabled = false;
                        _selectedItem?.Enabled = false;
                        UpdateRowEnabledCheckbox(trackedItem.Id, false);
                        StatusText.Text = $"Item disabled: {saveValidErr}";
                    }
                    else
                    {
                        StatusText.Text = "";
                    }

                    db.Update(trackedItem);
                }
            }

            await db.SaveChangesAsync();

            bool isExplicitSave = sender != null;
            if (isExplicitSave)
                await Dispatcher.InvokeAsync(LoadItemRows);

            UpdateSaveButtonBorder(false);
            await Dispatcher.InvokeAsync(() => SaveBtn.Content = "Saved!");
            await Task.Delay(isExplicitSave ? 1500 : 100);
            
            await Dispatcher.InvokeAsync(() => SaveBtn.Content = "Save Changes");
        }

        private void WriteDetailToItem(WheelItem item)
        {
            item.Text = ItemTextBox.Text.Trim();
            item.IsInfinite = ItemInfiniteCheck.IsChecked ?? false;

            if (int.TryParse(ItemWeightBox.Text, out int weight))
                item.Weight = Math.Max(1, weight);
            if (int.TryParse(ItemQuantityBox.Text, out int qty))
                item.Quantity = Math.Max(0, qty);
        }

        private static void CopyItemToTracked(WheelItem source, WheelItem tracked)
        {
            tracked.Text = source.Text;
            tracked.Weight = source.Weight;
            tracked.Quantity = source.Quantity;
            tracked.IsInfinite = source.IsInfinite;
            tracked.Enabled = source.Enabled;
        }

        private void SpinDelayBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(SpinDelayBox.Text, out int delay)) delay = 4;
            delay = Math.Max(0, delay);
            _config.Set("WheelSpin", "SpinDelaySeconds", delay.ToString());
            _config.Save();
            SuppressChanges(() => SpinDelayBox.Text = delay.ToString());
        }

        private async void SpinsOwedBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(SpinsOwedBox.Text, out int newOwed)) return;
            newOwed = Math.Max(0, newOwed);
            _spinsOwed = newOwed;
            await StateValueHelper.SetAsync(_factory, StateKeys.WheelSpinsOwed, newOwed);
            SuppressChanges(() => SpinsOwedBox.Text = newOwed.ToString());
            RaiseWheelDataChanged();
        }

        private async void SpinsOwedDecrement_Click(object sender, RoutedEventArgs e)
        {
            if (_spinsOwed <= 0) return;
            _spinsOwed = Math.Max(0, _spinsOwed - 1);
            await StateValueHelper.SetAsync(_factory, StateKeys.WheelSpinsOwed, _spinsOwed);
            SuppressChanges(() => SpinsOwedBox.Text = _spinsOwed.ToString());
            RaiseWheelDataChanged();
        }

        private async void SpinsOwedIncrement_Click(object sender, RoutedEventArgs e)
        {
            _spinsOwed++;
            await StateValueHelper.SetAsync(_factory, StateKeys.WheelSpinsOwed, _spinsOwed);
            SuppressChanges(() => SpinsOwedBox.Text = _spinsOwed.ToString());
            RaiseWheelDataChanged();
        }

        private async void SpinWheel_Click(object sender, RoutedEventArgs e)
            => await PerformSpinAsync();

        private async Task PerformSpinAsync()
        {
            if (_activeWheel == null || _isSpinning) return;
            _isSpinning = true;
            SpinWheelBtn.IsEnabled = false;

            await using (var db = await _factory.CreateDbContextAsync())
            {
                var tracked = await db.WheelSets.FindAsync(_activeWheel.Id);
                if (tracked != null)
                {
                    tracked.SpinCount++;
                    _activeWheel.SpinCount = tracked.SpinCount;
                    await db.SaveChangesAsync();
                }
            }

            SuppressChanges(() => SpinCountBox.Text = _activeWheel.SpinCount.ToString());

            if (_spinsOwed > 0)
            {
                _spinsOwed--;
                await StateValueHelper.SetAsync(_factory, StateKeys.WheelSpinsOwed, _spinsOwed);
                SuppressChanges(() => SpinsOwedBox.Text = _spinsOwed.ToString());
            }

            int.TryParse(SpinDelayBox.Text, out int delay);
            delay = Math.Max(0, delay);
            WheelEvents.RaiseWheelSpinStarted(_activeWheel, delay);
            if (delay > 0)
                await Task.Delay(TimeSpan.FromSeconds(delay));

            var item = PickWeightedItem();
            if (item != null)
            {
                string actionDesc = item.Action != null
                    ? $"ActionType={item.Action.ActionType}, Param=\"{item.Action.Parameter}\""
                    : "Action=Manual/Other";
                string msg = $"[WheelSpin] Rolled: Id={item.Id}, Name=\"{item.Text}\", {actionDesc}";
                //////////////////////////////////////////////////// TODO add webhook 
                _logger?.LogInformation(msg);

            }
            else
            {
                _logger?.LogWarning("[WheelSpin] No spinnable items available.");
            }

            if (item != null)
            {
                var histEntry = new WheelSpinHistory
                {
                    WheelId = _activeWheel.Id,
                    WheelItemId = item.Id,
                    Status = (item.Action?.ActionType.IsDoneImmediately() ?? false)
                        ? WheelSpinHistoryStatus.Done
                        : WheelSpinHistoryStatus.Pending,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };
                await using var dbH = await _factory.CreateDbContextAsync();
                dbH.WheelSpinHistories.Add(histEntry);
                await dbH.SaveChangesAsync();
                histEntry.LinkedItem = item;
                await Dispatcher.InvokeAsync(() => PrependHistoryRow(histEntry));
                WheelEvents.RaiseWheelSpinResult(_activeWheel, item, histEntry);
                if (item.Action?.ActionType.IsDoneImmediately() ?? false)
                {
                    switch (item.Action.ActionType)
                    {
                        case WheelSpinActionType.AddTime:
                        case WheelSpinActionType.SubtractTime:
                        {
                            var duration = Utils.ParseDurationString(item.Action.Parameter);
                            if (duration == TimeSpan.Zero) return;
                            var cmd = item.Action.ActionType.ToCommandType();
                            SubathonEvents.RaiseSubathonEventCreated(new SubathonEvent
                            {
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
            }

            if (item is { IsInfinite: false })
            {
                item.Quantity = Math.Max(0, item.Quantity - 1);
                await using var dbQ = await _factory.CreateDbContextAsync();
                var trackedQ = await dbQ.WheelItems.FindAsync(item.Id);
                if (trackedQ != null)
                {
                    trackedQ.Quantity = item.Quantity;
                    await dbQ.SaveChangesAsync();
                }

                await Dispatcher.InvokeAsync(LoadItemRows);
            }

            if (item?.Action?.ActionType == WheelSpinActionType.Reroll)
            {
                if (int.TryParse(item.Action.Parameter, out int rerollCount) && rerollCount >= 1)
                {
                    _spinsOwed += rerollCount;
                    await StateValueHelper.SetAsync(_factory, StateKeys.WheelSpinsOwed, _spinsOwed);
                    SuppressChanges(() => SpinsOwedBox.Text = _spinsOwed.ToString());
                }
            }

            RaiseWheelDataChanged();
            _isSpinning = false;
            await Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(1500);
                SpinWheelBtn.IsEnabled = true;
            });
        }

        private WheelItem? PickWeightedItem()
        {
            if (_activeWheel == null) return null;
            var spinnable = _activeWheel.WheelItems.Where(i => i.IsSpinnable()).ToList();
            if (spinnable.Count == 0) return null;
            int totalWeight = spinnable.Sum(i => i.Weight);
            if (totalWeight <= 0) return spinnable.First();
            int roll = Random.Shared.Next(totalWeight);
            int cumulative = 0;
            foreach (var item in spinnable)
            {
                cumulative += item.Weight;
                if (roll < cumulative) return item;
            }
            return spinnable.Last();
        }

        private void MarkPendingChanges()
        {
            if (_suppressCount > 0 || _activeWheel == null) return;
            Dispatcher.Invoke(() => UpdateSaveButtonBorder(true));
        }

        private void UpdateSaveButtonBorder(bool hasPendingChanges)
        {
            Dispatcher.InvokeAsync(() =>
                UiUtils.UiUtils.UpdateButtonPendingBorder(SaveButtonBorder, hasPendingChanges));
        }

        private void SuppressChanges(Action action)
        {
            _suppressCount++;
            try { action(); }
            finally { _suppressCount--; }
        }

        private void AttachChangeHandlers()
        {
            ItemTextBox.TextChanged += (_, _) => MarkPendingChanges();
            ItemWeightBox.TextChanged += (_, _) => MarkPendingChanges();
            ItemQuantityBox.TextChanged += (_, _) => MarkPendingChanges();
            ActionParameterBox.TextChanged += (_, _) => MarkPendingChanges();
            MultiplierAmountBox.TextChanged += (_, _) => MarkPendingChanges();
            MultiplierDurationBox.TextChanged += (_, _) => MarkPendingChanges();
            RerollCountBox.TextChanged += (_, _) => MarkPendingChanges();
        }

        private void NumberOnly_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            e.Handled = !int.TryParse(e.Text, out _);
        }

        private void FloatOnly_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            e.Handled = e.Text != "." && !int.TryParse(e.Text, out _);
        }

        private bool IsCurrentUiActionValid(out string error)
        {
            if (!((ActionTypeBox.SelectedItem as ComboBoxItem)?.Tag is WheelSpinActionType selected) 
                || !selected.HasAction()) { error = ""; return true; }

            if (selected is WheelSpinActionType.AddTime or WheelSpinActionType.SubtractTime)
            {
                string paramText = ActionParameterBox.Text.Trim();
                if (string.IsNullOrEmpty(paramText) || Utils.ParseDurationString(paramText) == TimeSpan.Zero)
                { error = "Duration must be non-zero (e.g. 5m, 300s, 1h30m)."; return false; }
                error = ""; return true;
            }

            if (selected == WheelSpinActionType.Reroll)
            {
                if (!int.TryParse(RerollCountBox.Text.Trim(), out int count) || count < 1)
                { error = "Reroll count must be at least 1"; return false; }
                error = ""; return true;
            }

            if (selected == WheelSpinActionType.SetMultiplier)
            {
                if (!double.TryParse(MultiplierAmountBox.Text.Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double amt) || amt == 0)
                { error = "Multiplier amount must be a non-zero number"; return false; }

                string durText = MultiplierDurationBox.Text.Trim();
                if (string.IsNullOrEmpty(durText) || Utils.ParseDurationString(durText) == TimeSpan.Zero)
                { error = "Multiplier duration is required and must be non-zero (e.g. 30m or 1h)"; return false; }

                if (!(MultiplierTimeCheck.IsChecked ?? false) && !(MultiplierPointsCheck.IsChecked ?? false))
                { error = "At least one of Time or Points must be selected"; return false; }

                error = ""; return true;
            }

            error = ""; return true;
        }

        private static bool IsItemActionValid(WheelItem item, out string error)
        {
            if (item.Action == null) { error = ""; return true; }

            var type = item.Action.ActionType;
            var param = item.Action.Parameter;

            if (type is WheelSpinActionType.AddTime or WheelSpinActionType.SubtractTime)
            {
                if (string.IsNullOrEmpty(param) || Utils.ParseDurationString(param) == TimeSpan.Zero)
                { error = "Time parameter must be a non-zero duration"; return false; }
                error = ""; return true;
            }

            if (type == WheelSpinActionType.Reroll)
            {
                var parts = param.Split('|');
                if (parts.Length < 1 || !int.TryParse(parts[0], out int count) || count < 1)
                { error = "Reroll count must be at least 1"; return false; }
                error = ""; return true;
            }

            if (type == WheelSpinActionType.SetMultiplier)
            {
                var parts = param.Split('|');
                if (parts.Length < 4) { error = "Multiplier parameters are incomplete"; return false; }

                if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double amt) || amt == 0)
                { error = "Multiplier amount is missing or zero"; return false; }

                string durStr = parts[1].EndsWith("s") ? parts[1][..^1] : parts[1];
                if (parts[1] == "xs" || string.IsNullOrEmpty(durStr) || !int.TryParse(durStr, out int ds) || ds <= 0)
                { error = "Multiplier duration is required and must be non-zero"; return false; }

                bool applyPoints = parts[2].Equals("True", StringComparison.OrdinalIgnoreCase);
                bool applyTime = parts[3].Equals("True", StringComparison.OrdinalIgnoreCase);
                if (!applyPoints && !applyTime)
                { error = "At least one of Time or Points must be selected"; return false; }

                error = ""; return true;
            }

            error = ""; return true;
        }

        private void UpdateRowEnabledCheckbox(Guid itemId, bool enabled)
        {
            var row = ItemsStack.Children.OfType<Grid>()
                .FirstOrDefault(g => g.Tag is WheelItem wi && wi.Id == itemId);
            var cb = row?.Children.OfType<CheckBox>().FirstOrDefault();
            if (cb != null) SuppressChanges(() => cb.IsChecked = enabled);
        }

        private async Task LoadHistoryAsync(bool append = false)
        {
            if (_historyLoading) return;
            _historyLoading = true;
            try
            {
                if (!append)
                {
                    _historyOffset = 0;
                    await Dispatcher.InvokeAsync(() => HistoryStack.Children.Clear());
                }
                if (_activeWheel == null) return;

                await using var db = await _factory.CreateDbContextAsync();
                var entries = await db.WheelSpinHistories
                    .Include(h => h.LinkedItem).ThenInclude(i => i!.Action)
                    .Where(h => h.WheelId == _activeWheel.Id
                             && (_historyFilter == null || h.Status == _historyFilter))
                    .OrderByDescending(h => h.CreatedAt)
                    .Skip(_historyOffset)
                    .Take(HistoryPageSize)
                    .AsNoTracking()
                    .ToListAsync();

                _historyOffset += entries.Count;

                await Dispatcher.InvokeAsync(() =>
                {
                    foreach (var entry in entries)
                        HistoryStack.Children.Add(BuildHistoryRow(entry));
                });
            }
            finally
            {
                _historyLoading = false;
            }
        }

        private void PrependHistoryRow(WheelSpinHistory h)
        {
            _historyOffset++;
            HistoryStack.Children.Insert(0, BuildHistoryRow(h));
        }

        private Grid BuildHistoryRow(WheelSpinHistory h)
        {
            var row = new Grid { Margin = new Thickness(2, 1, 2, 1), MinHeight = 26, Tag = h };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.7, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

            var tsLabel = new TextBlock
            {
                Text = h.CreatedAt.ToString("MM/dd HH:mm"),
                FontSize = 10,
                Foreground = System.Windows.Media.Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = h.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
            };

            var itemLabel = new TextBlock
            {
                Text = h.LinkedItem?.Text ?? "(deleted)",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 4, 0),
                ToolTip = h.LinkedItem?.Text
            };

            var statusLabel = new TextBlock
            {
                Text = h.Status.ToString(),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = HistoryStatusBrush(h.Status),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var hoverBtns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 12, 0)
            };

            void AddBtn(WheelSpinHistoryStatus target, Wpf.Ui.Controls.SymbolRegular icon, string tip)
            {
                var btn = new Wpf.Ui.Controls.Button
                {
                    Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = icon },
                    Width = 22, Height = 22,
                    Padding = new Thickness(1),
                    Margin = new Thickness(1, 0, 0, 0),
                    ToolTip = tip,
                    IsEnabled = h.Status != target,
                    Tag = target
                };
                btn.Click += async (_, _) => await SetHistoryStatus(h, target, statusLabel, hoverBtns);
                hoverBtns.Children.Add(btn);
            }

            AddBtn(WheelSpinHistoryStatus.Done,      Wpf.Ui.Controls.SymbolRegular.Checkmark24, "Mark Done");
            AddBtn(WheelSpinHistoryStatus.Pending,   Wpf.Ui.Controls.SymbolRegular.Clock24,     "Mark Pending");
            AddBtn(WheelSpinHistoryStatus.Cancelled, Wpf.Ui.Controls.SymbolRegular.Dismiss24,   "Mark Cancelled");

            var actionType = h.LinkedItem?.Action?.ActionType;
            bool hasPlayBtn = h.Status == WheelSpinHistoryStatus.Pending
                && actionType.HasValue && actionType.Value.IsCommand();

            string actionStr = h.LinkedItem?.Action == null
                ? "Manual"
                : $"{h.LinkedItem.Action.ActionType}: {h.LinkedItem.Action.Parameter}";

            var actionCell = new Grid { Margin = new Thickness(4, 0, 2, 0) };
            actionCell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (hasPlayBtn)
                actionCell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var actionLabel = new TextBlock
            {
                Text = actionStr,
                FontSize = 10,
                Foreground = System.Windows.Media.Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = actionStr
            };

            Grid.SetColumn(actionLabel, 0);
            actionCell.Children.Add(actionLabel);

            if (hasPlayBtn)
            {
                bool isMultiplier = actionType == WheelSpinActionType.SetMultiplier;
                var playBtn = new Wpf.Ui.Controls.Button
                {
                    Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Play24 },
                    Width = 20, Height = 20,
                    Padding = new Thickness(1),
                    Margin = new Thickness(3, 0, 0, 0),
                    ToolTip = isMultiplier ? "Apply multiplier (disabled while one is active)" : "Apply command",
                    IsEnabled = !isMultiplier || !_multiplierActive,
                    Tag = isMultiplier ? "MultiplierPlayBtn" : null
                };
                playBtn.Click += async (_, _) => await ExecuteHistoryAction(h, playBtn, statusLabel, hoverBtns);
                Grid.SetColumn(playBtn, 1);
                actionCell.Children.Add(playBtn);
            }

            Grid.SetColumn(tsLabel,     0);
            Grid.SetColumn(itemLabel,   1);
            Grid.SetColumn(actionCell,  2);
            Grid.SetColumn(statusLabel, 3);
            Grid.SetColumn(hoverBtns,   4);

            row.Children.Add(tsLabel);
            row.Children.Add(itemLabel);
            row.Children.Add(actionCell);
            row.Children.Add(statusLabel);
            row.Children.Add(hoverBtns);

            return row;
        }

        private static System.Windows.Media.Brush HistoryStatusBrush(WheelSpinHistoryStatus status) => status switch
        {
            WheelSpinHistoryStatus.Done      => System.Windows.Media.Brushes.MediumSeaGreen,
            WheelSpinHistoryStatus.Pending   => System.Windows.Media.Brushes.CornflowerBlue,
            WheelSpinHistoryStatus.Cancelled => System.Windows.Media.Brushes.IndianRed,
            _                                => System.Windows.Media.Brushes.Gray
        };

        private async Task SetHistoryStatus(WheelSpinHistory h, WheelSpinHistoryStatus newStatus,
            TextBlock statusLabel, StackPanel hoverBtns)
        {
            h.Status = newStatus;
            h.UpdatedAt = DateTime.Now;

            await using var db = await _factory.CreateDbContextAsync();
            var tracked = await db.WheelSpinHistories.FindAsync(h.Id);
            if (tracked == null) return;
            tracked.Status = newStatus;
            tracked.UpdatedAt = h.UpdatedAt;
            await db.SaveChangesAsync();

            await Dispatcher.InvokeAsync(() =>
            {
                statusLabel.Text = newStatus.ToString();
                statusLabel.Foreground = HistoryStatusBrush(newStatus);
                foreach (var btn in hoverBtns.Children.OfType<Wpf.Ui.Controls.Button>())
                    btn.IsEnabled = btn.Tag is WheelSpinHistoryStatus s && s != newStatus;
            });
            WheelEvents.RaiseWheelSpinStatusChanged(h);
        }

        private void RaiseWheelDataChanged()
        {
            if (_activeWheel == null) return;
            WheelEvents.RaiseWheelDataChanged(_activeWheel, _spinsOwed);
        }

        private void OnSubathonDataUpdate(SubathonData data, DateTime _)
        {
            _multiplierActive = data.Multiplier?.IsRunning() ?? false;

            if (Interlocked.CompareExchange(ref _multiplierRefreshQueued, 1, 0) != 0) return;
            Dispatcher.InvokeAsync(() =>
            {
                Interlocked.Exchange(ref _multiplierRefreshQueued, 0);
                RefreshMultiplierButtons(_multiplierActive);
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void RefreshMultiplierButtons(bool multiplierActive)
        {
            foreach (var row in HistoryStack.Children.OfType<Grid>())
            {
                if (row.Tag is not WheelSpinHistory h) continue;
                bool isPending = h.Status == WheelSpinHistoryStatus.Pending;
                foreach (var cell in row.Children.OfType<Grid>())
                {
                    foreach (var btn in cell.Children.OfType<Wpf.Ui.Controls.Button>())
                    {
                        if (btn.Tag?.ToString() == "MultiplierPlayBtn")
                            btn.IsEnabled = isPending && !multiplierActive;
                    }
                }
            }
        }

        private async Task ExecuteHistoryAction(WheelSpinHistory h, Wpf.Ui.Controls.Button playBtn,
            TextBlock statusLabel, StackPanel hoverBtns)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                playBtn.IsEnabled = false;
                playBtn.Visibility = Visibility.Collapsed;
            });

            var action = h.LinkedItem?.Action;
            if (action == null) return;

            switch (action.ActionType)
            {
                case WheelSpinActionType.AddTime:
                case WheelSpinActionType.SubtractTime:
                {
                    var duration = Utils.ParseDurationString(action.Parameter);
                    if (duration == TimeSpan.Zero) return;
                    var cmd = action.ActionType.ToCommandType();
                    SubathonEvents.RaiseSubathonEventCreated(new SubathonEvent
                    {
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
                case WheelSpinActionType.SetMultiplier:
                {
                    SubathonEvents.RaiseSubathonEventCreated(new SubathonEvent
                    {
                        Source = SubathonEventSource.WheelSpin,
                        EventTimestamp = DateTime.Now,
                        Command = SubathonCommandType.SetMultiplier,
                        EventType = SubathonEventType.Command,
                        User = "WheelSpin",
                        Value = action.Parameter
                    });
                    
                    _multiplierActive = true;
                    await Dispatcher.InvokeAsync(() => RefreshMultiplierButtons(true));
                    break;
                }
            }

            if (h.Status != WheelSpinHistoryStatus.Done)
                await SetHistoryStatus(h, WheelSpinHistoryStatus.Done, statusLabel, hoverBtns);
        }

        private void HistoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_activeWheel == null) return;
            _historyFilter = (HistoryFilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
            {
                "Pending"   => WheelSpinHistoryStatus.Pending,
                "Done"      => WheelSpinHistoryStatus.Done,
                "Cancelled" => WheelSpinHistoryStatus.Cancelled,
                _           => null
            };
            _ = LoadHistoryAsync();
        }

        private void HistoryScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_historyLoading) return;
            if (HistoryScroller.ScrollableHeight > 0 &&
                HistoryScroller.ScrollableHeight - HistoryScroller.VerticalOffset < 100)
                _ = LoadHistoryAsync(append: true);
        }

        private async void ExportHistory_Click(object sender, RoutedEventArgs e)
        {
            await using var db = await _factory.CreateDbContextAsync();
            var histories = await db.WheelSpinHistories
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
            foreach (var h in histories)
            {
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
                    h.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss")
                ));
            }

            await File.WriteAllTextAsync(filepath, sb.ToString(), Encoding.UTF8);

            try
            {
                Process.Start(new ProcessStartInfo { FileName = exportDir, UseShellExecute = true, Verb = "open" });
            }
            catch { /**/ }
        }
    }
}
