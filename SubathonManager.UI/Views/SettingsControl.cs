using System.Windows.Controls;
using SubathonManager.Core.Enums;
using SubathonManager.Data;

namespace SubathonManager.UI.Views;

public abstract class SettingsControl : UserControl
{
    protected SettingsView Host;

    public virtual void Init(SettingsView host)
    {
        Host = host;
    }
    internal abstract void UpdateStatus(bool status, SubathonEventSource source, string name, string service);
    public abstract void LoadValues(AppDbContext db);
    public abstract bool UpdateValueSettings(AppDbContext db);
    public abstract bool UpdateConfigValueSettings();
}