using System.Windows.Controls;
using System.Windows;
using System.Text.Json;
using SubathonManager.Integration;
using SubathonManager.Core.Enums;
using SubathonManager.Data;

namespace SubathonManager.UI.Views.SettingsViews;

public partial class ExternalServiceSettings : UserControl
{
    public required SettingsView Host { get; set; }
    public ExternalServiceSettings()
    {
        InitializeComponent();
    }

    public void Init(SettingsView host)
    {
        Host = host;
    }
    public bool UpdateValueSettings(AppDbContext db)
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