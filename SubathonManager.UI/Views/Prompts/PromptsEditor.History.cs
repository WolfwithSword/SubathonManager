using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Services;
using SubathonManager.UI.Controls;
using SubathonManager.UI.Services;
using SubathonManager.UI.UiUtils;
using SubathonManager.UI.Views.Actions;

namespace SubathonManager.UI.Views.Prompts;

public partial class PromptsEditor {
    private const int HistoryPageSize = 50;
    private readonly ILogger? _logger = AppServices.Provider.GetService<ILogger<PromptsEditor>>();
    private readonly Dictionary<Guid, long> _historyProgress = new();
    private SubathonPromptRunStatus? _historyFilter;
    private bool _historyLoading;
    private int _historyOffset;

    private void InitHistory() {
        SubathonEvents.PromptRunStarted += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadHistoryAsync());

        SubathonEvents.PromptRunUpdate += (run, prompt) => Dispatcher.UIThread.Post(() =>
            UpdateHistoryRow(run.Id, r => {
                r.Status = run.Status;
                r.EndedAt = run.EndedAt;
                r.LinkedPrompt ??= prompt;
            }));

        SubathonEvents.PromptRunProgressUpdated += (run, progress) => Dispatcher.UIThread.Post(() => {
            _historyProgress[run.Id] = progress;
            UpdateHistoryRow(run.Id, _ => { });
        });

        SubathonEvents.PromptRunActionStatusChanged += run => Dispatcher.UIThread.Post(() =>
            UpdateHistoryRow(run.Id, r => {
                r.ActionStatus = run.ActionStatus;
                r.ActionProgress = run.ActionProgress;
            }));

