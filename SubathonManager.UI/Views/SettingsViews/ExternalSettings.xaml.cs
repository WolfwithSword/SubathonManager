using System.Windows.Controls;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.UI.Views.SettingsViews.External;

namespace SubathonManager.UI.Views.SettingsViews;

public partial class ExternalSettings : SettingsGroupControl
{
    protected override IEnumerable<SubathonEventSource> _eventSources =>
        Enum.GetValues<SubathonEventSource>()
            .Where(s => s.GetGroup() == SubathonSourceGroup.ExternalService && s != SubathonEventSource.KoFiTunnel)
            .OrderBy(g => g.GetGroupLabelOrder());

    protected override StackPanel? GetSourceContents => SourceContents;
    protected override StackPanel? GetSourceList => SourceList;

    public ExternalSettings()
    {
        InitializeComponent();
        SettingsEvents.HotLinkToDevTunnelsRequested += HotLinkToDevTunnels;
    }

    protected override SettingsControl? GetSettingsControl(SubathonEventSource eventSource)
    {
        if (_settingsControls.TryGetValue(eventSource, out var control)) return control;

        switch (eventSource)
        {
            case SubathonEventSource.KoFi:
                _settingsControls[eventSource] = new KoFiCombinedSettings();
                break;
            case SubathonEventSource.KoFiTunnel:
                // merged into KoFi tab — return the shared instance without registering again
                return _settingsControls.TryGetValue(SubathonEventSource.KoFi, out var kofi) ? kofi : null;
            case SubathonEventSource.DevTunnels:
                _settingsControls[eventSource] = new DevTunnelsSettings();
                break;
            case SubathonEventSource.GoAffPro:
                _settingsControls[eventSource] = new GoAffProSettings();
                break;
            case SubathonEventSource.External:
                _settingsControls[eventSource] = new ExternalServiceSettings();
                break;
            case SubathonEventSource.FourthWall:
                _settingsControls[eventSource] = new FourthWallSettings();
                break;
            default: return null;
        }
        _settingsControls[eventSource].Init(Host);
        return _settingsControls[eventSource];
    }
    
    public void RefreshTierCombo(SubathonEventSource source)
    {
        if (source is not (SubathonEventSource.KoFi or SubathonEventSource.External or SubathonEventSource.FourthWall)) return;
        _settingsControls.TryGetValue(source, out var control);
        // hc
        if (source == SubathonEventSource.KoFi)
            ((KoFiCombinedSettings?) control)?.RefreshTierCombo();
        if (source == SubathonEventSource.External)
            ((ExternalServiceSettings?) control)?.RefreshTierCombo();
        // if (source == SubathonEventSource.FourthWall)
        //     ((FourthWallSettings?) control)?.RefreshTierCombo();
    }
    
    private void HotLinkToDevTunnels()
    {
        TryHotLinkToSource(SubathonEventSource.DevTunnels);
    }
    
}