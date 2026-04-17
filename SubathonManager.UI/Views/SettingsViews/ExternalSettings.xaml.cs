using System.Windows.Controls;
using SubathonManager.Core.Enums;
using SubathonManager.UI.Views.SettingsViews.External;

namespace SubathonManager.UI.Views.SettingsViews;

public partial class ExternalSettings : SettingsGroupControl
{
    protected override IEnumerable<SubathonEventSource> _eventSources =>
        Enum.GetValues<SubathonEventSource>().Where(s => s.GetGroup() == SubathonSourceGroup.ExternalService)
            .OrderBy(g => g.GetGroupLabelOrder());

    protected override StackPanel? GetSourceContents => SourceContents;
    protected override StackPanel? GetSourceList => SourceList;

    public ExternalSettings()
    {
        InitializeComponent();
    }

    protected override SettingsControl? GetSettingsControl(SubathonEventSource eventSource)
    {
        if (_settingsControls.TryGetValue(eventSource, out var control)) return control;

        switch (eventSource)
        {
            case SubathonEventSource.KoFi:
                _settingsControls[eventSource] = new KoFiSettings();
                break;
            case SubathonEventSource.KoFiWebhook:
                _settingsControls[eventSource] = new KoFiWebhookSettings();
                break;
            case SubathonEventSource.DevTunnels:
                _settingsControls[eventSource] = new DevTunnelsSettings();
                break;
            case SubathonEventSource.GoAffPro:
                _settingsControls[eventSource] = new GoAffProSettings();
                break;
            case SubathonEventSource.External:
                _settingsControls[eventSource] = new ExternalServiceSettings();
                break;
            default: return null;
        }
        _settingsControls[eventSource].Init(Host);
        return _settingsControls[eventSource];
    }
    
    public void RefreshTierCombo(SubathonEventSource source)
    {
        if (source is not (SubathonEventSource.KoFi or SubathonEventSource.External)) return;
        _settingsControls.TryGetValue(source, out var control);
        // hc
        if (source == SubathonEventSource.KoFi)
            ((KoFiSettings?) control)?.RefreshTierCombo();
        if (source == SubathonEventSource.External)
            ((ExternalServiceSettings?) control)?.RefreshTierCombo();
    }
    
}