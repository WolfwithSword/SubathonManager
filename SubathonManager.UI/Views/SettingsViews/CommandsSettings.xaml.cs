using System.Windows.Controls;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;

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
        bool hasNewCommands = false;
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        foreach (SubathonCommandType commandType in Enum.GetValues(typeof(SubathonCommandType)))
        {
            if (commandType == SubathonCommandType.None || commandType == SubathonCommandType.Unknown) continue;
            // 200 | 30 blank | 200 | 120 | 120 | remain
            // enum / blank / name / mods / vips / whitelist 
            bool.TryParse(config!.Get("Chat", $"Commands.{commandType}.permissions.Mods", "false")!, out var checkMods);
            bool.TryParse(config!.Get("Chat",$"Commands.{commandType}.permissions.VIPs", "false")!, out var checkVips);
            string name = config!.Get("Chat",$"Commands.{commandType}.name", commandType.ToString().ToLower())!;
            string whitelist = config!.Get("Chat", $"Commands.{commandType}.permissions.Whitelist", string.Empty)!;
            
            if (config!.Get("Chat",$"Commands.{commandType}.name") == string.Empty
                && !checkMods && !checkVips && whitelist == string.Empty)
            {
                config!.Set("Chat", $"Commands.{commandType}.name", name);
                config!.Set("Chat", $"Commands.{commandType}.permissions.Mods", "False");
                config!.Set("Chat", $"Commands.{commandType}.permissions.VIPs", "False");
                config!.Set("Chat", $"Commands.{commandType}.permissions.Whitelist", string.Empty);
                hasNewCommands = true;
            }

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

            Wpf.Ui.Controls.TextBox enumName = new Wpf.Ui.Controls.TextBox
            {
                Text = name,
                Width = 200,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                PlaceholderText = name
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

            Wpf.Ui.Controls.TextBox enumWhitelist = new Wpf.Ui.Controls.TextBox
            {
                Text = whitelist,
                Width = 456,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            entryPanel.Children.Add(enumType);
            entryPanel.Children.Add(enumName);
            entryPanel.Children.Add(doMods);
            entryPanel.Children.Add(doVips);
            entryPanel.Children.Add(enumWhitelist);

            CommandListPanel.Children.Add(entryPanel);
        }

        if (hasNewCommands)
            config!.Save();
    }
    
    public void UpdateValueSettings()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        foreach (var child in CommandListPanel.Children)
        {
            if (child is StackPanel entry)

                if (entry.Children[0] is TextBlock enumType)
                {
                    string key = $"Commands.{enumType.Text}";
                        
                    if (entry.Children[1] is TextBox enumName &&
                        config!.Get("Chat", $"{key}.name") != enumName.Text.Trim())
                    {
                        config!.Set("Chat", $"{key}.name", enumName.Text.Trim());
                    }
                    
                    if (entry.Children[2] is CheckBox doMods &&
                        config!.Get("Chat", $"{key}.permissions.Mods") != $"{doMods.IsChecked}")
                    {
                        config!.Set("Chat", $"{key}.permissions.Mods",  $"{doMods.IsChecked}");
                    }

                    if (entry.Children[3] is CheckBox doVips &&
                        config!.Get("Chat", $"{key}.permissions.VIPs") != $"{doVips.IsChecked}")
                    {
                        config!.Set("Chat", $"{key}.permissions.VIPs", $"{doVips.IsChecked}");
                    }

                    if (entry.Children[4] is TextBox whitelist &&
                        config!.Get("Chat", $"{key}.permissions.Whitelist") != whitelist.Text.Trim())
                    {
                        config!.Set("Chat", $"{key}.permissions.Whitelist", whitelist.Text.Trim());
                    }
                }
        }
        config!.Save();
    }
}