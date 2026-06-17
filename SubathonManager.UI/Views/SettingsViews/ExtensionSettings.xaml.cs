using System.Windows.Controls;
using SubathonManager.Core.Enums;
using SubathonManager.UI.Views.SettingsViews.Extensions;

namespace SubathonManager.UI.Views.SettingsViews;

public partial class ExtensionSettings : SettingsGroupControl
{
    protected override IEnumerable<SubathonEventSource> _eventSources =>
        Enum.GetValues<SubathonEventSource>().Where(s => s.GetGroup() == SubathonSourceGroup.StreamExtension)
            .OrderBy(g => g.GetGroupLabelOrder());

    protected override StackPanel? GetSourceContents => SourceContents;
    protected override StackPanel? GetSourceList => SourceList;

    public ExtensionSettings()
    {
        InitializeComponent();
    }

    protected override SettingsControl? GetSettingsControl(SubathonEventSource eventSource)
    {
        if (_settingsControls.TryGetValue(eventSource, out var control)) return control;

        switch (eventSource)
        {
            case SubathonEventSource.Blerp:
                _settingsControls[eventSource] = new ChatExtensionSettings();
                break;
            case SubathonEventSource.StreamElements:
                _settingsControls[eventSource] = new StreamElementsSettings();
                break;
            case SubathonEventSource.StreamLabs:
                _settingsControls[eventSource] = new StreamLabsSettings();
                break;
            default: return null;
        }
        _settingsControls[eventSource].Init(Host);
        return _settingsControls[eventSource];
    }
    
    public bool SaveConfigValues()
    {
        // blerp only atm
        if (GetSettingsControl(SubathonEventSource.Blerp) is not ChatExtensionSettings control) return false;
        return control.SaveConfigValues();
    }

    public void LoadConfigValues()
    {
        // blerp only atm
        if (GetSettingsControl(SubathonEventSource.Blerp) is not ChatExtensionSettings control) return ;
        control.LoadConfigValues();
    }
    
}