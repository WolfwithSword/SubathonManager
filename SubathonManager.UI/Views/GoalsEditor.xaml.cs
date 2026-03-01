using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Data;

namespace SubathonManager.UI.Views
{
    public partial class GoalsEditor
    {
        private SubathonGoalSet? _activeGoalSet;
        private readonly IDbContextFactory<AppDbContext> _factory;

        public GoalsEditor()
        {
            _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            InitializeComponent();
            GoalSetType.ItemsSource = Enum.GetNames<GoalsType>().ToList();
            LoadActiveGoalSet();
            SubathonEvents.SubathonDataUpdate += UpdatePointsCount;
        }

        private void UpdatePointsCount(SubathonData subathon, DateTime time)
        {
            Dispatcher.InvokeAsync(() =>
            {
                double moneySum =  subathon.GetRoundedMoneySumWithCents();
                PointsValue.Text = $"{subathon.Points:N0} Pts";
                MoneyValue.Text = $"{subathon.Currency} {moneySum:N2}".Trim();
            });
        }
        private void LoadActiveGoalSet()
        {
            using var db = _factory.CreateDbContext();
            _activeGoalSet = db.SubathonGoalSets.Include(gs => gs.Goals)
                .FirstOrDefault(gs => gs.IsActive);
            if (_activeGoalSet == null)
            {
                StatusText.Text = "No active goal set. Create a new one.";
                return;
            }
            
            StatusText.Text = "";

            GoalSetNameBox.Text = _activeGoalSet.Name;
            GoalSetType.SelectedValue = $"{_activeGoalSet.Type ?? GoalsType.Points}";
            
            Dispatcher.InvokeAsync(LoadGoals);
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
            await db.Entry(_activeGoalSet!).ReloadAsync();

            var goals = _activeGoalSet!.Goals.OrderBy(g => g.Points).ToList();
            
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

                var pointsBox = new Wpf.Ui.Controls.TextBox
                {
                    Text = goal.Points.ToString(),
                    Width = 80,
                    Margin = new Thickness(0, 0, 8, 0),
                    ToolTip = "Points/Money to achieve"
                };
                pointsBox.PreviewTextInput += NumberOnly_PreviewTextInput;
                

                var deleteBtn = new Wpf.Ui.Controls.Button
                {
                    Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24 },
                    Width = 35,
                    Height = 35,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    ToolTip = "Remove",
                    Foreground = System.Windows.Media.Brushes.Red,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Margin = new Thickness(4, 0, 0, 0)
                };

                deleteBtn.Click += (s, e) => DeleteGoal_Click(goal);
                panel.Children.Add(textBox);
                panel.Children.Add(pointsBox);
                panel.Children.Add(deleteBtn);

                panel.Tag = goal;

                GoalsStack.Children.Add(panel);
                GoalsEditorScroller.Height = 600;
                GoalsEditorScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
        }

        private async void DeleteGoal_Click(SubathonGoal goal)
        {

            await using var db = await _factory.CreateDbContextAsync();
            db.SubathonGoals.Remove(goal);
            await db.SaveChangesAsync();
            await db.Entry(_activeGoalSet!).ReloadAsync();
            SubathonData? subathon = await db.SubathonDatas.AsNoTracking().FirstOrDefaultAsync(s => s.IsActive);
            long pts = subathon?.Points ?? 0;
            if (_activeGoalSet?.Type == GoalsType.Money) pts = subathon?.GetRoundedMoneySum() ?? 0;
            await Dispatcher.InvokeAsync(() => 
            {
                LoadGoals();
                SubathonEvents.RaiseSubathonGoalListUpdated(_activeGoalSet!.Goals, pts, (GoalsType) _activeGoalSet.Type!);
            });
        }
        
        private async void CreateNewGoalSet_Click(object sender, RoutedEventArgs e)
        {
            var msgBox = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Create new Goals List"
            };

