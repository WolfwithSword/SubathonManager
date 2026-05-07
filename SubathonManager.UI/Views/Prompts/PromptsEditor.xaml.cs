using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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
        private Guid? _activeRunPromptId;

        private record EventTypeEntry(SubathonEventType EventType, string Label);
        private Dictionary<SubathonEventSource, List<EventTypeEntry>> _eventsBySource = new();

        private readonly DispatcherTimer _popupCloseTimer;

        public PromptsEditor()
        {
            _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            InitializeComponent();

            _popupCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _popupCloseTimer.Tick += (_, _) =>
            {
                _popupCloseTimer.Stop();
                EventTypePopup.IsOpen = false;
                EventListPanel.Visibility = Visibility.Collapsed;
            };

            BuildEventsBySource();
            PopulateTypeComboBox();
            PopulateSourceListBox();
            LoadActiveSet();

            SubathonEvents.PromptRunStarted += (run, _) => Dispatcher.InvokeAsync(() => OnRunStateChanged(run.PromptId, true));
            SubathonEvents.PromptRunUpdate += (run, _) => Dispatcher.InvokeAsync(() =>
            {
                OnRunStateChanged(run.PromptId, false);
                if (run.Status == SubathonPromptRunStatus.Completed)
                    LoadPromptRows();
            });

            Loaded += (_, _) => Dispatcher.Invoke(AttachSetChangeHandlers);
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
                          .Select(e => new EventTypeEntry(e, e.GetLabel()))
                          .ToList()
                );
        }

        private void PopulateSourceListBox()
        {
            SourceListBox.Items.Clear();
            foreach (var source in _eventsBySource.Keys)
            {
                var item = new ListBoxItem
                {
                    Content = source.ToString(),
                    Tag = source,
                    Padding = new Thickness(10, 6, 10, 6)
                };
                item.MouseEnter += SourceItem_MouseEnter;
                SourceListBox.Items.Add(item);
            }
        }

        private void SourceItem_MouseEnter(object sender, MouseEventArgs e)
        {
            _popupCloseTimer.Stop();

            if (sender is not ListBoxItem item || item.Tag is not SubathonEventSource source) return;

            SourceListBox.SelectedItem = item;
            PopulateEventList(source);
            EventListPanel.Visibility = Visibility.Visible;
        }

        private void PopulateEventList(SubathonEventSource source)
        {
            EventListBox.Items.Clear();
            if (!_eventsBySource.TryGetValue(source, out var events)) return;

            foreach (var entry in events)
            {
                EventListBox.Items.Add(new ListBoxItem
                {
                    Content = entry.Label,
                    Tag = entry.EventType,
                    Padding = new Thickness(10, 6, 10, 6)
                });
            }
        }

        private void EventListBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (EventListBox.SelectedItem is not ListBoxItem item) return;
            if (item.Tag is not SubathonEventType eventType) return;

            _popupCloseTimer.Stop();
            EventTypePopup.IsOpen = false;
            EventListPanel.Visibility = Visibility.Collapsed;
            OnEventTypeSelected(eventType);
        }

        private void EventTypePopupBorder_MouseEnter(object sender, MouseEventArgs e)
        {
            _popupCloseTimer.Stop();
        }

        private void EventTypePopupBorder_MouseLeave(object sender, MouseEventArgs e)
        {
            _popupCloseTimer.Start();
        }

        private void EventTypePickerBtn_Click(object sender, RoutedEventArgs e)
        {
            if (EventTypePopup.IsOpen)
            {
                _popupCloseTimer.Stop();
                EventTypePopup.IsOpen = false;
                EventListPanel.Visibility = Visibility.Collapsed;
                return;
            }

            EventListPanel.Visibility = Visibility.Collapsed;

            if (_selectedFilterEventType.HasValue)
            {
                var source = _selectedFilterEventType.Value.GetSource();
                var sourceItem = SourceListBox.Items.OfType<ListBoxItem>()
                    .FirstOrDefault(i => i.Tag is SubathonEventSource s && s == source);
                if (sourceItem != null)
                {
                    SourceListBox.SelectedItem = sourceItem;
                    PopulateEventList(source);
                    EventListPanel.Visibility = Visibility.Visible;
                }
            }

            EventTypePopup.IsOpen = true;
        }

        private void OnEventTypeSelected(SubathonEventType eventType)
        {
            _selectedFilterEventType = eventType;
            EventTypePickerLabel.Text = $"{eventType.GetSource()} - {eventType.GetLabel()}";

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
                ? $"Evt:{prompt.FilterEventType.Value.GetLabel()}"
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
                EventTypePickerLabel.Text = _selectedFilterEventType.HasValue
                    ? $"{_selectedFilterEventType.Value.GetSource()} - {_selectedFilterEventType.Value.GetLabel()}"
                    : "- select -";

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