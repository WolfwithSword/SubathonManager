using System.ComponentModel;
using SubathonManager.Core.Enums;

namespace SubathonManager.UI.Controls;

public sealed class FilterOption : INotifyPropertyChanged {
    private bool _matchesSearch = true;
    private bool _selected;

    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Group { get; init; } = "";

    public static List<FilterOption> EventTypes(bool includeCommands = false) {
        return Enum.GetValues<SubathonEventType>()
            .Where(t => t != SubathonEventType.Unknown && (includeCommands || t != SubathonEventType.Command))
            .OrderBy(t => SubathonEventSourceHelper.GetSourceOrder(((SubathonEventType?)t).GetSource()))
            .ThenBy(t => ((SubathonEventType?)t).GetLabel(), StringComparer.OrdinalIgnoreCase)
            .Select(t => new FilterOption {
                Label = ((SubathonEventType?)t).GetLabel(),
                Value = t.ToString(),
                Group = ((SubathonEventType?)t).GetSource().ToString()
            })
            .ToList();
    }

    public bool Selected {
        get => _selected;
        set {
            if (_selected == value) return;
            _selected = value;
            Raise(nameof(Selected));
        }
    }

    public bool MatchesSearch {
        get => _matchesSearch;
        set {
            if (_matchesSearch == value) return;
            _matchesSearch = value;
            Raise(nameof(MatchesSearch));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class FilterGroup : INotifyPropertyChanged {
    private bool _hasMatches = true;
    private bool _isExpanded;

    public string Header { get; init; } = "";
    public List<FilterOption> Items { get; init; } = [];

    public bool IsExpanded {
        get => _isExpanded;
        set {
            if (_isExpanded == value) return;
            _isExpanded = value;
            Raise(nameof(IsExpanded));
        }
    }

    public bool HasMatches {
        get => _hasMatches;
        set {
            if (_hasMatches == value) return;
            _hasMatches = value;
            Raise(nameof(HasMatches));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
