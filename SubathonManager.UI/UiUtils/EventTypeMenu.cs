using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SubathonManager.Core.Enums;

namespace SubathonManager.UI.UiUtils;

public sealed record EventTypeMenuEntry(
    SubathonEventSource Source,
    string Label,
    bool IsSelected,
    Action OnSelected,
    string? Category = null);

public static class EventTypeMenu
{
    public static void Show(Control placementTarget, IReadOnlyList<EventTypeMenuEntry> entries,
        bool groupBySourceType = true, string? clearLabel = null, Action? onClear = null)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.Bottom };

        var searchBox = new TextBox
        {
            PlaceholderText = "Search...",
            MinWidth = 170,
            Height = 34,
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var searchItem = new MenuItem { Header = searchBox, StaysOpenOnClick = true, Focusable = false };
        flyout.Items.Add(searchItem);

        int fixedItemCount = 1;
        if (onClear != null)
        {
            var clearItem = new MenuItem
            {
                Header = new TextBlock { Text = clearLabel ?? "(none)", FontStyle = FontStyle.Italic }
            };
            clearItem.Click += (_, _) => onClear();
            flyout.Items.Add(clearItem);
            fixedItemCount = 2;
        }

        var nestedItems = BuildNested(entries, groupBySourceType);
        foreach (var item in nestedItems) flyout.Items.Add(item);

        List<EventTypeMenuEntry> currentMatches = [];

        searchBox.TextChanged += (_, _) =>
        {
            while (flyout.Items.Count > fixedItemCount) flyout.Items.RemoveAt(fixedItemCount);
            var query = (searchBox.Text ?? string.Empty).Trim();

            if (query.Length == 0)
            {
                currentMatches.Clear();
                foreach (var item in nestedItems) flyout.Items.Add(item);
                return;
            }

            currentMatches = entries.Where(en => Matches(en, query, groupBySourceType)).ToList();
            foreach (var entry in currentMatches)
            {
                var captured = entry;
                var flat = new MenuItem
                {
                    Header = MakeHeader(entry.Category is { Length: > 0 }
                        ? $"{entry.Source} - {entry.Category} - {entry.Label}"
                        : $"{entry.Source} - {entry.Label}", entry.IsSelected)
                };
                flat.Click += (_, _) => captured.OnSelected();
                flyout.Items.Add(flat);
            }
        };

        searchBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || currentMatches.Count == 0) return;
            e.Handled = true;
            flyout.Hide();
            currentMatches[0].OnSelected();
        };

        flyout.Opened += (_, _) => searchBox.Focus();
        flyout.ShowAt(placementTarget);
    }

    private static bool Matches(EventTypeMenuEntry entry, string query, bool groupBySourceType)
    {
        var haystack = groupBySourceType
            ? $"{entry.Source.GetGroup().GetLabel()} {entry.Source} {entry.Category} {entry.Label}"
            : $"{entry.Source} {entry.Category} {entry.Label}";
        return haystack.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static List<MenuItem> BuildNested(IReadOnlyList<EventTypeMenuEntry> entries, bool groupBySourceType)
    {
        if (!groupBySourceType)
        {
            var sourceItems = new List<MenuItem>();
            foreach (var sourceGroup in entries.GroupBy(en => en.Source)
                         .OrderBy(g => SubathonEventSourceHelper.GetSourceOrder(g.Key)))
                sourceItems.Add(BuildSourceItem(sourceGroup, out _));
            return sourceItems;
        }

        var groupItems = new List<MenuItem>();

        var groups = entries
            .GroupBy(en => en.Source.GetGroup())
            .OrderBy(g => g.Min(en => SubathonEventSourceHelper.GetSourceOrder(en.Source)));

        foreach (var group in groups)
        {
            var groupItem = new MenuItem();
            bool groupHasSelection = false;

            foreach (var sourceGroup in group.GroupBy(en => en.Source)
                         .OrderBy(g => SubathonEventSourceHelper.GetSourceOrder(g.Key)))
            {
                var sourceItem = BuildSourceItem(sourceGroup, out bool sourceHasSelection);
                groupHasSelection |= sourceHasSelection;
                groupItem.Items.Add(sourceItem);
            }

            groupItem.Header = MakeHeader(group.Key.GetLabel(), groupHasSelection);
            groupItems.Add(groupItem);
        }

        return groupItems;
    }

    private static MenuItem BuildSourceItem(IGrouping<SubathonEventSource, EventTypeMenuEntry> sourceGroup,
        out bool hasSelection)
    {
        var sourceItem = new MenuItem();
        hasSelection = false;
        var categoryItems = new Dictionary<string, MenuItem>(StringComparer.OrdinalIgnoreCase);
        var categorySelections = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in sourceGroup)
        {
            var captured = entry;
            var leaf = new MenuItem { Header = MakeHeader(entry.Label, entry.IsSelected) };
            hasSelection |= entry.IsSelected;
            leaf.Click += (_, _) => captured.OnSelected();

            if (entry.Category is { Length: > 0 } category)
            {
                if (!categoryItems.TryGetValue(category, out var categoryItem))
                {
                    categoryItem = new MenuItem();
                    categoryItems[category] = categoryItem;
                    sourceItem.Items.Add(categoryItem);
                }
                categoryItem.Items.Add(leaf);
                categorySelections[category] = categorySelections.GetValueOrDefault(category) | entry.IsSelected;
            }
            else
            {
                sourceItem.Items.Add(leaf);
            }
        }

        foreach (var (category, categoryItem) in categoryItems)
            categoryItem.Header = MakeHeader(category, categorySelections.GetValueOrDefault(category));

        sourceItem.Header = MakeHeader(sourceGroup.Key.ToString(), hasSelection);
        return sourceItem;
    }

    private static TextBlock MakeHeader(string text, bool hasSelection)
    {
        var header = new TextBlock
        {
            Text = text,
            FontWeight = hasSelection ? FontWeight.SemiBold : FontWeight.Normal
        };
        if (hasSelection) header.Foreground = Brushes.CornflowerBlue;
        return header;
    }
}
