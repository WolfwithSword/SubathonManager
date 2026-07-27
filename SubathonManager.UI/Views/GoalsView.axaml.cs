using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;

namespace SubathonManager.UI.Views;

public partial class GoalsView : UserControl
{
    private ObservableCollection<GoalViewModel> Goals { get; } = new();
    private long _subathonLastPoints = -1;
    private GoalsType _type = GoalsType.Points;
    private string _currency = "";
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly bool _isDarkTheme;

    public GoalsView()
    {
        _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();

        _isDarkTheme = global::Avalonia.Application.Current?.ActualThemeVariant
            != global::Avalonia.Styling.ThemeVariant.Light;
            
        InitializeComponent();
        LoadGoals();
        SubathonEvents.SubathonDataUpdate += OnSubathonUpdate;
        SubathonEvents.SubathonGoalListUpdated += OnGoalsUpdate;
    }

    private void OnGoalsUpdate(List<SubathonGoal> goals, long points, GoalsType type)
        => Dispatcher.UIThread.Post(LoadGoals);

    private void OnSubathonUpdate(SubathonData subathon, DateTime timestamp)
    {
        if (string.IsNullOrWhiteSpace(_currency)) _currency = subathon.Currency ?? "";

        Dispatcher.UIThread.Post(() =>
        {
            long moneySum = subathon.GetRoundedMoneySum();
            if (_currency == subathon.Currency &&
                (_subathonLastPoints == subathon.Points || _type != GoalsType.Points) &&
                (_subathonLastPoints == moneySum || _type != GoalsType.Money)) return;

            if (Goals.Any(goal => (_type == GoalsType.Points &&
                                   ((!goal.Completed && subathon.Points >= goal.Points) || subathon.Points == 0)) ||
                                  (_type == GoalsType.Money &&
                                   ((!goal.Completed && moneySum >= goal.Points)
                                    || moneySum == 0 || _currency != subathon.Currency))))
            {
                _currency = subathon.Currency ?? "";
                LoadGoals();
            }

            _subathonLastPoints = _type == GoalsType.Money ? moneySum : subathon.Points;
        });
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
            long moneySum = activeSubathon!.GetRoundedMoneySum();
            currentPoints = moneySum;
            suffix = $"{activeSubathon?.Currency ?? "?"}";
        }

        Goals.Clear();
        foreach (var goal in activeGoalSet.Goals)
        {
            if (currentPoints >= goal.Points)
                Goals.Clear(); // keep only from the last completed onward

            Goals.Add(new GoalViewModel(_isDarkTheme)
            {
                Text = goal.Text,
                PointsText = $"{goal.Points:N0} {suffix}",
                Points = goal.Points,
                Completed = currentPoints >= goal.Points
            });
        }

        Dispatcher.UIThread.Post(() =>
        {
            GoalsList.ItemsSource = Goals;
            var lastCompleted = Goals
                .Select((g, i) => new { g, i })
                .LastOrDefault(x => x.g.Completed);
            if (lastCompleted == null) return;
            (GoalsList.ContainerFromIndex(lastCompleted.i))?.BringIntoView();
        });
    }

    public class GoalViewModel(bool isDarkTheme)
    {
        public string Text { get; set; } = "";
        public long Points { get; set; }
        public string PointsText { get; set; } = "";
        public bool Completed { get; set; }

        private readonly bool _isDarkTheme = isDarkTheme;

        public IBrush TextColor => Completed ? Brushes.Gray : (_isDarkTheme ? Brushes.White : Brushes.Black);
        public IBrush PointsColor => Completed ? Brushes.DarkGray : (_isDarkTheme ? Brushes.LightBlue : Brushes.DarkBlue);

        public double OpacityValue => Completed ? 0.6 : 1.0;
        public TextDecorationCollection? PointsDecoration => Completed ? TextDecorations.Strikethrough : null;
    }
}
