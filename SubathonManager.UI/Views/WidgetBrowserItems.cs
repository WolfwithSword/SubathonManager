using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Data.Widgets;

namespace SubathonManager.UI.Views;

public sealed class CatalogItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public const string PresetSection = "Presets";
    public const string UnknownAuthor = "Unknown Author";
    public const string UngroupedName = "Ungrouped";

    public required WidgetCatalogEntry Entry { get; init; }

    public string Name => string.IsNullOrWhiteSpace(Entry.Name) ? "Untitled Widget" : Entry.Name;
    public string AuthorLabel => string.IsNullOrWhiteSpace(Entry.Author) ? "Unknown author" : Entry.Author;
    public string VersionLabel => "v" + WidgetPackPaths.DisplayVersion(Entry.Version);

    public Bitmap? Preview { get; init; }
    public bool HasPreview => Preview != null;

    public bool CanAdd { get; init; } = true;

    public string AddHint => CanAdd
        ? "Add this widget to the overlay being edited"
        : "Open an overlay for editing to add widgets to it";

    public bool CanDelete => Entry.Source != WidgetCatalogSource.Preset;

    public string? DocsUrl
        => !string.IsNullOrWhiteSpace(Entry.DocsUrl) &&
           Uri.TryCreate(Entry.DocsUrl, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? Entry.DocsUrl
            : null;

    public bool HasDocs => DocsUrl != null;

    public string DeleteHint => CanDelete
        ? "Delete this package file from disk"
        : "Widgets included with the app cannot be deleted";

    public string TooltipText
    {
        get
        {
            var lines = new List<string> { Name, $"{AuthorLabel} - {VersionLabel}" };
            if (!string.IsNullOrWhiteSpace(Entry.Tags)) lines.Add(Entry.Tags);
            lines.Add(Entry.PackPath);
            return string.Join('\n', lines);
        }
    }

    public IReadOnlyList<string> TagList { get; init; } = [];

    public bool HasTags => TagList.Count > 0;

    public static IReadOnlyList<string> SplitTags(string? tags)
        => string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public string SearchBlob { get; init; } = string.Empty;

    public string SectionTitle => Entry.Source == WidgetCatalogSource.Preset
        ? PresetSection
        : string.IsNullOrWhiteSpace(Entry.Author) ? UnknownAuthor : Entry.Author.Trim();

    public string GroupTitle =>
        string.IsNullOrWhiteSpace(Entry.Group) ||
        string.Equals(Entry.Group, WidgetPackPaths.DefaultGroup, StringComparison.OrdinalIgnoreCase)
            ? UngroupedName
            : Entry.Group.Trim();

    public string VersionKey => string.IsNullOrWhiteSpace(Entry.PackId)
        ? Entry.PackPath
        : $"{Entry.Source}|{Entry.PackId}";
}

public sealed class CatalogGroup
{
    public required string Title { get; init; }

    public required string Key { get; init; }
    public bool IsExpanded { get; set; } = true;
    public string CountLabel { get; set; } = string.Empty;

    public ObservableCollection<CatalogItem> Items { get; } = [];
}

public sealed class CatalogSection
{
    public required string Title { get; init; }

    public required string Key { get; init; }
    public bool IsExpanded { get; set; } = true;
    public ObservableCollection<CatalogGroup> Groups { get; } = [];
    public string CountLabel { get; set; } = string.Empty;
}
