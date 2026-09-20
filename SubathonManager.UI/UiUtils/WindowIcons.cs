using Avalonia.Controls;
using Avalonia.Platform;

namespace SubathonManager.UI.UiUtils;

public static class WindowIcons {
    private const string IconUri = "avares://SubathonManager/Assets/icon_128.png";

    private static WindowIcon? _icon;
    private static bool _loaded;

    public static void Apply(Window window) {
        if (OperatingSystem.IsWindows()) return;
        if (Load() is { } icon) window.Icon = icon;
    }

    private static WindowIcon? Load() {
        if (_loaded) return _icon;
        _loaded = true;
        try {
            using Stream stream = AssetLoader.Open(new Uri(IconUri));
            _icon = new WindowIcon(stream);
        }
        catch {
            _icon = null;
        }

        return _icon;
    }
}