            var textBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Width = 320
            };
            textBlock.Inlines.Add("Create a new goals list and delete the current one?");
            msgBox.Content = textBlock;
            msgBox.CloseButtonText = "Cancel";
            msgBox.Owner = Application.Current.Windows
                .OfType<Window>()
                .FirstOrDefault(w => w.IsActive);
            msgBox.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            msgBox.PrimaryButtonText = "Confirm";
            var result = await msgBox.ShowDialogAsync();
            bool confirm = result == Wpf.Ui.Controls.MessageBoxResult.Primary;
            if (!confirm) return;
            
            await using var db = await _factory.CreateDbContextAsync();
            foreach (var gs in db.SubathonGoalSets)
                gs.IsActive = false;

            var newGoalSet = new SubathonGoalSet
            {
                IsActive = true
            };

            db.SubathonGoalSets.Add(newGoalSet);
            await db.SaveChangesAsync();

            _activeGoalSet = newGoalSet;
            GoalSetNameBox.Text = newGoalSet.Name;
            GoalSetType.SelectedValue = $"{newGoalSet.Type ?? GoalsType.Points}";
            GoalsStack.Children.Clear();
            StatusText.Text = "";
            SubathonData? subathon = await db.SubathonDatas.AsNoTracking().FirstOrDefaultAsync(s => s.IsActive);
            long pts = subathon?.Points ?? 0;
            if (_activeGoalSet.Type == GoalsType.Money) pts = subathon?.GetRoundedMoneySum() ?? 0;
            SubathonEvents.RaiseSubathonGoalListUpdated(_activeGoalSet!.Goals, pts, (GoalsType) _activeGoalSet.Type!);
        }

        private void AddGoal_Click(object sender, RoutedEventArgs e)
        {
            if (_activeGoalSet == null) return;
            SaveGoals_Click(null, null);

            using var db = _factory.CreateDbContext();

            long maxPoints = 0;
            if (_activeGoalSet.Goals.Count > 0)
                maxPoints = _activeGoalSet.Goals.Max(g => g.Points);

            var newGoal = new SubathonGoal
            {
                Points = maxPoints + 1,
                GoalSetId = _activeGoalSet.Id
            };

            db.SubathonGoals.Add(newGoal);
            db.SaveChanges();
            db.Entry(_activeGoalSet).Reload();
            SubathonData? subathon = db.SubathonDatas.AsNoTracking().FirstOrDefault(s => s.IsActive);
            Dispatcher.InvokeAsync(() => 
            {
                LoadGoals();
                long pts = subathon?.Points ?? 0;
                if (_activeGoalSet?.Type == GoalsType.Money) pts = subathon!.GetRoundedMoneySum();
                SubathonEvents.RaiseSubathonGoalListUpdated(_activeGoalSet!.Goals, pts, (GoalsType) _activeGoalSet.Type!);
            });
            
            // do we want to push event update here? probably
        }

        private async void SaveGoals_Click(object? sender, RoutedEventArgs? e)
        {
            if (_activeGoalSet == null) return;

            _activeGoalSet.Name = GoalSetNameBox.Text;
            _activeGoalSet.Type = Enum.TryParse($"{GoalSetType.SelectedValue}", out GoalsType type) ? 
                type : _activeGoalSet.Type;

            await using var db = await _factory.CreateDbContextAsync();
            db.Update(_activeGoalSet);
            foreach (StackPanel panel in GoalsStack.Children.OfType<StackPanel>())
            {
                if (panel.Tag is SubathonGoal goal)
                {
                    var textBox = panel.Children[0] as TextBox;
                    var pointsBox = panel.Children[1] as TextBox;

                    goal.Text = textBox?.Text ?? "";
                    if (long.TryParse(pointsBox?.Text, out long points))
                        goal.Points = points;
                    db.Update(goal);
                }
            }

            await db.SaveChangesAsync();
            if (sender != null && e != null)
            {
                await Dispatcher.InvokeAsync(LoadGoals);
                SubathonData? subathon = db.SubathonDatas.AsNoTracking().FirstOrDefault(s => s.IsActive); 
                long pts = subathon?.Points ?? 0;
                if (_activeGoalSet?.Type == GoalsType.Money) pts = subathon!.GetRoundedMoneySum();
                SubathonEvents.RaiseSubathonGoalListUpdated(_activeGoalSet!.Goals, pts, (GoalsType) _activeGoalSet.Type!);
            }
           
            await Dispatcher.InvokeAsync(() => 
                { 
                    SaveGoalsBtn.Content = "Saved!";
                } 
            );
            await Task.Delay(1500);
            await Dispatcher.InvokeAsync(() => 
                { 
                    SaveGoalsBtn.Content = "Save Changes";
                } 
            );
        }
    }
}
