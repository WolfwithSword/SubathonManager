using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Events;
using SubathonManager.Data;
using SubathonManager.Core;
using SubathonManager.Core.Models;

namespace SubathonManager.UI.Views;

public partial class GoalsView
{
    public ObservableCollection<GoalViewModel> Goals { get; set; } = new();
    private int _subathonLastPoints = -1;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public GoalsView()
    {
        _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        InitializeComponent();
        LoadGoals();
        SubathonEvents.SubathonDataUpdate += OnSubathonUpdate;
        SubathonEvents.SubathonGoalListUpdated += OnGoalsUpdate;
    }

    private void OnGoalsUpdate(List<SubathonGoal> goals, int points)
    {
        Dispatcher.InvokeAsync(() => { LoadGoals() ;});
    }

    private void OnSubathonUpdate(SubathonData subathon, DateTime timestamp)
    {
        if (_subathonLastPoints != subathon.Points)
        {
            Dispatcher.InvokeAsync(() =>
            {
                foreach (var goal in Goals)
                {
                    if (!goal.Completed && subathon.Points >= goal.Points || subathon.Points == 0)
                    {
                        LoadGoals();
                        break;
                    }
                }
                _subathonLastPoints = subathon.Points;
            });
        }
    }
    
    private void LoadGoals()
    {
        using var db = _factory.CreateDbContext();
        
        var activeGoalSet = db.SubathonGoalSets
            .Include(gs => gs.Goals.OrderBy(g => g.Points))
            .FirstOrDefault(gs => gs.IsActive);
        if (activeGoalSet == null) return;
        
        var activeSubathon = db.SubathonDatas.AsNoTracking().FirstOrDefault(s => s.IsActive);
        int currentPoints = activeSubathon?.Points ?? 0;

        Goals.Clear();
        foreach (var goal in activeGoalSet.Goals)
        {
            // TODO remove this, and make the BringIntoView more reliable?
            if (currentPoints >= goal.Points)
                Goals.Clear(); // just remove previous completed
            
            Goals.Add(new GoalViewModel
            {
                Text = goal.Text,
                PointsText = $"{goal.Points:N0} pts",
                Points = goal.Points,
                Completed =  currentPoints >= goal.Points
            });
        }

        GoalsList.ItemsSource = Goals;
        Dispatcher.InvokeAsync(() =>
        {
            var lastCompleted = Goals
                .Select((g, i) => new { g, i })
                .LastOrDefault(x => x.g.Completed);
            
            if (lastCompleted != null)
            {
                var item = GoalsList.ItemContainerGenerator.ContainerFromIndex(lastCompleted.i) as FrameworkElement;
                item?.BringIntoView();
            }
        });
    }
    
    public class GoalViewModel
    {
        public string Text { get; set; } = "";
        public int Points { get; set; } = 0;
        public string PointsText { get; set; } = "";
        public bool Completed { get; set; } = false;

        public Brush TextColor => Completed ? Brushes.Gray : Brushes.White;
        public Brush PointsColor => Completed ? Brushes.DarkGray : Brushes.LightBlue;
        public double OpacityValue => Completed ? 0.6 : 1.0;
        public TextDecorationCollection? PointsDecoration =>
            Completed ? TextDecorations.Strikethrough : null;
    }
}