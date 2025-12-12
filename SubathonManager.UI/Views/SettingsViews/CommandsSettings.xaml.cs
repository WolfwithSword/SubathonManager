using System.Windows.Controls;
using System.Windows;
using SubathonManager.Core.Enums;

namespace SubathonManager.UI.Views.SettingsViews;

public partial class CommandsSettings : UserControl
{
    public required SettingsView Host { get; set; }
    public CommandsSettings()
    {
        InitializeComponent();
    }

    public void Init(SettingsView host)
    {
        Host = host;
        InitCommandSettings();
    }
    
    private void InitCommandSettings()
    {
        foreach (SubathonCommandType commandType in Enum.GetValues(typeof(SubathonCommandType)))
        {
            if (commandType == SubathonCommandType.None || commandType == SubathonCommandType.Unknown) continue;
            // 200 | 30 blank | 200 | 120 | 120 | remain
            // enum / blank / name / mods / vips / whitelist 
            bool.TryParse(App.AppConfig!.Get("Twitch", $"Commands.{commandType}.permissions.Mods", "false")!, out var checkMods);
            bool.TryParse(App.AppConfig!.Get("Twitch",$"Commands.{commandType}.permissions.VIPs", "false")!, out var checkVips);
            string name = App.AppConfig!.Get("Twitch",$"Commands.{commandType}.name", commandType.ToString().ToLower())!;
            string whitelist = App.AppConfig!.Get("Twitch", $"Commands.{commandType}.permissions.Whitelist", string.Empty)!;

            StackPanel entryPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Height = 40
            };
            
            TextBlock enumType = new TextBlock
            {
                Text = commandType.ToString(),
                Width = 200,
                Margin = new Thickness(0, 0, 30, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            TextBox enumName = new TextBox
            {
                Text = name,
                Width = 200,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            CheckBox doMods = new CheckBox
            {
                IsChecked = checkMods,
                Width = 120,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Allow Mods",
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            CheckBox doVips = new CheckBox
            {
                IsChecked = checkVips,
                Width = 120,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Allow VIPs",
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            TextBox enumWhitelist = new TextBox
            {
                Text = whitelist,
                Width = 456,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            entryPanel.Children.Add(enumType);
            entryPanel.Children.Add(enumName);
            entryPanel.Children.Add(doMods);
            entryPanel.Children.Add(doVips);
            entryPanel.Children.Add(enumWhitelist);

            CommandListPanel.Children.Add(entryPanel);
        }   
    }
    
    public void UpdateValueSettings()
    {
        foreach (var child in CommandListPanel.Children)
        {
            if (child is StackPanel entry)

                if (entry.Children[0] is TextBlock enumType)
                {
                    string key = $"Commands.{enumType.Text}";
                        
                    if (entry.Children[1] is TextBox enumName &&
                        App.AppConfig!.Get("Twitch", $"{key}.name") != enumName.Text.Trim())
                    {
                        App.AppConfig!.Set("Twitch", $"{key}.name", enumName.Text.Trim());
                    }
                    
                    if (entry.Children[2] is CheckBox doMods &&
                        App.AppConfig!.Get("Twitch", $"{key}.permissions.Mods") != $"{doMods.IsChecked}")
                    {
                        App.AppConfig!.Set("Twitch", $"{key}.permissions.Mods",  $"{doMods.IsChecked}");
                    }

                    if (entry.Children[3] is CheckBox doVips &&
                        App.AppConfig!.Get("Twitch", $"{key}.permissions.VIPs") != $"{doVips.IsChecked}")
                    {
                        App.AppConfig!.Set("Twitch", $"{key}.permissions.VIPs", $"{doVips.IsChecked}");
                    }

                    if (entry.Children[4] is TextBox whitelist &&
                        App.AppConfig!.Get("Twitch", $"{key}.permissions.Whitelist") != whitelist.Text.Trim())
                    {
                        App.AppConfig!.Set("Twitch", $"{key}.permissions.Whitelist", whitelist.Text.Trim());
                    }
                }
        }
        App.AppConfig!.Save();
    }
}