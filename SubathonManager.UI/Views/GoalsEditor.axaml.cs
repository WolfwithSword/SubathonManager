using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.UI.Controls;
using SubathonManager.UI.UiUtils;
using SubathonManager.UI.Validation;

namespace SubathonManager.UI.Views;

public partial class GoalsEditor : UserControl {
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly HashSet<SubathonGoal> _unsavedGoals = new();
    private SubathonGoalSet? _activeGoalSet;
    private bool _appendRowAfterSave;
    private bool _initialized;
    private int _suppressCount;

    public GoalsEditor() {
        _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        InitializeComponent();
        GoalSetType.ItemsSource = Enum.GetNames<GoalsType>().ToList();
        LoadAllSets();
        SubathonEvents.SubathonDataUpdate += UpdatePointsCount;

        Loaded += (_, _) => {
            if (_initialized) return;
            _initialized = true;
            EnterKeyCommit.Attach(this, source => {
                GoalSetNameBox_LostFocus(GoalSetNameBox, new RoutedEventArgs());
                _appendRowAfterSave = IsInLastGoalRow(source);
                SaveGoals_Click(this, new RoutedEventArgs());
            });
        };
    }

    private void UpdatePointsCount(SubathonData subathon, DateTime time) {
        Dispatcher.UIThread.Post(() => {
            double moneySum = subathon.GetRoundedMoneySumWithCents();
            PointsValue.Text = $"{subathon.Points:N0} Pts";
            MoneyValue.Text = $"{subathon.Currency} {moneySum:N2}".Trim();
        });
    }

    private void LoadAllSets() {
        using AppDbContext db = _factory.CreateDbContext();
        List<SubathonGoalSet> allSets = db.SubathonGoalSets.OrderBy(s => s.Name).ToList();

        SuppressChanges(() => {
            GoalSetSelectorBox.Items.Clear();
            foreach (SubathonGoalSet s in allSets)
                GoalSetSelectorBox.Items.Add(new ComboBoxItem { Content = s.Name, Tag = s.Id });
        });

        if (allSets.Count == 0) {
            StatusText.Text = "No goal sets found. Create a new one.";
            DeleteGoalSetBtn.IsEnabled = false;
            return;
        }

        DeleteGoalSetBtn.IsEnabled = allSets.Count > 1;

        SubathonGoalSet active = allSets.FirstOrDefault(s => s.IsActive) ?? allSets.First();
        SuppressChanges(() => {
            ComboBoxItem? item = GoalSetSelectorBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (Guid)i.Tag! == active.Id);
            GoalSetSelectorBox.SelectedItem = item;
        });

