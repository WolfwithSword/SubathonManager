using System.Windows.Controls;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.UI.Views.SettingsViews.External;
using SubathonManager.UI.Views.SettingsViews.ExternalSoftware;

namespace SubathonManager.UI.Views.SettingsViews;

public partial class ExternalSoftwareSettings : SettingsGroupControl
{
    protected override IEnumerable<SubathonEventSource> _eventSources =>
        Enum.GetValues<SubathonEventSource>().Where(s => s.GetGroup() == SubathonSourceGroup.ExternalSoftware)
            .OrderBy(g => g.GetGroupLabelOrder());

    protected override StackPanel? GetSourceContents => SourceContents;
    protected override Panel? GetSourceList => SourceList;

    public ExternalSoftwareSettings()
    {
        InitializeComponent();
        SettingsEvents.HotLinkToDevTunnelsRequested += HotLinkToDevTunnels;
    }

    private void HotLinkToDevTunnels()
    {
        TryHotLinkToSource(SubathonEventSource.DevTunnels);
        Dispatcher.BeginInvoke(() => BringIntoView());
    }

    protected override SettingsControl? GetSettingsControl(SubathonEventSource eventSource)
    {
        if (_settingsControls.TryGetValue(eventSource, out var control)) return control;

        switch (eventSource)
        {
            case SubathonEventSource.OBS:
                _settingsControls[eventSource] = new ObsSettings();
                break;
            case SubathonEventSource.DevTunnels:
                _settingsControls[eventSource] = new DevTunnelsSettings();
                break;
            case SubathonEventSource.StreamDeck:
                _settingsControls[eventSource] = new StreamDeckSettings();
                break;
            case SubathonEventSource.StreamerBot:
                _settingsControls[eventSource] = new StreamerBotSettings();
                break;
            default: return null;
        }
        _settingsControls[eventSource].Init(Host);
        return _settingsControls[eventSource];
    }
}
