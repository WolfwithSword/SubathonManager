using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Data;

namespace SubathonManager.UI.Views
{
    public partial class GoalsEditor : UserControl
    {
        private SubathonGoalSet? _activeGoalSet;

        public GoalsEditor()
        {
            InitializeComponent();
            LoadActiveGoalSet();
            SubathonEvents.SubathonDataUpdate += UpdatePointsCount;
        }

        private void UpdatePointsCount(SubathonData subathon, DateTime time)
        {
            Dispatcher.InvokeAsync(() => { PointsValue.Text = $"{subathon.Points.ToString()} Pts"; });
        }
        private void LoadActiveGoalSet()
        {
            using var db = new AppDbContext();
            _activeGoalSet = db.SubathonGoalSets.Include(gs => gs.Goals)
                .FirstOrDefault(gs => gs.IsActive);
            if (_activeGoalSet == null)
            {
                StatusText.Text = "No active goal set. Create a new one.";
                return;
            }
            
            StatusText.Text = "";

            GoalSetNameBox.Text = _activeGoalSet.Name;
            Dispatcher.InvokeAsync(() => 
            {
                LoadGoals();
            });
        }
        
        private void NumberOnly_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            e.Handled = !int.TryParse(e.Text, out _);
        }

        private async void LoadGoals()
        {
            GoalsStack.Children.Clear();

            if (_activeGoalSet == null) return;
            using var db = new AppDbContext();
            await db.Entry(_activeGoalSet!).ReloadAsync();

            var goals = _activeGoalSet!.Goals.OrderBy(g => g.Points).ToList();
            
            foreach (var goal in goals)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 8) };

                var textBox = new TextBox
                {
                    Text = goal.Text,
                    Width = 522,
                    Margin = new Thickness(0, 0, 8, 0)
                };

                var pointsBox = new TextBox
                {
                    Text = goal.Points.ToString(),
                    Width = 80,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                pointsBox.PreviewTextInput += NumberOnly_PreviewTextInput;
                

                var deleteBtn = new Wpf.Ui.Controls.Button
                {
                    Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24 },
                    Width = 35,
                    Height = 35,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    ToolTip = "Remove",
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 40, 40)),
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
        
        private void DeleteGoal_Click(SubathonGoal goal)
        {
            using var db = new AppDbContext();
            db.SubathonGoals.Remove(goal);
            db.SaveChanges();
            db.Entry(_activeGoalSet!).Reload();
            Dispatcher.InvokeAsync(() => 
            {
                LoadGoals();
                SubathonEvents.RaiseSubathonGoalListUpdated(_activeGoalSet!.Goals);
            });
        }

        private void CreateNewGoalSet_Click(object sender, RoutedEventArgs e)
        {
            using var db = new AppDbContext();
            foreach (var gs in db.SubathonGoalSets)
                gs.IsActive = false;

            var newGoalSet = new SubathonGoalSet
            {
                IsActive = true
            };

            db.SubathonGoalSets.Add(newGoalSet);
            db.SaveChanges();

            _activeGoalSet = newGoalSet;
            GoalSetNameBox.Text = newGoalSet.Name;
            GoalsStack.Children.Clear();
            StatusText.Text = "";
            SubathonEvents.RaiseSubathonGoalListUpdated(_activeGoalSet!.Goals);
        }

        private void AddGoal_Click(object sender, RoutedEventArgs e)
        {
            if (_activeGoalSet == null) return;
            SaveGoals_Click(null, null);

            using var db = new AppDbContext();

            int maxPoints = 0;
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

            Dispatcher.InvokeAsync(() => 
            {
                LoadGoals();
                SubathonEvents.RaiseSubathonGoalListUpdated(_activeGoalSet!.Goals);
            });
            
            // do we want to push event update here? probably
        }

        private void SaveGoals_Click(object? sender, RoutedEventArgs? e)
        {
            if (_activeGoalSet == null) return;

            _activeGoalSet.Name = GoalSetNameBox.Text;

            using var db = new AppDbContext();
            db.Update(_activeGoalSet);
            foreach (StackPanel panel in GoalsStack.Children.OfType<StackPanel>())
            {
                if (panel.Tag is SubathonGoal goal)
                {
                    var textBox = panel.Children[0] as TextBox;
                    var pointsBox = panel.Children[1] as TextBox;

                    goal.Text = textBox?.Text ?? "";
                    if (int.TryParse(pointsBox?.Text, out int points))
                        goal.Points = points;
                    db.Update(goal);
                }
            }

            db.SaveChanges();
            if (sender != null && e != null)
            {
                Dispatcher.InvokeAsync(() => { LoadGoals(); });
                SubathonEvents.RaiseSubathonGoalListUpdated(_activeGoalSet!.Goals);
                // TODO push update event
            }
        }
    }
}