        LoadSetById(active.Id);
    }

    private void LoadSetById(Guid setId) {
        using AppDbContext db = _factory.CreateDbContext();
        _activeGoalSet = db.SubathonGoalSets
            .Include(gs => gs.Goals)
            .FirstOrDefault(gs => gs.Id == setId);

        if (_activeGoalSet == null) {
            StatusText.Text = "Set not found.";
            return;
        }

        foreach (SubathonGoalSet s in db.SubathonGoalSets.Where(s => s.Id != setId))
            s.IsActive = false;
        _activeGoalSet.IsActive = true;
        db.SaveChanges();

        StatusText.Text = "";

        SuppressChanges(() => {
            GoalSetNameBox.Text = _activeGoalSet.Name;
            GoalSetType.SelectedItem = $"{_activeGoalSet.Type ?? GoalsType.Points}";
        });

        Dispatcher.UIThread.Post(LoadGoals);
        RaiseGoalListUpdated(db);
    }

    private void GoalSetSelectorBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        if (_suppressCount > 0) return;
        if (GoalSetSelectorBox.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not Guid setId) return;
        LoadSetById(setId);
    }

    private void GoalSetNameBox_LostFocus(object? sender, RoutedEventArgs e) {
        if (_activeGoalSet == null) return;
        string newName = (GoalSetNameBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName == _activeGoalSet.Name) return;

        _activeGoalSet.Name = newName;

        using AppDbContext db = _factory.CreateDbContext();
        SubathonGoalSet? tracked = db.SubathonGoalSets.Find(_activeGoalSet.Id);
        if (tracked == null) return;
        tracked.Name = newName;
        db.SaveChanges();

        SuppressChanges(() => {
            ComboBoxItem? item = GoalSetSelectorBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (Guid)i.Tag! == _activeGoalSet.Id);
            if (item != null) item.Content = newName;
        });
    }

    private void GoalSetType_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        bool realChange = DirtySaveGuard.Consume(sender);
        if (_suppressCount > 0 || _activeGoalSet == null || !realChange) return;
        UpdateSaveButtonBorder(true);
    }

    private async void NewGoalSet_Click(object? sender, RoutedEventArgs e) {
        await using AppDbContext db = await _factory.CreateDbContextAsync();

        foreach (SubathonGoalSet s in db.SubathonGoalSets)
            s.IsActive = false;

        var newSet = new SubathonGoalSet { Name = "New Goal Set", IsActive = true };
        db.SubathonGoalSets.Add(newSet);
        await db.SaveChangesAsync();

        var newItem = new ComboBoxItem { Content = newSet.Name, Tag = newSet.Id };
        SuppressChanges(() => {
            GoalSetSelectorBox.Items.Add(newItem);
            GoalSetSelectorBox.SelectedItem = newItem;
        });

        _activeGoalSet = newSet;
        SuppressChanges(() => {
            GoalSetNameBox.Text = newSet.Name;
            GoalSetType.SelectedItem = $"{GoalsType.Points}";
        });

        GoalsStack.Children.Clear();
        StatusText.Text = "";
        DeleteGoalSetBtn.IsEnabled = true;
        GoalSetNameBox.Focus();
        GoalSetNameBox.SelectAll();
    }

    private async void DeleteGoalSet_Click(object? sender, RoutedEventArgs e) {
        if (_activeGoalSet == null) return;

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        int total = await db.SubathonGoalSets.CountAsync();
        if (total <= 1) return;

        Guid deletingId = _activeGoalSet.Id;
        SubathonGoalSet? tracked = await db.SubathonGoalSets.FindAsync(deletingId);
        if (tracked != null) db.SubathonGoalSets.Remove(tracked);

        SubathonGoalSet? next = await db.SubathonGoalSets
            .Where(s => s.Id != deletingId)
            .OrderBy(s => s.Name)
            .FirstOrDefaultAsync();
        if (next != null) next.IsActive = true;

        await db.SaveChangesAsync();

        SuppressChanges(() => {
            ComboBoxItem? item = GoalSetSelectorBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (Guid)i.Tag! == deletingId);
            if (item != null) GoalSetSelectorBox.Items.Remove(item);
        });

        if (next != null) {
            ComboBoxItem? selectItem = GoalSetSelectorBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (Guid)i.Tag! == next.Id);
            SuppressChanges(() => GoalSetSelectorBox.SelectedItem = selectItem);
            LoadSetById(next.Id);
        }

        DeleteGoalSetBtn.IsEnabled = GoalSetSelectorBox.Items.Count > 1;
    }

    private async void LoadGoals() {
        GoalsStack.Children.Clear();
        _unsavedGoals.Clear();
        if (_activeGoalSet == null) return;
        _suppressCount++;

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        await db.Entry(_activeGoalSet).ReloadAsync();

        List<SubathonGoal> goals = await db.SubathonGoals
            .Where(g => g.GoalSetId == _activeGoalSet.Id)
            .OrderBy(g => g.Points)
            .ToListAsync();
        _activeGoalSet.Goals = goals;

        foreach (SubathonGoal goal in goals) AddGoalRow(goal, false);

        if (_appendRowAfterSave) {
            _appendRowAfterSave = false;
            AppendBlankGoalRow();
        }

        GoalsEditorScroller.Height = 600;
        GoalsEditorScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Dispatcher.UIThread.Post(() => {
            _suppressCount--;
            UiHelpers.UpdateButtonPendingBorder(SaveButtonBorder, false);
        }, DispatcherPriority.Background);
    }

    private StackPanel AddGoalRow(SubathonGoal goal, bool isUnsaved) {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 8) };

        var textBox = new TextBox {
            Text = goal.Text,
            Width = 522,
            Margin = new Thickness(0, 0, 8, 0),
            PlaceholderText = "Goal Description...",
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(textBox, "Goal Description");
        textBox.TextChanged += Value_OnChanged;
        DirtySaveGuard.Rebase(textBox);
        TextBoxAssist.SetClear(textBox, true);

        var pointsBox = new TextBox {
            Text = isUnsaved ? "" : goal.Points.ToString(),
            Width = 80,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(pointsBox, "Points/Money to achieve");
        NumericInputBehaviour.SetMode(pointsBox, NumericInputBehaviour.NumericMode.Integer);
        pointsBox.TextChanged += Value_OnChanged;
        DirtySaveGuard.Rebase(pointsBox);

        var deleteBtn = new Button {
            Content = new SymIcon { Glyph = "Delete20", HorizontalAlignment = HorizontalAlignment.Center },
            Width = 32, Height = 32,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = Brushes.Red,
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(4, 0, 0, 0)
        };
        ToolTip.SetTip(deleteBtn, "Remove");
        deleteBtn.Click += (_, _) => DeleteGoal_Click(goal);

        panel.Children.Add(textBox);
        panel.Children.Add(pointsBox);
        panel.Children.Add(deleteBtn);
        panel.Tag = goal;

        if (isUnsaved) _unsavedGoals.Add(goal);
        GoalsStack.Children.Add(panel);
        return panel;
    }

    private void AppendBlankGoalRow() {
        if (_activeGoalSet == null) return;

        var goal = new SubathonGoal { Text = "", Points = 0, GoalSetId = _activeGoalSet.Id };
        StackPanel panel = AddGoalRow(goal, true);

        Dispatcher.UIThread.Post(() => {
            panel.BringIntoView();
            (panel.Children[0] as TextBox)?.Focus();
        }, DispatcherPriority.Background);
    }

    private bool IsInLastGoalRow(object? source) {
        StackPanel? last = GoalsStack.Children.OfType<StackPanel>().LastOrDefault();
        if (last == null) return false;

        var visual = source as Visual;
        while (visual != null) {
            if (ReferenceEquals(visual, last)) return true;
            visual = visual.GetVisualParent();
        }

        return false;
    }

    private async void DeleteGoal_Click(SubathonGoal goal) {
        if (_unsavedGoals.Remove(goal)) {
            StackPanel? row = GoalsStack.Children.OfType<StackPanel>()
                .FirstOrDefault(p => ReferenceEquals(p.Tag, goal));
            if (row != null) GoalsStack.Children.Remove(row);
            return;
        }

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        db.SubathonGoals.Remove(goal);
        await db.SaveChangesAsync();
        await db.Entry(_activeGoalSet!).ReloadAsync();
        await Dispatcher.UIThread.InvokeAsync(() => {
            LoadGoals();
            RaiseGoalListUpdated(db);
        });
    }

    private async void AddGoal_Click(object? sender, RoutedEventArgs e) {
        if (_activeGoalSet == null) return;
        await SaveGoalsAsync(null, null);

        await using AppDbContext db = await _factory.CreateDbContextAsync();

        long maxPoints = _activeGoalSet.Goals.Count > 0 ? _activeGoalSet.Goals.Max(g => g.Points) : 0;
        var newGoal = new SubathonGoal { Points = maxPoints + 1, GoalSetId = _activeGoalSet.Id };

        db.SubathonGoals.Add(newGoal);
        await db.SaveChangesAsync();
        await db.Entry(_activeGoalSet).ReloadAsync();

        await Dispatcher.UIThread.InvokeAsync(() => {
            LoadGoals();
            RaiseGoalListUpdated(db);
        });
    }

    private async void SaveGoals_Click(object? sender, RoutedEventArgs? e) {
        await SaveGoalsAsync(sender, e);
    }

    private async Task SaveGoalsAsync(object? sender, RoutedEventArgs? e) {
        if (_activeGoalSet == null) {
            _appendRowAfterSave = false;
            return;
        }

        _activeGoalSet.Name = (GoalSetNameBox.Text ?? "").Trim();
        _activeGoalSet.Type = Enum.TryParse($"{GoalSetType.SelectedItem}", out GoalsType type)
            ? type
            : _activeGoalSet.Type;

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        db.Update(_activeGoalSet);

        foreach (StackPanel panel in GoalsStack.Children.OfType<StackPanel>()) {
            if (panel.Tag is not SubathonGoal goal) continue;
            var textBox = panel.Children[0] as TextBox;
            var pointsBox = panel.Children[1] as TextBox;

            bool hasPoints = long.TryParse(pointsBox?.Text, out long pts);
            if (string.IsNullOrWhiteSpace(textBox?.Text) || !hasPoints) {
                if (!_unsavedGoals.Contains(goal)) db.SubathonGoals.Remove(goal);
                continue;
            }

            goal.Text = textBox.Text ?? "";
            goal.Points = pts;

            if (_unsavedGoals.Contains(goal)) db.SubathonGoals.Add(goal);
            else db.Update(goal);
        }

        await db.SaveChangesAsync();

        if (sender != null && e != null) {
            await Dispatcher.UIThread.InvokeAsync(LoadGoals);
            RaiseGoalListUpdated(db);
        }

        UpdateSaveButtonBorder(false);
        await Dispatcher.UIThread.InvokeAsync(() => SaveGoalsBtn.Content = "Saved!");
        await Task.Delay(sender != null ? 1500 : 100);
        await Dispatcher.UIThread.InvokeAsync(() => SaveGoalsBtn.Content = "Save Changes");
    }

    private void RaiseGoalListUpdated(AppDbContext db) {
        if (_activeGoalSet == null) return;
        SubathonData? subathon = db.SubathonDatas.AsNoTracking().FirstOrDefault(s => s.IsActive);
        long pts = subathon?.Points ?? 0;
        if (_activeGoalSet.Type == GoalsType.Money) pts = subathon?.GetRoundedMoneySum() ?? 0;
        SubathonEvents.RaiseSubathonGoalListUpdated(
            _activeGoalSet.Goals, pts, _activeGoalSet.Type ?? GoalsType.Points);
    }

    private void UpdateSaveButtonBorder(bool hasPendingChanges) {
        Dispatcher.UIThread.Post(() => UiHelpers.UpdateButtonPendingBorder(SaveButtonBorder, hasPendingChanges));
    }

    private void SuppressChanges(Action action) {
        _suppressCount++;
        try {
            action();
        }
        finally {
            _suppressCount--;
        }
    }

    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e) {
        (sender as Control)?.Focus();
    }

    private void Value_OnChanged(object? sender, TextChangedEventArgs e) {
        bool realChange = DirtySaveGuard.Consume(sender);
        if (_suppressCount > 0 || !realChange) return;
        UiHelpers.UpdateButtonPendingBorder(SaveButtonBorder, true);
    }

    private async void ExportGoalSet_Click(object? sender, RoutedEventArgs e) {
        if (_activeGoalSet == null) return;

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        SubathonGoalSet? set = await db.SubathonGoalSets
            .Include(s => s.Goals)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == _activeGoalSet.Id);
        if (set == null) return;

        string exportDir = Path.Combine(Config.DataFolder, "exports");
        Directory.CreateDirectory(exportDir);

        string safeName = SafeFileName.Sanitize(set.Name, string.Empty, "goals");
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string filepath = Path.Combine(exportDir, $"{safeName}-{timestamp}.csv");

        string typeHeader = (set.Type ?? GoalsType.Points) == GoalsType.Money ? "Money" : "Points";

        var sb = new StringBuilder();
        sb.AppendLine($"Goal,Value,{typeHeader}");
        foreach (SubathonGoal goal in set.Goals.OrderBy(g => g.Points))
            sb.AppendLine($"{Utils.EscapeCsv(goal.Text)},{goal.Points}");

        await File.WriteAllTextAsync(filepath, sb.ToString(), Encoding.UTF8);

        try {
            UiHelpers.OpenFolder(exportDir);
        }
        catch {
            /**/
        }
    }

    private async void ImportGoalSet_Click(object? sender, RoutedEventArgs e) {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        IReadOnlyList<IStorageFile> picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = "Import Goal Set",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("CSV Files") { Patterns = ["*.csv"] }]
        });
        if (picked.Count == 0) return;
        IStorageFile file = picked[0];
        string filePath = file.Path.LocalPath;

        string[] lines;
        try {
            lines = await File.ReadAllLinesAsync(filePath, Encoding.UTF8);
        }
        catch {
            await ShowInvalidGoalCsvPopup();
            return;
        }

        if (lines.Length < 1) {
            await ShowInvalidGoalCsvPopup();
            return;
        }

        string[] headerCols = ParseCsvLine(lines[0]);
        if (headerCols.Length < 2) {
            await ShowInvalidGoalCsvPopup();
            return;
        }

        var goalType = GoalsType.Points;
        if (headerCols.Length >= 3 &&
            string.Equals(headerCols[2].Trim(), "Money", StringComparison.OrdinalIgnoreCase))
            goalType = GoalsType.Money;

        var goals = new List<SubathonGoal>();
        for (var i = 1; i < lines.Length; i++) {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            string[] cols = ParseCsvLine(lines[i]);
            if (cols.Length < 2 || !long.TryParse(cols[1].Trim(), out long pts)) {
                await ShowInvalidGoalCsvPopup();
                return;
            }

            goals.Add(new SubathonGoal { Text = cols[0], Points = pts });
        }

        string goalSetName = Path.GetFileNameWithoutExtension(filePath);

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        foreach (SubathonGoalSet s in db.SubathonGoalSets)
            s.IsActive = false;

        var newSet = new SubathonGoalSet { Name = goalSetName, IsActive = true, Type = goalType };
        db.SubathonGoalSets.Add(newSet);
        await db.SaveChangesAsync();

        foreach (SubathonGoal g in goals) {
            g.GoalSetId = newSet.Id;
            db.SubathonGoals.Add(g);
        }

        await db.SaveChangesAsync();

        LoadAllSets();
    }

    private static string[] ParseCsvLine(string line) {
        var result = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++) {
            char c = line[i];
            if (inQuotes)
                switch (c) {
                    case '"' when i + 1 < line.Length && line[i + 1] == '"':
                        field.Append('"');
                        i++;
                        break;
                    case '"':
                        inQuotes = false;
                        break;
                    default:
                        field.Append(c);
                        break;
                }
            else
                switch (c) {
                    case '"':
                        inQuotes = true;
                        break;
                    case ',':
                        result.Add(field.ToString());
                        field.Clear();
                        break;
                    default:
                        field.Append(c);
                        break;
                }
        }

        result.Add(field.ToString());
        return result.ToArray();
    }

    private async Task ShowInvalidGoalCsvPopup() {
        var dialog = new FAContentDialog {
            Title = "Invalid CSV",
            CloseButtonText = "OK",
            Content = new TextBlock {
                Text = "The selected file is not a valid goal set CSV and could not be imported.",
                TextWrapping = TextWrapping.Wrap,
                Width = 300,
                Margin = new Thickness(4)
            }
        };
        await dialog.ShowAsync();
    }
}