        Loaded += (_, _) => {
            ServiceManager.Actions.CustomActionsChanged -= OnCustomActionsChanged;
            ServiceManager.Actions.CustomActionsChanged += OnCustomActionsChanged;
        };
        Unloaded += (_, _) => ServiceManager.Actions.CustomActionsChanged -= OnCustomActionsChanged;
    }

    #region History

    private async Task LoadHistoryAsync(bool append = false) {
        if (_historyLoading) return;
        _historyLoading = true;
        try {
            if (!append) {
                _historyOffset = 0;
                await Dispatcher.UIThread.InvokeAsync(() => HistoryStack.Children.Clear());
            }

            if (_activeSet == null) return;
            Guid setId = _activeSet.Id;

            await using AppDbContext db = await _factory.CreateDbContextAsync();
            List<SubathonPromptRun> runs = await db.SubathonPromptRuns
                .Include(r => r.LinkedPrompt)
                .Where(r => r.SetId == setId && (_historyFilter == null || r.Status == _historyFilter))
                .OrderByDescending(r => r.StartedAt)
                .Skip(_historyOffset).Take(HistoryPageSize).AsNoTracking()
                .ToListAsync();

            foreach (SubathonPromptRun run in runs.Where(r => r is { IsActive: true, LinkedPrompt: not null }))
                _historyProgress[run.Id] = await PromptOrchestratorService.GetCurrentCountAsync(db, run.LinkedPrompt!)
                                           - run.BaselineCount;

            _historyOffset += runs.Count;
            await Dispatcher.UIThread.InvokeAsync(() => {
                foreach (SubathonPromptRun run in runs) HistoryStack.Children.Add(BuildHistoryRow(run));
            });
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "[Prompts] Could not load prompt history");
        }
        finally {
            _historyLoading = false;
        }
    }

    private void UpdateHistoryRow(Guid runId, Action<SubathonPromptRun> change) {
        for (var i = 0; i < HistoryStack.Children.Count; i++) {
            if (HistoryStack.Children[i] is not Grid { Tag: SubathonPromptRun run } || run.Id != runId) continue;
            change(run);
            HistoryStack.Children[i] = BuildHistoryRow(run);
            return;
        }
    }

    private Grid BuildHistoryRow(SubathonPromptRun run) {
        var row = new Grid {
            Margin = new Thickness(2, 1, 2, 1),
            MinHeight = 26,
            Tag = run,
            ColumnDefinitions = new ColumnDefinitions("78,1.7*,64,*,76")
        };

        var timeLabel = new TextBlock {
            Text = run.StartedAt.ToString("MM/dd HH:mm"),
            FontSize = 10,
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(timeLabel, $"Started {run.StartedAt:yyyy-MM-dd HH:mm:ss}"
                                  + (run.EndedAt is { } ended ? $"\nEnded {ended:yyyy-MM-dd HH:mm:ss}" : ""));

        SubathonPrompt? prompt = run.LinkedPrompt;
        var promptLabel = new TextBlock {
            Text = prompt?.Text ?? "(deleted)",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0, 4, 0)
        };

        ToolTip.SetTip(promptLabel, prompt == null
            ? "Prompt was deleted"
            : $"{prompt.Text}\n{prompt.Type} - goal {run.SnapshotTargetValue} - "
              + $"{(int)prompt.CompletionDuration.TotalMinutes} min");

        string progress = run.Status switch {
            SubathonPromptRunStatus.Completed => $"{run.SnapshotTargetValue} / {run.SnapshotTargetValue}",
            SubathonPromptRunStatus.Active =>
                $"{Math.Max(0, _historyProgress.GetValueOrDefault(run.Id))} / {run.SnapshotTargetValue}",
            _ => "-"
        };
        var progressLabel = new TextBlock {
            Text = progress,
            FontSize = 10,
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var statusLabel = new TextBlock {
            Text = run.Status.ToString(),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = RunStatusBrush(run.Status),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        Grid.SetColumn(timeLabel, 0);
        Grid.SetColumn(promptLabel, 1);
        Grid.SetColumn(progressLabel, 2);
        Grid.SetColumn(statusLabel, 4);
        row.Children.Add(timeLabel);
        row.Children.Add(promptLabel);
        row.Children.Add(progressLabel);
        row.Children.Add(BuildActionCell(run));
        row.Children.Add(statusLabel);
        return row;
    }

    private Grid BuildActionCell(SubathonPromptRun run) {
        var cell = new Grid { Margin = new Thickness(4, 0, 2, 0), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(cell, 3);

        if (run.ActionId is not { } actionId) {
            cell.Children.Add(new TextBlock {
                Text = "-", FontSize = 10, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center
            });
            return cell;
        }

        string name = ServiceManager.Actions.GetCustomAction(actionId)?.Name ?? "(missing action)";
        (string state, IBrush brush, string tip) = run.ActionStatus switch {
            SubathonPromptActionStatus.Running => ("running", Brushes.Goldenrod, "Running"),
            SubathonPromptActionStatus.Done => ("ran", Brushes.MediumSeaGreen, "Done"),
            SubathonPromptActionStatus.Failed => ("failed", Brushes.IndianRed,
                "Did not finish"),
            _ when run.Status == SubathonPromptRunStatus.Active => ("waiting", Brushes.Gray,
                "Waiting..."),
            _ => ("not run", Brushes.Gray, "Only runs on completion")
        };

        var label = new TextBlock {
            Text = $"{name} - {state}",
            FontSize = 10,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ToolTip.SetTip(label, $"{name}\n{tip}");
        cell.Children.Add(label);

        if (run.ActionStatus != SubathonPromptActionStatus.Failed) return cell;

        var playBtn = new Button {
            Content = new SymIcon { Glyph = "Play16" },
            Width = 20, Height = 20,
            Padding = new Thickness(1),
            Margin = new Thickness(3, 0, 0, 0)
        };

        ToolTip.SetTip(playBtn, "Run the action again, resuming where it stopped");
        playBtn.Click += async (_, _) => {
            playBtn.IsEnabled = false;
            ActionRunResult result = await ServiceManager.Actions.RunPromptActionAsync(run.Id, true);
            if (result == ActionRunResult.Skipped) playBtn.IsEnabled = true;
        };

        Grid.SetColumn(playBtn, 1);
        cell.Children.Add(playBtn);
        return cell;
    }

    private static IBrush RunStatusBrush(SubathonPromptRunStatus status) {
        return status switch {
            SubathonPromptRunStatus.Active => new SolidColorBrush(Color.FromRgb(80, 180, 255)),
            SubathonPromptRunStatus.Completed => new SolidColorBrush(Color.FromRgb(80, 220, 120)),
            SubathonPromptRunStatus.Expired => new SolidColorBrush(Color.FromRgb(200, 100, 60)),
            _ => new SolidColorBrush(Color.FromRgb(140, 140, 140))
        };
    }

    private void HistoryFilter_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        if (_activeSet == null || sender is not ComboBox box) return;
        string? selection = (box.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        _historyFilter = Enum.TryParse(selection ?? "", out SubathonPromptRunStatus status) ? status : null;
        _ = LoadHistoryAsync();
    }

    private void HistoryScroller_ScrollChanged(object? sender, ScrollChangedEventArgs e) {
        if (_historyLoading) return;
        double scrollable = HistoryScroller.Extent.Height - HistoryScroller.Viewport.Height;
        if (scrollable > 0 && scrollable - HistoryScroller.Offset.Y < 100)
            _ = LoadHistoryAsync(true);
    }

    private async void ExportHistory_Click(object? sender, RoutedEventArgs e) {
        try {
            await using AppDbContext db = await _factory.CreateDbContextAsync();
            List<SubathonPromptRun> runs = await db.SubathonPromptRuns
                .Include(r => r.LinkedPrompt)
                .Include(r => r.LinkedSet)
                .OrderByDescending(r => r.StartedAt).AsNoTracking()
                .ToListAsync();

            string path = await CsvUtils.ExportAsync("prompt-history",
                [[
                    "Id", "Set Id", "Set Name", "Prompt Id", "Prompt Text", "Type", "Target", "Status",
                    "Started At", "Expires At", "Ended At", "Action Id", "Action Name", "Action Status"
                ]], runs,
                r => [
                    r.Id, r.SetId, r.LinkedSet?.Name, r.PromptId, r.LinkedPrompt?.Text, r.LinkedPrompt?.Type,
                    r.SnapshotTargetValue, r.Status, r.StartedAt, r.ExpiresAt, r.EndedAt, r.ActionId,
                    r.ActionId is { } id ? ServiceManager.Actions.GetCustomAction(id)?.Name : null,
                    r.ActionId == null ? null : r.ActionStatus
                ]);
            UiHelpers.OpenFolder(Path.GetDirectoryName(path));
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "[Prompts] Could not export prompt history");
        }
    }

    private async void DeleteAllPromptHistory_Click(object? sender, RoutedEventArgs e) {
        if (_activeSet == null) return;
        Guid setId = _activeSet.Id;

        var dialog = new FAContentDialog {
            Title = "Delete Prompt History",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            Content = new TextBlock {
                Text = $"Delete all finished prompt history for \"{_activeSet.Name}\"?",
                TextWrapping = TextWrapping.Wrap,
                Width = 320,
                Margin = new Thickness(4)
            }
        };
        if (await dialog.ShowAsync() != FAContentDialogResult.Primary) return;

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        await db.SubathonPromptRuns
            .Where(r => r.SetId == setId && r.Status != SubathonPromptRunStatus.Active
                                         && r.ActionStatus != SubathonPromptActionStatus.Running)
            .ExecuteDeleteAsync();
        await LoadHistoryAsync();
    }

    #endregion

    #region Optional Action

    private Guid? SelectedPromptActionId => (PromptActionBox.SelectedItem as ComboBoxItem)?.Tag as Guid?;

    private void PopulatePromptActionBox(Guid? select) {
        SuppressChanges(() => {
            PromptActionBox.Items.Clear();
            PromptActionBox.Items.Add(new ComboBoxItem { Content = "(none)", Tag = null });
            foreach (CustomAction action in ServiceManager.Actions.CustomActions)
                PromptActionBox.Items.Add(new ComboBoxItem { Content = action.Name, Tag = (Guid?)action.Id });

            if (select is { } id && PromptActionBox.Items.OfType<ComboBoxItem>().All(i => i.Tag as Guid? != id))
                PromptActionBox.Items.Add(new ComboBoxItem {
                    Content = "(missing from actions library)", Tag = (Guid?)id
                });

            PromptActionBox.SelectedItem = PromptActionBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag as Guid? == select);
        });
        UpdatePromptActionHint();
    }

    private void UpdatePromptActionHint() {
        if (SelectedPromptActionId is not { } id) {
            PromptActionHint.Text = "";
            return;
        }

        if (ServiceManager.Actions.GetCustomAction(id) is not { } action) {
            PromptActionHint.Text = "Action missing from actions library";
            return;
        }

        string problem = action.Graph.Nodes.Count == 0 ? $"\"{action.Name}\" has no steps"
            : !action.Graph.IsValid(out string error) ? error
            : action.Disabled ? $"\"{action.Name}\" is disabled" : "";
        PromptActionHint.Text = problem.Length > 0 ? problem : "";
    }

    private void OnCustomActionsChanged() {
        Dispatcher.UIThread.Post(() => {
            if (_selectedPrompt != null) PopulatePromptActionBox(SelectedPromptActionId);
            foreach (Grid row in HistoryStack.Children.OfType<Grid>())
                if (row.Tag is SubathonPromptRun { ActionId: not null } run)
                    UpdateHistoryRow(run.Id, _ => { });
        });
    }

    private void PromptAction_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        if (_suppressCount > 0) return;
        UpdatePromptActionHint();
        MarkPendingChanges();
    }

    private void OpenPromptActionEditor(CustomAction action) {
        Guid? openedFor = _selectedPrompt?.Id;
        ActionEditorWindow editor = ActionEditorWindow.Open(action);
        editor.Saved += id => {
            if (openedFor == null || _selectedPrompt?.Id != openedFor) return;
            if (SelectedPromptActionId == id) {
                UpdatePromptActionHint();
                return;
            }

            PopulatePromptActionBox(id);
            MarkPendingChanges();
        };
    }

    private void EditPromptAction_Click(object? sender, RoutedEventArgs e) {
        if (SelectedPromptActionId is { } id && ServiceManager.Actions.GetCustomAction(id) is { } action)
            OpenPromptActionEditor(action);
        else
            NewPromptAction_Click(sender, e);
    }

    private void NewPromptAction_Click(object? sender, RoutedEventArgs e) {
        string name = (PromptTextBox.Text ?? "").Trim();
        OpenPromptActionEditor(new CustomAction { Name = name.Length > 0 ? name : "New Action" });
    }

    private async void ImportPromptAction_Click(object? sender, RoutedEventArgs e) {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        IReadOnlyList<IStorageFile> picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = "Import Custom Action",
            AllowMultiple = false,
            FileTypeFilter = [
                new FilePickerFileType("Subathon Manager Action") { Patterns = [$"*{CustomAction.FileExtension}"] }
            ]
        });
        if (picked.Count == 0) return;

        (CustomAction Action, bool Replaced)? imported =
            await ServiceManager.Actions.ImportCustomActionAsync(picked[0].Path.LocalPath);
        if (imported is not { } result) {
            StatusText.Text = "That file is not a valid custom action";
            return;
        }

        PopulatePromptActionBox(result.Action.Id);
        StatusText.Text = result.Replaced
            ? $"Updated \"{result.Action.Name}\" in the actions library" : $"Imported \"{result.Action.Name}\"";
        MarkPendingChanges();
    }

    #endregion
}
