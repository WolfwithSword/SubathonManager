using System.Windows;
using System.Windows.Controls;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace SubathonManager.UI.Views.SettingsViews;

public class SettingsGroupControl : SettingsControl
{
    protected virtual IEnumerable<SubathonEventSource> _eventSources => [];
    private SubathonEventSource _activeSource = SubathonEventSource.Unknown;
    
    internal readonly Dictionary<SubathonEventSource, SettingsControl> _settingsControls = new();
    protected virtual StackPanel? GetSourceContents => null;
    protected virtual StackPanel? GetSourceList => null;

    protected virtual SettingsControl? GetSettingsControl(SubathonEventSource eventSource)
    {
        if (_settingsControls.TryGetValue(eventSource, out var control)) return control;
        return null;
    }
    
    public override void Init(SettingsView host)
    {
        Host = host;
        GetSourceList?.Children.Clear();

        foreach (var source in _eventSources)
        {
            var navBtn = new Wpf.Ui.Controls.Button
            {
                Content = source.GetDescription(),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 1, 0, -12),
                Padding = new Thickness(10, 6, 10, 6),
                Appearance = ControlAppearance.Transparent,
                FontSize = 20,
                MinWidth = 100,
                Tag = $"{source}",
                BorderThickness = new Thickness(1, 1, 1, 2),
                CornerRadius = new CornerRadius(4, 4, 0, 0)
            };
            navBtn.Click += GroupNav_Click;
            GetSourceList?.Children.Add(navBtn);
            
            var control = GetSettingsControl(source);
            
            if (control != null && _activeSource == SubathonEventSource.Unknown)
            {
                Dispatcher.Invoke(() =>
                {
                    SelectGroup(source.ToString());
                });
            }
        }
    }
    
    private void GroupNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.Button { Tag: string label })
            SelectGroup(label);
    }
    
    private void SelectGroup(string label)
    {
        if (GetSourceList == null) return;
        foreach (var child in GetSourceList.Children)
        {
            if (child is not Wpf.Ui.Controls.Button btn) continue;
            
            btn.Appearance = btn.Tag as string == label
                ? ControlAppearance.Secondary
                : ControlAppearance.Transparent;
        }

        if (!Enum.TryParse(label, out SubathonEventSource source)) return;
        if (source == _activeSource) return;
        
        var control = GetSettingsControl(source);
        if (control == null) return;
            
        GetSourceContents?.Children.Clear();
        GetSourceContents?.Children.Add(control);
        _activeSource = source;
    }


    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        throw new NotImplementedException();
    }

    protected internal override void LoadValues(AppDbContext db)
    {
        foreach (var controlPair in _settingsControls)
        {
            controlPair.Value.LoadValues(db);
        }
    }

    public override bool UpdateValueSettings(AppDbContext db)
    {
        bool hasUpdated = false;
        foreach (var controlPair in _settingsControls)
        {
            hasUpdated |= controlPair.Value.UpdateValueSettings(db);
        }
        return hasUpdated;
    }

    protected internal override bool UpdateConfigValueSettings()
    { 
        bool hasUpdated = false;
        foreach (var controlPair in _settingsControls)
        {
            hasUpdated |= controlPair.Value.UpdateConfigValueSettings();
        }
        return hasUpdated;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string text)
    {
        foreach (var controlPair in _settingsControls)
        {
            controlPair.Value.UpdateCurrencyBoxes(currencies, text);
        }
    }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val)
    {
        var source = val.EventType.GetSource();
        var control = GetSettingsControl(source);
        return control?.GetValueBoxes(val) ?? ("", "", null, null);
    }
}