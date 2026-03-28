using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;

namespace SubathonManager.UI.Views;

public abstract class SettingsControl : UserControl
{
#pragma warning disable CS8618
    protected SettingsView Host;
#pragma warning restore CS8618

    private int _suppressCount = 0;

    public virtual void Init(SettingsView host)
    {
        Host = host;
    }

    protected void SuppressUnsavedChanges(Action action)
    {
        _suppressCount++;
        try { action(); }
        finally { _suppressCount--; }
    }

    protected void RegisterUnsavedChangeHandlers()
    {
        Dispatcher.InvokeAsync(() => WireInputs(this), DispatcherPriority.Loaded);
    }

    protected void WireControl(DependencyObject control)
    {
        AttachHandler(control);
        WireInputs(control);
    }

    private void WireInputs(DependencyObject parent)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);

            if (child is { } dep &&
                SettingsProperties.GetExcludeFromUnsaved(dep))
                continue;

            switch (child)
            {
                case Expander expander:
                    WireExpander(expander);
                    continue;

                default:
                    AttachHandler(child);
                    break;
            }
            WireInputs(child);
        }
    }

    private void AttachHandler(DependencyObject element)
    {
        switch (element)
        {
            case TextBox tb:
                tb.TextChanged += OnInputChanged;
                break;
            case PasswordBox pb:
                pb.PasswordChanged += OnInputChanged;
                break;
            case ComboBox cb:
                cb.SelectionChanged += OnInputChanged;
                break;
            case CheckBox chk:
                chk.Checked += OnInputChanged;
                chk.Unchecked += OnInputChanged;
                break;
            case Slider sld:
                sld.ValueChanged += OnInputChanged;
                break;
        }
    }

    private void WireExpander(Expander expander)
    {
        bool firstExpand = true;

        expander.Expanded += (_, _) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                // ReSharper disable once AccessToModifiedClosure
                if (firstExpand)
                {
                    firstExpand = false;
                    SuppressUnsavedChanges(() => WireInputs(expander));
                }
            }, DispatcherPriority.Loaded);
        };

        if (expander.IsExpanded)
        {
            firstExpand = false;
            WireInputs(expander);
        }
    }

    private void OnInputChanged(object sender, EventArgs e)
    {
        if (_suppressCount > 0) return;
        SettingsEvents.RaiseSettingsUnsavedChanges(true);
    }

    internal abstract void UpdateStatus(IntegrationConnection? connection);

    protected internal virtual void LoadValues(AppDbContext db)
    {
        return;
    }
    public abstract bool UpdateValueSettings(AppDbContext db);

    protected internal virtual bool UpdateConfigValueSettings()
    {
        return false;
    }
    public abstract void UpdateCurrencyBoxes(List<string> currencies, string selected);

    public abstract (string seconds, string points, TextBox? timeBox, TextBox? pointsBox) GetValueBoxes(SubathonValue val);
}