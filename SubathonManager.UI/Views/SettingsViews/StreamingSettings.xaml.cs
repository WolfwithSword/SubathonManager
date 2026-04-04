using System.Windows.Controls;
using SubathonManager.Core.Enums;
using SubathonManager.UI.Views.SettingsViews.Streaming;

namespace SubathonManager.UI.Views.SettingsViews;

public partial class StreamingSettings : SettingsGroupControl
{
    protected override IEnumerable<SubathonEventSource> _eventSources =>
        Enum.GetValues<SubathonEventSource>().Where(s => s.GetGroup() == SubathonSourceGroup.Stream)
            .OrderBy(g => g.GetGroupLabelOrder());

    protected override StackPanel? GetSourceContents => SourceContents;
    protected override StackPanel? GetSourceList => SourceList;
    
    public StreamingSettings()
    {
        InitializeComponent();
    }

    protected override SettingsControl? GetSettingsControl(SubathonEventSource eventSource)
    {
        if (_settingsControls.TryGetValue(eventSource, out var control)) return control;

        switch (eventSource)
        {
            case SubathonEventSource.Twitch:
                _settingsControls[eventSource] = new TwitchSettings();
                break;
            case SubathonEventSource.YouTube:
                _settingsControls[eventSource] = new YouTubeSettings();
                break;
            case SubathonEventSource.Picarto:
                _settingsControls[eventSource] = new PicartoSettings();
                break;
            default: return null;
        }
        _settingsControls[eventSource].Init(Host);
        return _settingsControls[eventSource];
    }
    
    public void RefreshTierCombo(SubathonEventSource source)
    {
        if (source != SubathonEventSource.YouTube) return;
        _settingsControls.TryGetValue(source, out var control);
        // hc
        ((YouTubeSettings?) control)?.RefreshTierCombo();
    }
}