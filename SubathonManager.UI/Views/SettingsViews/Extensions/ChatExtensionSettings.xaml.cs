using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Integration;

namespace SubathonManager.UI.Views.SettingsViews.Extensions;

public partial class ChatExtensionSettings : SettingsControl
{
    public ChatExtensionSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RegisterUnsavedChangeHandlers();
        };
    }
    
    public override void Init(SettingsView host)
    {
        Host = host;
        LoadConfigValues();
    }

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        throw new NotImplementedException();
    }

    public override bool UpdateValueSettings(AppDbContext db)
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

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected)
    {
        return;
    }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val)
    {
        string v = $"{val.Seconds}";
        string p = $"{val.Points}";
        TextBox? box = null;
        TextBox? box2 = null;
        switch (val.EventType)
        {
            case SubathonEventType.BlerpBits:
                v = $"{Math.Round(val.Seconds * 100)}";
                box = BitsTextBox;
                box2 = Bits2TextBox; 
                break;
            case SubathonEventType.BlerpBeets:
                v = $"{Math.Round(val.Seconds * 100)}";
                box = BeetsTextBox;
                box2 = Beets2TextBox; 
                break;
        }
        return (v, p, box, box2);
    }

    public bool SaveConfigValues()
    {
        bool hasUpdated = false;
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        if (double.TryParse(BitsModifierTextBox.Text, out var blerpBitsMod))
        {
            hasUpdated |= config.Set("Extensions", "BlerpBits.Modifier", $"{blerpBitsMod}");
        }

        if (double.TryParse(BeetsModifierTextBox.Text, out var blerpBeetsMod))
        {
            hasUpdated |= config.Set("Extensions", "BlerpBeets.Modifier", $"{blerpBeetsMod}");
        }
        return hasUpdated;
    }

    public void LoadConfigValues()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        double.TryParse(config.Get("Extensions", "BlerpBits.Modifier", "1"), out var blerpBitsMod);
        BitsModifierTextBox.Text = $"{blerpBitsMod}";
        double.TryParse(config.Get("Extensions", "BlerpBeets.Modifier", "1"), out var blerpBeetsMod);
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