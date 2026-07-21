using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Data;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views.Prompts
{
    public partial class PromptsEditor
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private SubathonPromptSet? _activeSet;
        private SubathonPrompt? _selectedPrompt;
        private int _suppressCount = 0;
        private SubathonEventType? _selectedFilterEventType;
        private string? _selectedFilterEventMeta;
        private Guid? _activeRunPromptId;

        private record EventTypeEntry(SubathonEventType EventType, string Label, string? Meta = null, string? Category = null);
        private Dictionary<SubathonEventSource, List<EventTypeEntry>> _eventsBySource = new();

        public PromptsEditor()
        {
            _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            InitializeComponent();

            BuildEventsBySource();
            PopulateTypeComboBox();
            LoadActiveSet();

            GoAffProStoreRegistry.StoreDiscovered -= OnGoAffProStoreDiscovered;
            GoAffProStoreRegistry.StoreDiscovered += OnGoAffProStoreDiscovered;

            SubathonEvents.PromptRunStarted += (run, _) => Dispatcher.InvokeAsync(() => OnRunStateChanged(run.PromptId, true));
            SubathonEvents.PromptRunUpdate += (run, _) => Dispatcher.InvokeAsync(() =>
            {
                OnRunStateChanged(run.PromptId, false);
                if (run.Status == SubathonPromptRunStatus.Completed)
                    LoadPromptRows();
            });

            Loaded += (_, _) => Dispatcher.Invoke(AttachSetChangeHandlers);
        }

        private void OnGoAffProStoreDiscovered(GoAffProStore store)
        {
            Dispatcher.InvokeAsync(BuildEventsBySource);
        }

        private void BuildEventsBySource()
        {
            _eventsBySource = Enum.GetValues<SubathonEventType>()
                .Where(e => e.IsSelectableForPromptEvent())
                .GroupBy(e => e.GetSource())
                .OrderBy(g => g.Key.GetGroupLabelOrder())
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(e => e.GetOrderNumber())
                          .SelectMany(BuildEventEntries)
                          .ToList()
                );
        }

        private static IEnumerable<EventTypeEntry> BuildEventEntries(SubathonEventType e)
        {
            if (e == SubathonEventType.GoAffProOrder)
            {
                return GoAffProStoreRegistry.All().Where(s => s.Enabled)
                    .Select(s => new EventTypeEntry(e, s.EventName, s.SiteId.ToString()));
            }
            if (e == SubathonEventType.JuniperMerchSale)
            {
                return JuniperStoreRegistry.AllStores().Where(s => s.Enabled)
                    .SelectMany(s => new[] { new EventTypeEntry(e, "Any Sale", s.RowId.ToString(), s.StoreName) }
                        .Concat(s.Products
                            .OrderBy(p => p.ProductName, StringComparer.OrdinalIgnoreCase)
                            .Select(p => new EventTypeEntry(e, p.ProductName, p.ProductId.ToString(), s.StoreName))));
            }
            if (e is SubathonEventType.MakeShipPledge or SubathonEventType.MakeShipSale)
            {
                bool isPledge = e == SubathonEventType.MakeShipPledge;
                string category = isPledge ? "Pledges" : "Campaign Sales";
                var wantedType = isPledge ? MakeShipProductType.Petition : MakeShipProductType.Campaign;
                return new[] { new EventTypeEntry(e, isPledge ? "Any Pledge" : "Any Sale", null, category) }
                    .Concat(MakeShipTrackingRegistry.All()
                        .Where(t => MakeShipTrackingRegistry.ClassifyUrl(t.Url) == wantedType)
                        .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(t => new EventTypeEntry(e, t.Name, t.Name, category)));
            }
            return [new EventTypeEntry(e, e.GetLabel())];
        }

        private void EventTypePickerBtn_Click(object sender, RoutedEventArgs e)
        {
            BuildEventsBySource();
            var entries = new List<UiUtils.EventTypeMenuEntry>();

            foreach (var (source, events) in _eventsBySource)
            {
                foreach (var entry in events)
                {
                    var captured = entry;
                    entries.Add(new UiUtils.EventTypeMenuEntry(
                        source,
                        entry.Label,
                        _selectedFilterEventType == entry.EventType &&
                            entry.Meta == _selectedFilterEventMeta,
                        () => OnEventTypeSelected(captured),
                        Category: entry.Category));
                }
            }

            UiUtils.EventTypeMenu.Show(EventTypePickerBtn, entries, groupBySourceType: true);
        }

        private void OnEventTypeSelected(EventTypeEntry entry)
        {
            var eventType = entry.EventType;
            _selectedFilterEventType = eventType;
            _selectedFilterEventMeta = entry.Meta;
            EventTypePickerLabel.Text = entry.Category is { Length: > 0 }
                ? $"{eventType.GetSource()} - {entry.Category} - {entry.Label}"
                : $"{eventType.GetSource()} - {entry.Label}";

            if (_suppressCount > 0) return;

            RefreshSubTypeComboBox(SubathonPromptType.Event, eventType);
            RefreshConditionalPanels(SubathonPromptType.Event, eventType, SelectedSubType() ?? SubathonPromptSubType.Default);

            if (TierFilterPanel.Visibility == Visibility.Visible)
                RefreshTierComboBox(eventType);

            MarkPendingChanges();
        }

        private void PopulateTypeComboBox()
        {
            PromptTypeBox.Items.Clear();
            foreach (var t in Enum.GetValues<SubathonPromptType>())
            {
                PromptTypeBox.Items.Add(new ComboBoxItem
                {
                    Content = t.DisplayName(),
                    Tag = t
                });
            }
        }

        private void RefreshSubTypeComboBox(SubathonPromptType type, SubathonEventType? filterEvent = null)
        {
            SuppressChanges(() =>
            {
                var current = SelectedSubType();
                PromptSubTypeBox.Items.Clear();

                foreach (var st in type.GetValidSubTypes(filterEvent))
                {
                    PromptSubTypeBox.Items.Add(new ComboBoxItem
                    {
                        Content = st.DisplayName(type),
                        Tag = st
                    });
                }

                var match = PromptSubTypeBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => i.Tag is SubathonPromptSubType s && s == current);
                PromptSubTypeBox.SelectedItem = match ?? PromptSubTypeBox.Items[0];
            });
        }

        private void RefreshTierComboBox(SubathonEventType? eventType)
        {
            PromptTierBox.Items.Clear();
            if (eventType == null) return;

            using var db = _factory.CreateDbContext();
            var metas = db.SubathonValues
                .Where(sv => sv.EventType == eventType && sv.Meta != "")
                .Select(sv => sv.Meta)
                .Distinct()
                .OrderBy(m => m)
                .ToList();

            foreach (var meta in metas)
            {
                PromptTierBox.Items.Add(new ComboBoxItem
                {
                    Content = eventType.Value.TierMetaDisplayName(meta),
                    Tag = meta
                });
            }

            if (PromptTierBox.Items.Count > 0)
                PromptTierBox.SelectedIndex = 0;
        }

        private void LoadActiveSet()
        {
            using var db = _factory.CreateDbContext();

            var allSets = db.SubathonPromptSets.OrderBy(s => s.Name).ToList();
            if (allSets.Count == 0)
            {
                StatusText.Text = "No prompt sets found.";
                return;
            }

            SuppressChanges(() =>
            {
                SetSelectorBox.Items.Clear();
                foreach (var s in allSets)
                    SetSelectorBox.Items.Add(new ComboBoxItem { Content = s.Name, Tag = s.Id });
            });

            var activeRun = db.SubathonPromptRuns
                .FirstOrDefault(r => r.Status == SubathonPromptRunStatus.Active);
            _activeRunPromptId = activeRun?.PromptId;

            if (_activeRunPromptId.HasValue)
                Dispatcher.InvokeAsync(() => OnRunStateChanged(_activeRunPromptId.Value, true));

            var activeSet = allSets.FirstOrDefault(s => s.IsActive) ?? allSets.First();
            SuppressChanges(() =>
            {
                var item = SetSelectorBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => (Guid)i.Tag == activeSet.Id);
                SetSelectorBox.SelectedItem = item;
            });

            LoadSetById(activeSet.Id);
        }

        private void LoadSetById(Guid setId)
        {
            using var db = _factory.CreateDbContext();
            _activeSet = db.SubathonPromptSets
                .Include(s => s.Prompts)
                .FirstOrDefault(s => s.Id == setId);

            if (_activeSet == null)
            {
                StatusText.Text = "Set not found.";
                return;
            }

            StatusText.Text = "";
            _selectedPrompt = null;
            PromptDetailBorder.Visibility = Visibility.Collapsed;

            SuppressChanges(() =>
            {
                SetNameTextBox.Text = _activeSet.Name;
                SetEnabledCheck.IsChecked = _activeSet.Enabled;
                SetIntervalBox.Text = ((int)_activeSet.Interval.TotalMinutes).ToString();
                SetOffsetBox.Text = ((int)_activeSet.RandomOffset.TotalMinutes).ToString();
                SetCooldownBox.Text = ((int)_activeSet.Cooldown.TotalMinutes).ToString();
            });

            using var db2 = _factory.CreateDbContext();
            int totalSets = db2.SubathonPromptSets.Count();
            DeleteSetBtn.IsEnabled = totalSets > 1;

            Dispatcher.InvokeAsync(LoadPromptRows);
        }

        private void SetSelectorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCount > 0) return;
            if (SetSelectorBox.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not Guid setId) return;
            LoadSetById(setId);
        }

        private async void SetNameTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_activeSet == null) return;
            var newName = SetNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName) || newName == _activeSet.Name) return;

            _activeSet.Name = newName;

            await using var db = await _factory.CreateDbContextAsync();
            var tracked = await db.SubathonPromptSets.FindAsync(_activeSet.Id);
            if (tracked == null) return;
            tracked.Name = newName;
            await db.SaveChangesAsync();

            SuppressChanges(() =>
            {
                var item = SetSelectorBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => (Guid)i.Tag == _activeSet.Id);
                if (item != null) item.Content = newName;
            });
        }

        private async void SetEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressCount > 0) return;
            if (_activeSet == null) return;

            bool enabling = SetEnabledCheck.IsChecked ?? false;

            await using var db = await _factory.CreateDbContextAsync();

            if (enabling)
            {
                var others = await db.SubathonPromptSets
                    .Where(s => s.Id != _activeSet.Id)
                    .ToListAsync();
                foreach (var s in others)
                {
                    s.Enabled = false;
                    s.IsActive = false;
                }

                var tracked = await db.SubathonPromptSets.FindAsync(_activeSet.Id);
                if (tracked != null)
                {
                    tracked.Enabled = true;
                    tracked.IsActive = true;
                }
                _activeSet.Enabled = true;
                _activeSet.IsActive = true;
            }
            else
            {
                var tracked = await db.SubathonPromptSets.FindAsync(_activeSet.Id);
                if (tracked != null)
                {
                    tracked.Enabled = false;
                    tracked.IsActive = true; 
                }
                _activeSet.Enabled = false;
            }

            await db.SaveChangesAsync();

            SubathonEvents.RaisePromptSetEnabledChanged(enabling);
        }
        
        private void Grid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Keyboard.ClearFocus();
            (sender as Grid)?.Focus();
        }

        private async void NewSet_Click(object sender, RoutedEventArgs e)
        {
            await using var db = await _factory.CreateDbContextAsync();
            var newSet = new SubathonPromptSet
            {
                Name = "New Set",
                IsActive = false,
                Enabled = false
            };
            db.SubathonPromptSets.Add(newSet);
            await db.SaveChangesAsync();

            var newItem = new ComboBoxItem { Content = newSet.Name, Tag = newSet.Id };
            SuppressChanges(() =>
            {
                SetSelectorBox.Items.Add(newItem);
                SetSelectorBox.SelectedItem = newItem;
            });

            LoadSetById(newSet.Id);
            DeleteSetBtn.IsEnabled = true;
            SetNameTextBox.Focus();
        }

        private async void DeleteSet_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSet == null) return;

            await using var db = await _factory.CreateDbContextAsync();
            int total = await db.SubathonPromptSets.CountAsync();
            if (total <= 1) return;

            bool wasEnabled = _activeSet.Enabled;
            bool wasActive = _activeSet.IsActive;
            var deletingId = _activeSet.Id;

            if (_activeRunPromptId.HasValue)
            {
                var run = await db.SubathonPromptRuns
                    .FirstOrDefaultAsync(r => r.SetId == deletingId && r.Status == SubathonPromptRunStatus.Active);
                if (run != null)
                {
                    SubathonEvents.RaisePromptRunCancelRequested();
                    run.Status = SubathonPromptRunStatus.Cancelled;
                    run.EndedAt = DateTime.Now;
                }
            }

            var tracked = await db.SubathonPromptSets.FindAsync(deletingId);
            if (tracked != null)
                db.SubathonPromptSets.Remove(tracked);

            if (wasActive || wasEnabled)
            {
                var next = await db.SubathonPromptSets
                    .Where(s => s.Id != deletingId)
                    .OrderBy(s => s.Name)
                    .FirstOrDefaultAsync();
                if (next != null)
                {
                    next.IsActive = true;
                    if (wasEnabled)
                    {
                        next.Enabled = false;
                        SubathonEvents.RaisePromptSetEnabledChanged(false);
                    }
                }
            }

            await db.SaveChangesAsync();

            SuppressChanges(() =>
            {
                var item = SetSelectorBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => (Guid)i.Tag == deletingId);
                if (item != null) SetSelectorBox.Items.Remove(item);
            });

            var remaining = db.SubathonPromptSets.OrderBy(s => s.Name).First();
            var selectItem = SetSelectorBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (Guid)i.Tag == remaining.Id);
            SuppressChanges(() => SetSelectorBox.SelectedItem = selectItem);

            LoadSetById(remaining.Id);
            DeleteSetBtn.IsEnabled = SetSelectorBox.Items.Count > 1;
        }

        private async void LoadPromptRows()
        {
            PromptsStack.Children.Clear();
            if (_activeSet == null) return;

            await using var db = await _factory.CreateDbContextAsync();
            var prompts = await db.SubathonPrompts
                .Where(p => p.SetId == _activeSet.Id)
                .OrderBy(p => p.Index)
                .ToListAsync();

            _activeSet.Prompts = prompts;

            foreach (var prompt in prompts)
                PromptsStack.Children.Add(BuildPromptRow(prompt));

            RefreshRowHighlights();
        }
        
        private Grid BuildPromptRow(SubathonPrompt prompt)
        {
            var row = new Grid { Margin = new Thickness(4, 0, 4, 4), Tag = prompt, MinHeight = 30 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

            var enabledCheck = new CheckBox
            {
                IsChecked = prompt.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = "Enable / disable this prompt",
                Margin = new Thickness(-12, 0, 0, 0),
            };
            enabledCheck.Checked += (_, _) => OnRowEnabledToggled(prompt, true);
            enabledCheck.Unchecked += (_, _) => OnRowEnabledToggled(prompt, false);
            Grid.SetColumn(enabledCheck, 0);

            var textLabel = new TextBlock
            {
                Text = prompt.Text,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 4, 0),
                Cursor = Cursors.Hand,
                ToolTip = prompt.Text
            };
            textLabel.MouseLeftButtonUp += (_, _) => SelectPrompt(prompt, row);
            Grid.SetColumn(textLabel, 1);

            string typeBadgeText = prompt is { Type: SubathonPromptType.Event, FilterEventType: not null }
                ? prompt.FilterEventType switch
                {
                    SubathonEventType.GoAffProOrder =>
                        $"Evt:{GoAffProOrderHelper.GetOrderEventDisplayLabel(prompt.FilterEventType, prompt.FilterMeta)}",
                    SubathonEventType.JuniperMerchSale =>
                        $"Evt:{OrderMetaFilter.Describe(prompt.FilterEventType, prompt.FilterMeta)}",
                    SubathonEventType.MakeShipPledge or SubathonEventType.MakeShipSale
                        when !string.IsNullOrEmpty(prompt.FilterMeta) =>
                        $"Evt:{prompt.FilterEventType.Value.GetLabel()} ({prompt.FilterMeta})",
                    _ => $"Evt:{prompt.FilterEventType.Value.GetLabel()}"
                }
                : prompt.Type.DisplayName();
            var typeBadge = new TextBlock
            {
                Text = typeBadgeText,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = System.Windows.Media.Brushes.CornflowerBlue,
                ToolTip = prompt.Type == SubathonPromptType.Event
                    ? $"Event: {prompt.FilterEventType?.ToString() ?? "?"} / {prompt.SubType.DisplayName(prompt.Type)}"
                    : $"{prompt.Type.DisplayName()} / {prompt.SubType.DisplayName(prompt.Type)}"
            };
            Grid.SetColumn(typeBadge, 2);

            var valueLabel = new TextBlock
            {
                Text = prompt.Value.ToString("N0"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetColumn(valueLabel, 3);

            var durLabel = new TextBlock
            {
                Text = $"{(int)prompt.CompletionDuration.TotalMinutes}m",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetColumn(durLabel, 4);

            var qtyLabel = new TextBlock
            {
                Text = prompt.IsInfinite ? "∞" : prompt.Quantity.ToString(),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetColumn(qtyLabel, 5);

            var runNowBtn = new Wpf.Ui.Controls.Button
            {
                Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Play24 },
                Width = 26,
                Height = 26,
                Padding = new Thickness(2),
                Cursor = Cursors.Hand,
                ToolTip = "Run this prompt now"
            };
            runNowBtn.Click += (_, _) => SubathonEvents.RaisePromptRunNowRequested(prompt.Id);
            Grid.SetColumn(runNowBtn, 6);

            var deleteBtn = new Wpf.Ui.Controls.Button
            {
                Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24 },
                Width = 30,
                Height = 30,
                Padding = new Thickness(2),
                Foreground = System.Windows.Media.Brushes.Red,
                Cursor = Cursors.Hand,
                ToolTip = "Delete prompt"
            };
            deleteBtn.Click += (_, _) => DeletePrompt_Click(prompt);
            Grid.SetColumn(deleteBtn, 7);

            row.Children.Add(enabledCheck);
            row.Children.Add(textLabel);
            row.Children.Add(typeBadge);
            row.Children.Add(valueLabel);
            row.Children.Add(durLabel);
            row.Children.Add(qtyLabel);
            row.Children.Add(runNowBtn);
            row.Children.Add(deleteBtn);

            return row;
        }

        private async void OnRowEnabledToggled(SubathonPrompt prompt, bool enabled)
        {
            prompt.Enabled = enabled;

            if (_selectedPrompt?.Id == prompt.Id)
            {
                _selectedPrompt.Enabled = enabled;
            }

            await using var db = await _factory.CreateDbContextAsync();
            var tracked = await db.SubathonPrompts.FindAsync(prompt.Id);
            if (tracked == null) return;
            tracked.Enabled = enabled;
            await db.SaveChangesAsync();
        }

        private void SelectPrompt(SubathonPrompt prompt, Grid? clickedRow = null)
        {
            _selectedPrompt = prompt;
            PromptDetailBorder.Visibility = Visibility.Visible;

            SuppressChanges(() =>
            {
                PromptTextBox.Text = prompt.Text;
                PromptValueBox.Text = prompt.Value.ToString();
                PromptDurationBox.Text = ((int)prompt.CompletionDuration.TotalMinutes).ToString();
                PromptQuantityBox.Text = prompt.Quantity.ToString();
                PromptInfiniteCheck.IsChecked = prompt.IsInfinite;
                PromptQuantityBox.IsEnabled = !prompt.IsInfinite;

                var typeItem = PromptTypeBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => i.Tag is SubathonPromptType t && t == prompt.Type);
                PromptTypeBox.SelectedItem = typeItem;

                bool isEvent = prompt.Type == SubathonPromptType.Event;
                EventTypePanel.Visibility = isEvent ? Visibility.Visible : Visibility.Collapsed;

                _selectedFilterEventType = isEvent ? prompt.FilterEventType : null;
                _selectedFilterEventMeta = isEvent && prompt.FilterEventType
                        is SubathonEventType.GoAffProOrder or SubathonEventType.JuniperMerchSale
                        or SubathonEventType.MakeShipPledge or SubathonEventType.MakeShipSale
                    ? prompt.FilterMeta
                    : null;
                EventTypePickerLabel.Text = _selectedFilterEventType switch
                {
                    null => "- select -",
                    SubathonEventType.GoAffProOrder =>
                        $"{_selectedFilterEventType.Value.GetSource()} - {GoAffProOrderHelper.GetOrderEventDisplayLabel(_selectedFilterEventType, _selectedFilterEventMeta)}",
                    SubathonEventType.JuniperMerchSale =>
                        $"{_selectedFilterEventType.Value.GetSource()} - {OrderMetaFilter.Describe(_selectedFilterEventType, _selectedFilterEventMeta)}",
                    SubathonEventType.MakeShipPledge or SubathonEventType.MakeShipSale
                        when !string.IsNullOrEmpty(_selectedFilterEventMeta) =>
                        $"{_selectedFilterEventType.Value.GetSource()} - {_selectedFilterEventType.Value.GetLabel()} - {_selectedFilterEventMeta}",
                    _ => $"{_selectedFilterEventType.Value.GetSource()} - {_selectedFilterEventType.Value.GetLabel()}"
                };

                RefreshSubTypeComboBox(prompt.Type, prompt.FilterEventType);
                var subTypeItem = PromptSubTypeBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => i.Tag is SubathonPromptSubType st && st == prompt.SubType);
                if (subTypeItem != null) PromptSubTypeBox.SelectedItem = subTypeItem;

                RefreshConditionalPanels(prompt.Type, prompt.FilterEventType, prompt.SubType);
                if (TierFilterPanel.Visibility == Visibility.Visible)
                {
                    RefreshTierComboBox(prompt.FilterEventType);
                    if (!string.IsNullOrEmpty(prompt.FilterMeta))
                    {
                        var tierItem = PromptTierBox.Items.OfType<ComboBoxItem>()
                            .FirstOrDefault(i => i.Tag as string == prompt.FilterMeta);
                        if (tierItem != null) PromptTierBox.SelectedItem = tierItem;
                    }
                }

                UpdateValueLabel(prompt.Type, prompt.SubType);
            });

            RefreshRowHighlights(clickedRow);
            AttachPromptDetailChangeHandlers();
            ApplyEditorLock(_activeRunPromptId.HasValue && _activeRunPromptId.Value == prompt.Id);
        }

        private void RefreshConditionalPanels(SubathonPromptType type, SubathonEventType? filterEvent, SubathonPromptSubType subType)
        {
            bool showTier = type == SubathonPromptType.Event
                && filterEvent.HasValue
                && filterEvent.IsSubscription()
                && subType == SubathonPromptSubType.ByTier;
            TierFilterPanel.Visibility = showTier ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshRowHighlights(Grid? clickedRow = null)
        {
            foreach (var child in PromptsStack.Children.OfType<Grid>())
            {
                if (child.Tag is not SubathonPrompt p) continue;

                bool isActiveRun = _activeRunPromptId.HasValue && p.Id == _activeRunPromptId.Value;
                bool isSelected = !isActiveRun && (child == clickedRow || p.Id == _selectedPrompt?.Id);

                child.Background = isActiveRun
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 50, 200, 80))
                    : isSelected
                        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 100, 149, 237))
                        : System.Windows.Media.Brushes.Transparent;
            }
        }

        private void PromptType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCount > 0) return;
            if (SelectedType() is not { } type) return;

            bool isEvent = type == SubathonPromptType.Event;
            EventTypePanel.Visibility = isEvent ? Visibility.Visible : Visibility.Collapsed;

            if (!isEvent)
            {
                TierFilterPanel.Visibility = Visibility.Collapsed;
                SuppressChanges(() =>
                {
                    _selectedFilterEventType = null;
                    _selectedFilterEventMeta = null;
                    EventTypePickerLabel.Text = "- select -";
                });
            }

            RefreshSubTypeComboBox(type, isEvent ? _selectedFilterEventType : null);
            UpdateValueLabel(type, SelectedSubType() ?? SubathonPromptSubType.Default);
            MarkPendingChanges();
        }

        private void PromptInfinite_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressCount > 0) return;
            PromptQuantityBox.IsEnabled = !(PromptInfiniteCheck.IsChecked ?? false);
            MarkPendingChanges();
        }

        private SubathonPromptType? SelectedType()
            => (PromptTypeBox.SelectedItem as ComboBoxItem)?.Tag as SubathonPromptType?;

        private SubathonPromptSubType? SelectedSubType()
            => (PromptSubTypeBox.SelectedItem as ComboBoxItem)?.Tag as SubathonPromptSubType?;

        private SubathonEventType? SelectedFilterEventType() => _selectedFilterEventType;

        private void UpdateValueLabel(SubathonPromptType type, SubathonPromptSubType subType)
        {
            ValueLabel.Text = type.ValueLabel(subType) + ":";
        }

        private async void AddPrompt_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSet == null) return;
            await SaveAsync(null, null);

            await using var db = await _factory.CreateDbContextAsync();
            int nextOrder = await db.SubathonPrompts
                .Where(p => p.SetId == _activeSet.Id)
                .Select(p => (int?)p.Index)
                .MaxAsync() ?? 0;
            nextOrder++;

            var newPrompt = new SubathonPrompt
            {
                SetId = _activeSet.Id,
                Text = "New Prompt",
                Enabled = false,
                Index = nextOrder
            };
            db.SubathonPrompts.Add(newPrompt);
            await db.SaveChangesAsync();
            await db.Entry(_activeSet).ReloadAsync();

            await Dispatcher.InvokeAsync(() =>
            {
                LoadPromptRows();
                SelectPrompt(newPrompt);
            });
        }

        private async void DeletePrompt_Click(SubathonPrompt prompt)
        {
            if (_selectedPrompt?.Id == prompt.Id)
            {
                _selectedPrompt = null;
                PromptDetailBorder.Visibility = Visibility.Collapsed;
            }

            await using var db = await _factory.CreateDbContextAsync();
            var tracked = await db.SubathonPrompts.FindAsync(prompt.Id);
            if (tracked != null)
                db.SubathonPrompts.Remove(tracked);

            await db.SaveChangesAsync();
            await Dispatcher.InvokeAsync(LoadPromptRows);
        }

        private async void Save_Click(object? sender, RoutedEventArgs? e)
            => await SaveAsync(sender, e);

        private async Task SaveAsync(object? sender, RoutedEventArgs? e)
        {
            if (_activeSet == null) return;

            _activeSet.Name = SetNameTextBox.Text.Trim();

            if (int.TryParse(SetIntervalBox.Text, out int intervalMin))
                _activeSet.Interval = TimeSpan.FromMinutes(Math.Max(1, intervalMin));
            if (int.TryParse(SetOffsetBox.Text, out int offsetMin))
                _activeSet.RandomOffset = TimeSpan.FromMinutes(Math.Max(0, offsetMin));
            if (int.TryParse(SetCooldownBox.Text, out int cooldownMin))
                _activeSet.Cooldown = TimeSpan.FromMinutes(Math.Max(0, cooldownMin));

            _activeSet.ClampRandomOffset();
            SuppressChanges(() => SetOffsetBox.Text = ((int)_activeSet.RandomOffset.TotalMinutes).ToString());

            if (_selectedPrompt != null)
                WriteDetailToPrompt(_selectedPrompt);

            await using var db = await _factory.CreateDbContextAsync();
            db.Update(_activeSet);

            if (_selectedPrompt != null)
            {
                var tracked = await db.SubathonPrompts.FindAsync(_selectedPrompt.Id);
                if (tracked != null)
                {
                    CopyPromptToTracked(_selectedPrompt, tracked);
                    db.Update(tracked);
                }
            }

            await db.SaveChangesAsync();

            bool isExplicitSave = sender != null;
            if (isExplicitSave)
                await Dispatcher.InvokeAsync(LoadPromptRows);

            UpdateSaveButtonBorder(false);
            await Dispatcher.InvokeAsync(() => SaveBtn.Content = "Saved!");
            await Task.Delay(isExplicitSave ? 1500 : 100);
            await Dispatcher.InvokeAsync(() => SaveBtn.Content = "Save Changes");
        }

        private void WriteDetailToPrompt(SubathonPrompt prompt)
        {
            prompt.Text = PromptTextBox.Text.Trim();
            prompt.IsInfinite = PromptInfiniteCheck.IsChecked ?? false;

            if (long.TryParse(PromptValueBox.Text, out long val))
                prompt.Value = Math.Max(1, val);
            if (int.TryParse(PromptDurationBox.Text, out int dur))
                prompt.CompletionDuration = TimeSpan.FromMinutes(Math.Max(1, dur));
            if (int.TryParse(PromptQuantityBox.Text, out int qty))
                prompt.Quantity = Math.Max(0, qty);

            prompt.Type = SelectedType() ?? prompt.Type;
            prompt.SubType = SelectedSubType() ?? SubathonPromptSubType.Default;

            if (prompt.Type == SubathonPromptType.Event)
            {
                prompt.FilterEventType = _selectedFilterEventType;
                prompt.FilterSubType = prompt.FilterEventType?.GetSubType();
                if (prompt.FilterEventType is SubathonEventType.GoAffProOrder or SubathonEventType.JuniperMerchSale
                    or SubathonEventType.MakeShipPledge or SubathonEventType.MakeShipSale)
                    prompt.FilterMeta = _selectedFilterEventMeta; // site id (meta) / product id / store guid / tracking name
                else
                    prompt.FilterMeta = prompt.SubType == SubathonPromptSubType.ByTier
                        ? (PromptTierBox.SelectedItem as ComboBoxItem)?.Tag as string
                        : null;
            }
            else
            {
                prompt.FilterEventType = null;
                prompt.FilterSubType = null;
                prompt.FilterMeta = null;
            }
        }

        private static void CopyPromptToTracked(SubathonPrompt source, SubathonPrompt tracked)
        {
            tracked.Text = source.Text;
            tracked.Value = source.Value;
            tracked.CompletionDuration = source.CompletionDuration;
            tracked.Quantity = source.Quantity;
            tracked.IsInfinite = source.IsInfinite;
            tracked.Enabled = source.Enabled;
            tracked.Type = source.Type;
            tracked.SubType = source.SubType;
            tracked.FilterEventType = source.FilterEventType;
            tracked.FilterMeta = source.FilterMeta;
            tracked.FilterSubType = source.FilterSubType;
        }

        private void MarkPendingChanges()
        {
            if (_suppressCount > 0) return;
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

        private void AttachSetChangeHandlers()
        {
            SuppressChanges(() =>
            {
                SetIntervalBox.TextChanged += (_, _) => MarkPendingChanges();
                SetOffsetBox.TextChanged += (_, _) => MarkPendingChanges();
                SetCooldownBox.TextChanged += (_, _) => MarkPendingChanges();
            });
        }

        private void AttachPromptDetailChangeHandlers()
        {
            PromptTextBox.TextChanged += (_, _) => MarkPendingChanges();
            PromptValueBox.TextChanged += (_, _) => MarkPendingChanges();
            PromptDurationBox.TextChanged += (_, _) => MarkPendingChanges();
            PromptQuantityBox.TextChanged += (_, _) => MarkPendingChanges();

            PromptSubTypeBox.SelectionChanged += (_, _) =>
            {
                if (_suppressCount > 0) return;
                var type = SelectedType() ?? SubathonPromptType.Points;
                var subType = SelectedSubType() ?? SubathonPromptSubType.Default;

                RefreshConditionalPanels(type, _selectedFilterEventType, subType);
                if (TierFilterPanel.Visibility == Visibility.Visible)
                    RefreshTierComboBox(_selectedFilterEventType);

                UpdateValueLabel(type, subType);
                MarkPendingChanges();
            };

            PromptTierBox.SelectionChanged += (_, _) => MarkPendingChanges();
        }

        private void NumberOnly_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            e.Handled = !int.TryParse(e.Text, out _);
        }

        private async void ExportPromptSet_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSet == null) return;

            await using var db = await _factory.CreateDbContextAsync();
            var set = await db.SubathonPromptSets
                .Include(s => s.Prompts)
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == _activeSet.Id);
            if (set == null) return;

            string exportDir = Path.Combine(Config.DataFolder, "exports");
            Directory.CreateDirectory(exportDir);

            string safeName = string.Concat(set.Name.Split(Path.GetInvalidFileNameChars()));
            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string filepath = Path.Combine(exportDir, $"{safeName}-{timestamp}.csv");

            var sb = new StringBuilder();
            sb.AppendLine($"#Interval={(int)set.Interval.TotalMinutes},Offset={(int)set.RandomOffset.TotalMinutes},Cooldown={(int)set.Cooldown.TotalMinutes}");
            sb.AppendLine("Text,Value,Duration,Quantity,Infinite,Enabled,Type,SubType,EventType,FilterMeta");
            foreach (var p in set.Prompts.OrderBy(p => p.Index))
            {
                sb.AppendLine(string.Join(",",
                    Utils.EscapeCsv(p.Text),
                    p.Value,
                    (int)p.CompletionDuration.TotalMinutes,
                    p.Quantity,
                    p.IsInfinite,
                    p.Enabled,
                    p.Type,
                    p.SubType,
                    p.FilterEventType?.ToString() ?? "",
                    Utils.EscapeCsv(p.FilterMeta ?? "")
                ));
            }

            await File.WriteAllTextAsync(filepath, sb.ToString(), Encoding.UTF8);

            try
            {
                Process.Start(new ProcessStartInfo { FileName = exportDir, UseShellExecute = true, Verb = "open" });
            }
            catch { /**/ }
        }

        private async void ImportPromptSet_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import Prompt Set",
                Filter = "CSV Files (*.csv)|*.csv",
                DefaultExt = "csv"
            };

            if (dlg.ShowDialog() != true) return;

            string[] lines;
            try { lines = await File.ReadAllLinesAsync(dlg.FileName, Encoding.UTF8); }
            catch { await ShowInvalidPromptCsvPopup(); return; }

            if (lines.Length < 1) { await ShowInvalidPromptCsvPopup(); return; }

            int headerIndex = 0;
            int intervalMin = 20, offsetMin = 0, cooldownMin = 20;

            if (lines[0].StartsWith('#'))
            {
                foreach (var kv in lines[0][1..].Split(','))
                {
                    var parts = kv.Split('=');
                    if (parts.Length != 2) continue;
                    if (int.TryParse(parts[1].Trim(), out int v))
                    {
                        if (parts[0].Trim().Equals("Interval", StringComparison.OrdinalIgnoreCase)) intervalMin = v;
                        else if (parts[0].Trim().Equals("Offset", StringComparison.OrdinalIgnoreCase)) offsetMin = v;
                        else if (parts[0].Trim().Equals("Cooldown", StringComparison.OrdinalIgnoreCase)) cooldownMin = v;
                    }
                }
                headerIndex = 1;
            }

            if (headerIndex >= lines.Length || ParseCsvLine(lines[headerIndex]).Length < 8)
            {
                await ShowInvalidPromptCsvPopup(); return;
            }

            var prompts = new List<SubathonPrompt>();
            for (int i = headerIndex + 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var cols = ParseCsvLine(lines[i]);
                if (cols.Length < 8
                    || !long.TryParse(cols[1].Trim(), out long value)
                    || !int.TryParse(cols[2].Trim(), out int durMin)
                    || !int.TryParse(cols[3].Trim(), out int qty)
                    || !bool.TryParse(cols[4].Trim(), out bool infinite)
                    || !bool.TryParse(cols[5].Trim(), out bool enabled)
                    || !Enum.TryParse<SubathonPromptType>(cols[6].Trim(), out var type)
                    || !Enum.TryParse<SubathonPromptSubType>(cols[7].Trim(), out var subType))
                {
                    await ShowInvalidPromptCsvPopup(); return;
                }

                SubathonEventType? filterEvent = null;
                string? goAffProMeta = null;
                var eventStr = cols.Length > 8 ? cols[8].Trim() : "";
                if (!string.IsNullOrEmpty(eventStr))
                {
                    if (Enum.TryParse<SubathonEventType>(eventStr, out var fet))
                    {
                        if (fet.GetLegacyGoAffProSiteId() > 0)
                        {
                            goAffProMeta = fet.GetLegacyGoAffProSiteId().ToString();
                            fet = SubathonEventType.GoAffProOrder;
                        }
                        filterEvent = fet;
                    }
                    else if (GoAffProOrderHelper.TryGetStoreByOrderKey(eventStr, out var keyStore))
                    {
                        filterEvent = SubathonEventType.GoAffProOrder;
                        goAffProMeta = keyStore.SiteId.ToString();
                    }
                    else { await ShowInvalidPromptCsvPopup(); return; }
                }

                string? filterMeta = cols.Length > 9 && !string.IsNullOrWhiteSpace(cols[9]) ? cols[9].Trim() : null;
                if (goAffProMeta != null) filterMeta = goAffProMeta;

                prompts.Add(new SubathonPrompt
                {
                    Text = cols[0],
                    Value = value,
                    CompletionDuration = TimeSpan.FromMinutes(Math.Max(1, durMin)),
                    Quantity = Math.Max(0, qty),
                    IsInfinite = infinite,
                    Enabled = enabled,
                    Type = type,
                    SubType = subType,
                    FilterEventType = filterEvent,
                    FilterSubType = filterEvent?.GetSubType(),
                    FilterMeta = filterMeta,
                    Index = prompts.Count
                });
            }

            string setName = Path.GetFileNameWithoutExtension(dlg.FileName);

            await using var db = await _factory.CreateDbContextAsync();
            foreach (var s in db.SubathonPromptSets)
                s.IsActive = false;

            var newSet = new SubathonPromptSet
            {
                Name = setName,
                IsActive = true,
                Enabled = false,
                Interval = TimeSpan.FromMinutes(Math.Max(1, intervalMin)),
                RandomOffset = TimeSpan.FromMinutes(Math.Max(0, offsetMin)),
                Cooldown = TimeSpan.FromMinutes(Math.Max(0, cooldownMin))
            };
            newSet.ClampRandomOffset();
            db.SubathonPromptSets.Add(newSet);
            await db.SaveChangesAsync();

            foreach (var p in prompts)
            {
                p.SetId = newSet.Id;
                db.SubathonPrompts.Add(p);
            }
            await db.SaveChangesAsync();

            LoadActiveSet();
        }

        private static string[] ParseCsvLine(string line)
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

        private static async Task ShowInvalidPromptCsvPopup()
        {
            var msgBox = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Invalid CSV",
                Content = new TextBlock
                {
                    Text = "The selected file is not a valid prompt set CSV and could not be imported.",
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

        private async void RunOrCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_activeRunPromptId.HasValue)
            {
                SubathonEvents.RaisePromptRunCancelRequested();
                return;
            }

            await using var db = await _factory.CreateDbContextAsync();
            var set = await db.SubathonPromptSets
                .Include(s => s.Prompts)
                .FirstOrDefaultAsync(s => s.IsActive);

            if (set == null) return;
            var pickable = set.PickablePrompts().ToList();
            if (pickable.Count == 0) return;

            SubathonEvents.RaisePromptRunNowRequested(pickable[Random.Shared.Next(pickable.Count)].Id);
        }

        private void OnRunStateChanged(Guid promptId, bool isRunning)
        {
            _activeRunPromptId = isRunning ? promptId : null;
            RefreshRowHighlights();

            RunOrCancelIcon.Symbol = isRunning
                ? Wpf.Ui.Controls.SymbolRegular.Stop24
                : Wpf.Ui.Controls.SymbolRegular.Play24;
            RunOrCancelBtn.ToolTip = isRunning ? "Cancel current running prompt" : "Run a random prompt now";
            RunOrCancelBtn.Foreground = isRunning
                ? System.Windows.Media.Brushes.OrangeRed
                : (System.Windows.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]!;

            if (_selectedPrompt?.Id == promptId)
                ApplyEditorLock(isRunning);
        }

        private void ApplyEditorLock(bool locked)
        {
            PromptDetailBorder.IsEnabled = !locked;
            RunningLockText.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
            SaveBtn.IsEnabled = !locked;
        }
    }
}