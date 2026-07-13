using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SubathonManager.Core.Enums;

namespace SubathonManager.UI.UiUtils;

public sealed record EventTypeMenuEntry(
    SubathonEventSource Source,
    string Label,
    bool IsSelected,
    Action OnSelected);

public static class EventTypeMenu
{
    public static void Show(FrameworkElement placementTarget, IReadOnlyList<EventTypeMenuEntry> entries,
        bool groupBySourceType = true, string? clearLabel = null, Action? onClear = null)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = placementTarget,
            Placement = PlacementMode.Bottom,
            MinWidth = placementTarget.ActualWidth
        };

        var searchBox = new Wpf.Ui.Controls.TextBox
        {
            PlaceholderText = "Search...",
            ClearButtonEnabled = false,
            MinWidth = 170,
            Height = 34,
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var searchItem = new MenuItem
        {
            Header = searchBox,
            StaysOpenOnClick = true,
            Focusable = false
        };
        menu.Items.Add(searchItem);

        int fixedItemCount = 1;
        if (onClear != null)
        {
            var clearItem = new MenuItem
            {
                Header = new TextBlock { Text = clearLabel ?? "(none)", FontStyle = FontStyles.Italic }
            };
            clearItem.Click += (_, _) => onClear();
            menu.Items.Add(clearItem);
            fixedItemCount = 2;
        }

        var nestedItems = BuildNested(entries, groupBySourceType);
        foreach (var item in nestedItems) menu.Items.Add(item);

        List<EventTypeMenuEntry> currentMatches = [];

        searchBox.TextChanged += (_, _) =>
        {
            while (menu.Items.Count > fixedItemCount) menu.Items.RemoveAt(fixedItemCount);
            var query = searchBox.Text.Trim();

            if (query.Length == 0)
            {
                currentMatches.Clear();
                foreach (var item in nestedItems) menu.Items.Add(item);
                return;
            }

            currentMatches = entries.Where(en => Matches(en, query, groupBySourceType)).ToList();
            foreach (var entry in currentMatches)
            {
                var flat = new MenuItem
                {
                    Header = $"{entry.Source} - {entry.Label}",
                    IsChecked = entry.IsSelected
                };
                var captured = entry;
                flat.Click += (_, _) => captured.OnSelected();
                menu.Items.Add(flat);
            }
        };

        searchBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || currentMatches.Count == 0) return;
            e.Handled = true;
            menu.IsOpen = false;
            currentMatches[0].OnSelected();
        };

        menu.Opened += (_, _) => searchBox.Focus();
        menu.IsOpen = true;
    }

    private static bool Matches(EventTypeMenuEntry entry, string query, bool groupBySourceType)
    {
        var haystack = groupBySourceType
            ? $"{entry.Source.GetGroup().GetLabel()} {entry.Source} {entry.Label}"
            : $"{entry.Source} {entry.Label}";
        return haystack.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static List<MenuItem> BuildNested(IReadOnlyList<EventTypeMenuEntry> entries, bool groupBySourceType)
    {
        if (!groupBySourceType)
        {
            var sourceItems = new List<MenuItem>();
            foreach (var sourceGroup in entries.GroupBy(en => en.Source)
                         .OrderBy(g => SubathonEventSourceHelper.GetSourceOrder(g.Key)))
            {
                var sourceItem = BuildSourceItem(sourceGroup, out _);
                AttachHoverExpand(sourceItem, () => sourceItems);
                sourceItems.Add(sourceItem);
            }
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
                AttachHoverExpand(sourceItem, () => groupItem.Items.OfType<MenuItem>());
                groupItem.Items.Add(sourceItem);
            }

            groupItem.Header = MakeHeader(group.Key.GetLabel(), groupHasSelection);
            AttachHoverExpand(groupItem, () => groupItems);
            groupItems.Add(groupItem);
        }

        return groupItems;
    }

    private static MenuItem BuildSourceItem(IGrouping<SubathonEventSource, EventTypeMenuEntry> sourceGroup,
        out bool hasSelection)
    {
        var sourceItem = new MenuItem();
        hasSelection = false;

        foreach (var entry in sourceGroup)
        {
            var leaf = new MenuItem { Header = entry.Label, IsChecked = entry.IsSelected };
            hasSelection |= entry.IsSelected;
            var captured = entry;
            leaf.Click += (_, _) => captured.OnSelected();
            sourceItem.Items.Add(leaf);
        }

        sourceItem.Header = MakeHeader(sourceGroup.Key.ToString(), hasSelection);
        return sourceItem;
    }

    private static TextBlock MakeHeader(string text, bool hasSelection)
    {
        var header = new TextBlock
        {
            Text = text,
            FontWeight = hasSelection ? FontWeights.SemiBold : FontWeights.Normal
        };
        if (hasSelection) header.Foreground = Brushes.CornflowerBlue;
        return header;
    }

    private static void AttachHoverExpand(MenuItem item, Func<IEnumerable<MenuItem>> siblings)
    {
        item.MouseEnter += (_, _) =>
        {
            foreach (var sibling in siblings())
                sibling.IsSubmenuOpen = ReferenceEquals(sibling, item);
        };
    }
}
