using System.Windows.Controls;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Data;
using SubathonManager.Integration;

namespace SubathonManager.UI.Views.SettingsViews;

public partial class ChatExtensionSettings : UserControl
{
    public required SettingsView Host { get; set; }
    //private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<ChatExtensionSettings>>();
    
    public ChatExtensionSettings()
    {
        InitializeComponent();
    }
    
    public void Init(SettingsView host)
    {
        Host = host;
        LoadConfigValues();
    }
    
    public bool UpdateValueSettings(AppDbContext db)
    {

        bool hasUpdated = false;
        var blerpBitsValue =
            db.SubathonValues.FirstOrDefault(sv =>
                sv.EventType == SubathonEventType.BlerpBits && sv.Meta == "");
        if (blerpBitsValue != null && double.TryParse(BitsTextBox.Text, out var bitsSeconds) &&
            !bitsSeconds.Equals(blerpBitsValue.Seconds / 100.0))
        {
            blerpBitsValue.Seconds = bitsSeconds / 100.0;
            hasUpdated = true;
        }

        if (blerpBitsValue != null && double.TryParse(Bits2TextBox.Text, out var bitsPoints) &&
            !bitsPoints.Equals(blerpBitsValue.Points))
        {
            blerpBitsValue.Points = bitsPoints;
            hasUpdated = true;
        }
        
        var blerpBeetsValue =
            db.SubathonValues.FirstOrDefault(sv =>
                sv.EventType == SubathonEventType.BlerpBits && sv.Meta == "");
        if (blerpBeetsValue != null && double.TryParse(BitsTextBox.Text, out var beetsSeconds) &&
            !beetsSeconds.Equals(blerpBeetsValue.Seconds / 100.0))
        {
            blerpBeetsValue.Seconds = beetsSeconds / 100.0;
            hasUpdated = true;
        }

        if (blerpBeetsValue != null && double.TryParse(Bits2TextBox.Text, out var beetsPoints) &&
            !beetsPoints.Equals(blerpBeetsValue.Points))
        {
            blerpBeetsValue.Points = beetsPoints;
            hasUpdated = true;
        }
        
        return hasUpdated;
    }

    public void SaveConfigValues()
    {
        if (double.TryParse(BitsModifierTextBox.Text, out var blerpBitsMod))
            App.AppConfig!.Set("Extensions", "BlerpBits.Modifier", $"{blerpBitsMod}");
        
        if (double.TryParse(BeetsModifierTextBox.Text, out var blerpBeetsMod))
            App.AppConfig!.Set("Extensions", "BlerpBeets.Modifier", $"{blerpBeetsMod}");
    }

    public void LoadConfigValues()
    {
        double.TryParse(App.AppConfig!.Get("Extensions", "BlerpBits.Modifier", "1"), out var blerpBitsMod);
        BitsModifierTextBox.Text = $"{blerpBitsMod}";
        double.TryParse(App.AppConfig!.Get("Extensions", "BlerpBeets.Modifier", "1"), out var blerpBeetsMod);
        BeetsModifierTextBox.Text = $"{blerpBeetsMod}";
    }
    
    private void TestBlerp_Click(object sender, RoutedEventArgs e)
    {
        if (!long.TryParse(SimulateBlerpAmt.Text, out var amount))
            return;
        var currency = BlerpCurrencyBox.Text;
        BlerpChatService.SimulateBlerpMessage(amount, currency);
    }
}