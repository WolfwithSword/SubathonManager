using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SubathonManager.UI.Controls;

public partial class FilterPopout : UserControl {
    private readonly List<FilterGroup> _groups = [];
    private readonly List<FilterOption> _options = [];

    public FilterPopout() {
        InitializeComponent();
        GroupList.ItemsSource = _groups;
        UpdateSummary();
    }

    public string EmptyText { get; set; } = "All (no filter)";

    public IReadOnlyList<FilterOption> Options => _options;

    public IEnumerable<FilterOption> SelectedOptions => _options.Where(o => o.Selected);

    public event EventHandler? SelectionChanged;

    public void SetOptions(IEnumerable<FilterOption> options, bool expandGroups = false) {
        foreach (FilterOption option in _options) option.PropertyChanged -= OnOptionChanged;

        _options.Clear();
        _options.AddRange(options);
        foreach (FilterOption option in _options) option.PropertyChanged += OnOptionChanged;
        _groups.Clear();
        foreach (IGrouping<string, FilterOption> group in _options.GroupBy(o => o.Group, StringComparer.OrdinalIgnoreCase))
            _groups.Add(new FilterGroup {
                Header = string.IsNullOrWhiteSpace(group.Key) ? "All" : group.Key,
                Items = group.ToList(),
                IsExpanded = expandGroups
            });

        GroupList.ItemsSource = null;
        GroupList.ItemsSource = _groups;
        UpdateSummary();
    }

    public void SetSelected(IEnumerable<string> values) {
        var wanted = values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (FilterOption option in _options) option.Selected = wanted.Contains(option.Value);
    }

    public void ClearSelection() {
        foreach (FilterOption option in _options) option.Selected = false;
    }

    private void OnOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) {
        if (e.PropertyName != nameof(FilterOption.Selected)) return;
        UpdateSummary();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSummary() {
        List<FilterOption> selected = _options.Where(o => o.Selected).ToList();

        if (selected.Count == 0) SummaryBox.Text = EmptyText;
        else if (selected.Count == _options.Count) SummaryBox.Text = $"All {_options.Count}";
        else SummaryBox.Text = $"{selected.Count} selected: {string.Join(", ", selected.Select(o => o.Label))}";

        if (CountText != null)
            CountText.Text = $"{selected.Count} of {_options.Count} selected";
    }

    private void Open_Click(object? sender, RoutedEventArgs e) {
        SearchBox.Text = string.Empty;
        ApplySearch(string.Empty);
        ChoicesPopup.IsOpen = true;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) {
        ChoicesPopup.IsOpen = false;
    }

    private void Search_TextChanged(object? sender, TextChangedEventArgs e) {
        ApplySearch(SearchBox.Text ?? string.Empty);
    }

    private void ApplySearch(string term) {
        term = term.Trim();

        foreach (FilterGroup group in _groups) {
            var anyVisible = false;
            foreach (FilterOption option in group.Items) {
                bool hit = term.Length == 0 ||
                           option.Label.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                           option.Value.Contains(term, StringComparison.OrdinalIgnoreCase);
                option.MatchesSearch = hit;
                anyVisible |= hit;
            }

            group.HasMatches = anyVisible;
            if (term.Length > 0 && anyVisible) group.IsExpanded = true;
        }
    }

    private void All_Click(object? sender, RoutedEventArgs e) {
        foreach (FilterOption option in _options.Where(o => o.MatchesSearch)) option.Selected = true;
    }

    private void None_Click(object? sender, RoutedEventArgs e) {
        foreach (FilterOption option in _options.Where(o => o.MatchesSearch)) option.Selected = false;
    }

    private void ExpandAll_Click(object? sender, RoutedEventArgs e) {
        foreach (FilterGroup group in _groups) group.IsExpanded = true;
    }

    private void CollapseAll_Click(object? sender, RoutedEventArgs e) {
        foreach (FilterGroup group in _groups) group.IsExpanded = false;
    }
}
