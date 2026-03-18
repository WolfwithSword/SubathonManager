using System.Windows;
using System.Text.Json;
using SubathonManager.Integration;
using SubathonManager.Core.Enums;
using SubathonManager.Data;

namespace SubathonManager.UI.Views.SettingsViews;

public partial class ExternalServiceSettings : SettingsControl
{
    public ExternalServiceSettings()
    {
        InitializeComponent();
    }

    public override void Init(SettingsView host)
    {
        Host = host;
    }

    internal override void UpdateStatus(bool status, SubathonEventSource source, string name, string service)
    {
        throw new NotImplementedException();
    }

    public override void LoadValues(AppDbContext db)
    {
        throw new NotImplementedException();
    }

    public override bool UpdateValueSettings(AppDbContext db)
    {
        bool hasUpdated = false;
        var externalDonoValue = db.SubathonValues.FirstOrDefault(sv =>
            sv.EventType == SubathonEventType.ExternalDonation
            && sv.Meta == "");
        if (externalDonoValue != null && double.TryParse(DonoBox.Text, out var exSeconds) &&
            !exSeconds.Equals(externalDonoValue.Seconds))
        {
            externalDonoValue.Seconds = exSeconds;
            hasUpdated = true;
        }

        if (externalDonoValue != null && double.TryParse(DonoBox2.Text, out var exPoints)
            && !exPoints.Equals(externalDonoValue.Points))
        {
            externalDonoValue.Points = exPoints;
            hasUpdated = true;
        }

        return hasUpdated;
    }

    public override bool UpdateConfigValueSettings()
    {
        throw new NotImplementedException();
    }

    private void TestExternalDonation_Click(object sender, RoutedEventArgs e)
    {
        var value = SimulateExternalAmt.Text;
        var currency = CurrencyBox.Text;
        Dictionary<string, JsonElement> data = new Dictionary<string, JsonElement>();
        data.Add("type", JsonSerializer.SerializeToElement($"{SubathonEventType.ExternalDonation}"));
        data.Add("user", JsonSerializer.SerializeToElement("SYSTEM"));
        data.Add("currency", JsonSerializer.SerializeToElement(currency));
        data.Add("amount", JsonSerializer.SerializeToElement(value));
        ExternalEventService.ProcessExternalDonation(data);
    }
}