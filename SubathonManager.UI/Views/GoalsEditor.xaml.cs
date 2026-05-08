using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Data;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views
{
    public partial class GoalsEditor
    {
        private SubathonGoalSet? _activeGoalSet;
        private readonly IDbContextFactory<AppDbContext> _factory;
        private int _suppressCount = 0;

        public GoalsEditor()
        {
            _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            InitializeComponent();
            GoalSetType.ItemsSource = Enum.GetNames<GoalsType>().ToList();
            LoadAllSets();
            SubathonEvents.SubathonDataUpdate += UpdatePointsCount;
        }

        private void UpdatePointsCount(SubathonData subathon, DateTime time)
        {
            Dispatcher.InvokeAsync(() =>
            {
                double moneySum = subathon.GetRoundedMoneySumWithCents();
                PointsValue.Text = $"{subathon.Points:N0} Pts";
                MoneyValue.Text = $"{subathon.Currency} {moneySum:N2}".Trim();
            });
        }

        private void LoadAllSets()
        {
            using var db = _factory.CreateDbContext();
            var allSets = db.SubathonGoalSets.OrderBy(s => s.Name).ToList();

            SuppressChanges(() =>
            {
                GoalSetSelectorBox.Items.Clear();
                foreach (var s in allSets)
                    GoalSetSelectorBox.Items.Add(new ComboBoxItem { Content = s.Name, Tag = s.Id });
            });

            if (allSets.Count == 0)
            {
                StatusText.Text = "No goal sets found. Create a new one.";
                DeleteGoalSetBtn.IsEnabled = false;
                return;
            }

            DeleteGoalSetBtn.IsEnabled = allSets.Count > 1;

            var active = allSets.FirstOrDefault(s => s.IsActive) ?? allSets.First();
            SuppressChanges(() =>
            {
                var item = GoalSetSelectorBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => (Guid)i.Tag == active.Id);
                GoalSetSelectorBox.SelectedItem = item;
            });

            LoadSetById(active.Id);
        }

        private void LoadSetById(Guid setId)
        {
            using var db = _factory.CreateDbContext();
            _activeGoalSet = db.SubathonGoalSets
                .Include(gs => gs.Goals)
                .FirstOrDefault(gs => gs.Id == setId);

            if (_activeGoalSet == null)
            {
                StatusText.Text = "Set not found.";
                return;
            }

            foreach (var s in db.SubathonGoalSets.Where(s => s.Id != setId))
                s.IsActive = false;
            _activeGoalSet.IsActive = true;
            db.SaveChanges();

            StatusText.Text = "";

            SuppressChanges(() =>
            {
                GoalSetNameBox.Text = _activeGoalSet.Name;
                GoalSetType.SelectedValue = $"{_activeGoalSet.Type ?? GoalsType.Points}";
            });

            Dispatcher.InvokeAsync(LoadGoals);
            RaiseGoalListUpdated(db);
        }

        private void GoalSetSelectorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCount > 0) return;
            if (GoalSetSelectorBox.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not Guid setId) return;
            LoadSetById(setId);
        }

        private void GoalSetNameBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_activeGoalSet == null) return;
            var newName = GoalSetNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName) || newName == _activeGoalSet.Name) return;

            _activeGoalSet.Name = newName;

            using var db = _factory.CreateDbContext();
            var tracked = db.SubathonGoalSets.Find(_activeGoalSet.Id);
            if (tracked == null) return;
            tracked.Name = newName;
            db.SaveChanges();

            SuppressChanges(() =>
            {
                var item = GoalSetSelectorBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => (Guid)i.Tag == _activeGoalSet.Id);
                item?.Content = newName;
            });
        }

        private void GoalSetType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCount > 0 || _activeGoalSet == null) return;
            UpdateSaveButtonBorder(true);
        }

        private async void NewGoalSet_Click(object sender, RoutedEventArgs e)
        {
            await using var db = await _factory.CreateDbContextAsync();

            foreach (var s in db.SubathonGoalSets)
                s.IsActive = false;

            var newSet = new SubathonGoalSet { Name = "New Goal Set", IsActive = true };
            db.SubathonGoalSets.Add(newSet);
            await db.SaveChangesAsync();

            var newItem = new ComboBoxItem { Content = newSet.Name, Tag = newSet.Id };
            SuppressChanges(() =>
            {
                GoalSetSelectorBox.Items.Add(newItem);
                GoalSetSelectorBox.SelectedItem = newItem;
            });

            _activeGoalSet = newSet;
            SuppressChanges(() =>
            {
                GoalSetNameBox.Text = newSet.Name;
                GoalSetType.SelectedValue = $"{GoalsType.Points}";
            });

            GoalsStack.Children.Clear();
            StatusText.Text = "";
            DeleteGoalSetBtn.IsEnabled = true;
            GoalSetNameBox.Focus();
            GoalSetNameBox.SelectAll();
        }

        private async void DeleteGoalSet_Click(object sender, RoutedEventArgs e)
        {
            if (_activeGoalSet == null) return;

            await using var db = await _factory.CreateDbContextAsync();
            int total = await db.SubathonGoalSets.CountAsync();
            if (total <= 1) return;

            var deletingId = _activeGoalSet.Id;
            var tracked = await db.SubathonGoalSets.FindAsync(deletingId);
            if (tracked != null) db.SubathonGoalSets.Remove(tracked);

            var next = await db.SubathonGoalSets
                .Where(s => s.Id != deletingId)
                .OrderBy(s => s.Name)
                .FirstOrDefaultAsync();
            next?.IsActive = true;

            await db.SaveChangesAsync();

            SuppressChanges(() =>
            {
                var item = GoalSetSelectorBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => (Guid)i.Tag == deletingId);
                if (item != null) GoalSetSelectorBox.Items.Remove(item);
            });

            if (next != null)
            {
                var selectItem = GoalSetSelectorBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => (Guid)i.Tag == next.Id);
                SuppressChanges(() => GoalSetSelectorBox.SelectedItem = selectItem);
                LoadSetById(next.Id);
            }

            DeleteGoalSetBtn.IsEnabled = GoalSetSelectorBox.Items.Count > 1;
        }
        
        private void NumberOnly_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            e.Handled = !int.TryParse(e.Text, out _);
        }

        private async void LoadGoals()
        {
            GoalsStack.Children.Clear();
            if (_activeGoalSet == null) return;

            await using var db = await _factory.CreateDbContextAsync();
            await db.Entry(_activeGoalSet).ReloadAsync();

            var goals = _activeGoalSet.Goals.OrderBy(g => g.Points).ToList();

            foreach (var goal in goals)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 8) };

                var textBox = new Wpf.Ui.Controls.TextBox
                {
                    Text = goal.Text,
                    Width = 522,
                    Margin = new Thickness(0, 0, 8, 0),
                    ToolTip = "Goal Description",
                    PlaceholderText = "Goal Description..."
                };
                textBox.TextChanged += Value_OnChanged;

                var pointsBox = new Wpf.Ui.Controls.TextBox
                {
                    Text = goal.Points.ToString(),
                    Width = 80,
                    Margin = new Thickness(0, 0, 8, 0),
                    ToolTip = "Points/Money to achieve",
                    ClearButtonEnabled = false
                };
                pointsBox.PreviewTextInput += NumberOnly_PreviewTextInput;
                pointsBox.TextChanged += Value_OnChanged;

                var deleteBtn = new Wpf.Ui.Controls.Button
                {
                    Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24 },
                    Width = 35, Height = 35,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    ToolTip = "Remove",
                    Foreground = System.Windows.Media.Brushes.Red,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Margin = new Thickness(4, 0, 0, 0)
                };
                deleteBtn.Click += (_, _) => DeleteGoal_Click(goal);

                panel.Children.Add(textBox);
                panel.Children.Add(pointsBox);
                panel.Children.Add(deleteBtn);
                panel.Tag = goal;

                GoalsStack.Children.Add(panel);
            }

            GoalsEditorScroller.Height = 600;
            GoalsEditorScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }

        private async void DeleteGoal_Click(SubathonGoal goal)
        {
            await using var db = await _factory.CreateDbContextAsync();
            db.SubathonGoals.Remove(goal);
            await db.SaveChangesAsync();
            await db.Entry(_activeGoalSet!).ReloadAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                LoadGoals();
                RaiseGoalListUpdated(db);
            });
        }

        private async void AddGoal_Click(object sender, RoutedEventArgs e)
        {
            if (_activeGoalSet == null) return;
            await SaveGoalsAsync(null, null);

            await using var db = await _factory.CreateDbContextAsync();

            long maxPoints = _activeGoalSet.Goals.Count > 0 ? _activeGoalSet.Goals.Max(g => g.Points) : 0;
            var newGoal = new SubathonGoal { Points = maxPoints + 1, GoalSetId = _activeGoalSet.Id };

            db.SubathonGoals.Add(newGoal);
            await db.SaveChangesAsync();
            await db.Entry(_activeGoalSet).ReloadAsync();

            await Dispatcher.InvokeAsync(() =>
            {
                LoadGoals();
                RaiseGoalListUpdated(db);
            });
        }

        private async void SaveGoals_Click(object? sender, RoutedEventArgs? e)
            => await SaveGoalsAsync(sender, e);

        private async Task SaveGoalsAsync(object? sender, RoutedEventArgs? e)
        {
            if (_activeGoalSet == null) return;

            _activeGoalSet.Name = GoalSetNameBox.Text.Trim();
            _activeGoalSet.Type = Enum.TryParse($"{GoalSetType.SelectedValue}", out GoalsType type)
                ? type : _activeGoalSet.Type;

            await using var db = await _factory.CreateDbContextAsync();
            db.Update(_activeGoalSet);

            foreach (StackPanel panel in GoalsStack.Children.OfType<StackPanel>())
            {
                if (panel.Tag is not SubathonGoal goal) continue;
                var textBox   = panel.Children[0] as TextBox;
                var pointsBox = panel.Children[1] as TextBox;
                goal.Text = textBox?.Text ?? "";
                if (long.TryParse(pointsBox?.Text, out long pts)) goal.Points = pts;
                db.Update(goal);
            }

            await db.SaveChangesAsync();

            if (sender != null && e != null)
            {
                await Dispatcher.InvokeAsync(LoadGoals);
                RaiseGoalListUpdated(db);
            }

            UpdateSaveButtonBorder(false);
            await Dispatcher.InvokeAsync(() => SaveGoalsBtn.Content = "Saved!");
            await Task.Delay(sender != null ? 1500 : 100);
            await Dispatcher.InvokeAsync(() => SaveGoalsBtn.Content = "Save Changes");
        }

        private void RaiseGoalListUpdated(AppDbContext db)
        {
            if (_activeGoalSet == null) return;
            var subathon = db.SubathonDatas.AsNoTracking().FirstOrDefault(s => s.IsActive);
            long pts = subathon?.Points ?? 0;
            if (_activeGoalSet.Type == GoalsType.Money) pts = subathon?.GetRoundedMoneySum() ?? 0;
            SubathonEvents.RaiseSubathonGoalListUpdated(
                _activeGoalSet.Goals, pts, _activeGoalSet.Type ?? GoalsType.Points);
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
        
        private void Grid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Keyboard.ClearFocus();
            (sender as Grid)?.Focus();
        }


        private void Value_OnChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressCount > 0) return;
            Dispatcher.Invoke(() => UiUtils.UiUtils.UpdateButtonPendingBorder(SaveButtonBorder, true));
        }
    }
}