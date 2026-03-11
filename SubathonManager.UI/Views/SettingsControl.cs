using System.Windows.Controls;
using SubathonManager.Core.Enums;
using SubathonManager.Data;

namespace SubathonManager.UI.Views;

public abstract class SettingsControl : UserControl
{
#pragma warning disable CS8618 
    protected SettingsView Host;
#pragma warning restore CS8618 

    public virtual void Init(SettingsView host)
    {
        Host = host;
    }
    internal abstract void UpdateStatus(bool status, SubathonEventSource source, string name, string service);
    public abstract void LoadValues(AppDbContext db);
    public abstract bool UpdateValueSettings(AppDbContext db);
    public abstract bool UpdateConfigValueSettings();
}