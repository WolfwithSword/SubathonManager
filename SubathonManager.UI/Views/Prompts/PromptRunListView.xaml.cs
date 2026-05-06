using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Data;

namespace SubathonManager.UI.Views.Prompts;
 
public partial class PromptRunListView
{
    public ObservableCollection<PromptRunViewModel> RunItems { get; set; } = new();
    private const int MaxItems = 20;
    private readonly IDbContextFactory<AppDbContext> _factory;
 
    public PromptRunListView()
    {
        _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        InitializeComponent();
        PromptRunListPanel.ItemsSource = RunItems;
 
        Task.Run(LoadRecentRuns);
 
        SubathonEvents.PromptRunStarted += (run, _) => Dispatcher.InvokeAsync(LoadRecentRuns);
        SubathonEvents.PromptRunUpdate += OnRunUpdate;
        SubathonEvents.PromptRunProgressUpdated += OnProgressUpdated;
    }
 
    private void OnRunUpdate(SubathonPromptRun run, SubathonPrompt? prompt)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var vm = RunItems.FirstOrDefault(v => v.RunId == run.Id);
            if (vm != null)
            {
                vm.UpdateFromRun(run);
                if (run.Status == SubathonPromptRunStatus.Completed)
                    vm.Progress = vm.SnapshotTargetValue;
                else if (run.Status != SubathonPromptRunStatus.Active)
                    vm.Progress = 0;
            }
            else
                _ = LoadRecentRuns();
        });
    }
 
    private void OnProgressUpdated(SubathonPromptRun run, long progress)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var vm = RunItems.FirstOrDefault(v => v.RunId == run.Id);
            vm?.Progress = progress;
        });
    }
 
    private async Task LoadRecentRuns()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var runs = await db.SubathonPromptRuns
            .Include(r => r.LinkedPrompt)
            .OrderByDescending(r => r.StartedAt)
            .Take(MaxItems)
            .AsNoTracking()
            .ToListAsync();
 
        await Dispatcher.InvokeAsync(() =>
        {
            var existingProgress = RunItems.ToDictionary(v => v.RunId, v => v.Progress);
 
            RunItems.Clear();
            foreach (var run in runs)
            {
                existingProgress.TryGetValue(run.Id, out var progress);
                RunItems.Add(new PromptRunViewModel(run, progress));
            }
        });
    }
}

public class PromptRunViewModel(SubathonPromptRun run, long progress = 0) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
 
    public SubathonPromptRun Run { get; } = run;
    public Guid RunId => Run.Id;
 
    public SubathonPrompt? LinkedPrompt => Run.LinkedPrompt;
    public DateTime StartedAt          => Run.StartedAt;
    public long SnapshotTargetValue    => Run.SnapshotTargetValue;
 
    private SubathonPromptRunStatus _status = run.Status;
    public SubathonPromptRunStatus Status
    {
        get => _status;
        set { _status = value; Notify(); Notify(nameof(IsActive)); }
    }
 
    private DateTime? _endedAt = run.EndedAt;
    public DateTime? EndedAt
    {
        get => _endedAt;
        set { _endedAt = value; Notify(); }
    }
 
    private long _progress = progress;
    public long Progress
    {
        get => _progress;
        set { _progress = value; Notify(); Notify(nameof(ProgressText)); Notify(nameof(ProgressPct)); }
    }
 
    public string ProgressText => $"{_progress} / {SnapshotTargetValue}";
 
    public double ProgressPct => SnapshotTargetValue > 0
        ? Math.Min((double)_progress / SnapshotTargetValue, 1.0)
        : 0;

    public void UpdateFromRun(SubathonPromptRun updated)
    {
        Status  = updated.Status;
        EndedAt = updated.EndedAt;
        Notify(nameof(IsActive));
    }
 
    public bool IsActive => Status == SubathonManager.Core.Enums.SubathonPromptRunStatus.Active;
}
