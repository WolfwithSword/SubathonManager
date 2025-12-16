using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Events;
using SubathonManager.Data;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;

namespace SubathonManager.UI.Views;

public partial class GoalsView
{
    public ObservableCollection<GoalViewModel> Goals { get; set; } = new();
    private long _subathonLastPoints = -1;
    private GoalsType _type = GoalsType.Points;
    private string _currency = "";
    private readonly IDbContextFactory<AppDbContext> _factory;
    
    public GoalsView()
    {
        _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        InitializeComponent();
        LoadGoals();
        SubathonEvents.SubathonDataUpdate += OnSubathonUpdate;
        SubathonEvents.SubathonGoalListUpdated += OnGoalsUpdate;
    }

    private void OnGoalsUpdate(List<SubathonGoal> goals, long points, GoalsType type)
    {
        Dispatcher.InvokeAsync(() => { LoadGoals() ;});
    }

    private void OnSubathonUpdate(SubathonData subathon, DateTime timestamp)
    {
        if (string.IsNullOrWhiteSpace(_currency)) _currency = subathon.Currency ?? "";
            
        if (_currency != subathon.Currency ||
            (_subathonLastPoints != subathon.Points && _type == GoalsType.Points) || 
            (_subathonLastPoints != subathon.GetRoundedMoneySum() && _type == GoalsType.Money))
        {
            Dispatcher.InvokeAsync(() =>
            {
                foreach (var goal in Goals)
                {
                    if (_type == GoalsType.Points && 
                        (!goal.Completed && subathon.Points >= goal.Points || subathon.Points == 0))
                    {
                        _currency = subathon.Currency ?? "";
                        LoadGoals();
                        break;
                    }
                    if (_type == GoalsType.Money && 
                        (!goal.Completed && subathon.GetRoundedMoneySum() >= goal.Points
                         || subathon.GetRoundedMoneySum() == 0 || _currency != subathon.Currency))
                    {
                        _currency = subathon.Currency ?? "";
                        LoadGoals();
                        break;
                    }
                }
                _subathonLastPoints = _type == GoalsType.Money ? subathon.GetRoundedMoneySum() :
                    subathon.Points;
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
        _type = activeGoalSet.Type ?? GoalsType.Points;
        
        var activeSubathon = db.SubathonDatas.AsNoTracking().FirstOrDefault(s => s.IsActive);
        long currentPoints = activeSubathon?.Points ?? 0;

        string suffix = "pts";
        if (activeGoalSet.Type == GoalsType.Money)
        {
            currentPoints = activeSubathon?.GetRoundedMoneySum() ?? 0;
            suffix = $"{activeSubathon?.Currency ?? "?"}";
        }

        Goals.Clear();
        foreach (var goal in activeGoalSet.Goals)
        {
            if (currentPoints >= goal.Points)
                Goals.Clear(); // just remove previous completed
            
            Goals.Add(new GoalViewModel
            {
                Text = goal.Text,
                PointsText = $"{goal.Points:N0} {suffix}",
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
        public long Points { get; set; } = 0;
        public string PointsText { get; set; } = "";
        public bool Completed { get; set; } = false;

        public Brush TextColor => Completed ? Brushes.Gray : 
            "Dark".Equals(App.AppConfig!.Get("App", "Theme", "Dark")!, StringComparison.OrdinalIgnoreCase) ?  Brushes.White : Brushes.Black;
        public Brush PointsColor => Completed ? Brushes.DarkGray : 
            "Dark".Equals(App.AppConfig!.Get("App", "Theme","Dark")!, StringComparison.OrdinalIgnoreCase) ?  Brushes.LightBlue : Brushes.DarkBlue;
        public double OpacityValue => Completed ? 0.6 : 1.0;
        public TextDecorationCollection? PointsDecoration =>
            Completed ? TextDecorations.Strikethrough : null;
    }
}