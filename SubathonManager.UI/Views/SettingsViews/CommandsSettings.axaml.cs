using Avalonia.Controls;
using Avalonia.Layout;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;

namespace SubathonManager.UI.Views.SettingsViews;

public partial class CommandsSettings : SettingsControl
{
    public CommandsSettings()
    {
        InitializeComponent();
        Loaded += (_, _) => RegisterUnsavedChangeHandlers();
    }

    public override void Init(SettingsView host)
    {
        Host = host;
        SuppressUnsavedChanges(InitCommandSettings);
    }

    internal override void UpdateStatus(IntegrationConnection? connection) => throw new NotImplementedException();

    public override bool UpdateValueSettings(AppDbContext db) => false;

    private void InitCommandSettings()
    {
        bool hasNewCommands = false;
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        foreach (SubathonCommandType commandType in Enum.GetValues(typeof(SubathonCommandType)))
        {
            if (commandType == SubathonCommandType.None || commandType == SubathonCommandType.Unknown) continue;

            var checkMods = config.GetBool("Chat", $"Commands.{commandType}.permissions.Mods", false);
            var checkVips = config.GetBool("Chat", $"Commands.{commandType}.permissions.VIPs", false);
            string name = config.Get("Chat", $"Commands.{commandType}.name", commandType.ToString().ToLower())!;
            string whitelist = config.Get("Chat", $"Commands.{commandType}.permissions.Whitelist", string.Empty)!;

            if (config.Get("Chat", $"Commands.{commandType}.name") == string.Empty
                && !checkMods && !checkVips && whitelist == string.Empty)
            {
                config.Set("Chat", $"Commands.{commandType}.name", name);
                config.SetBool("Chat", $"Commands.{commandType}.permissions.Mods", false);
                config.SetBool("Chat", $"Commands.{commandType}.permissions.VIPs", false);
                config.Set("Chat", $"Commands.{commandType}.permissions.Whitelist", string.Empty);
                hasNewCommands = true;
            }
            
            var entryPanel = new Grid
            {
                Height = 40,
                ColumnDefinitions = new ColumnDefinitions("200,30,200,120,120,*")
            };

            var enumType = new TextBlock
            {
                Text = commandType.GetDescription(),
                Tag = commandType,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            Grid.SetColumn(enumType, 0);

            var enumName = new TextBox
            {
                Text = name,
                VerticalAlignment = VerticalAlignment.Center,
                PlaceholderText = name
            };
            Grid.SetColumn(enumName, 2);

            var doMods = new CheckBox
            {
                IsChecked = checkMods,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            ToolTip.SetTip(doMods, "Allow Mods");
            Grid.SetColumn(doMods, 3);

            var doVips = new CheckBox
            {
                IsChecked = checkVips,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            ToolTip.SetTip(doVips, "Allow VIPs");
            Grid.SetColumn(doVips, 4);

            var enumWhitelist = new TextBox
            {
                Text = whitelist,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            Grid.SetColumn(enumWhitelist, 5);

            entryPanel.Children.Add(enumType);
            entryPanel.Children.Add(enumName);
            entryPanel.Children.Add(doMods);
            entryPanel.Children.Add(doVips);
            entryPanel.Children.Add(enumWhitelist);

            CommandListPanel.Children.Add(entryPanel);

            WireControl(enumName);
            WireControl(doMods);
            WireControl(doVips);
            WireControl(enumWhitelist);
        }

        if (hasNewCommands)
            config.Save();
    }

    protected internal override bool UpdateConfigValueSettings()
    {
        bool hasUpdated = false;
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        foreach (var child in CommandListPanel.Children)
        {
            if (child is not Grid entry) continue;
            if (entry.Children[0] is not TextBlock enumType) continue;
            string key = $"Commands.{enumType.Tag}";

            if (entry.Children[1] is TextBox enumName)
                hasUpdated |= config.Set("Chat", $"{key}.name", (enumName.Text ?? "").Trim());

            if (entry.Children[2] is CheckBox doMods)
                hasUpdated |= config.Set("Chat", $"{key}.permissions.Mods", $"{doMods.IsChecked}");

            if (entry.Children[3] is CheckBox doVips)
                hasUpdated |= config.Set("Chat", $"{key}.permissions.VIPs", $"{doVips.IsChecked}");

            if (entry.Children[4] is TextBox whitelist)
                hasUpdated |= config.Set("Chat", $"{key}.permissions.Whitelist", (whitelist.Text ?? "").Trim());
        }

        return hasUpdated;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) => throw new NotImplementedException();

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val) => throw new NotImplementedException();
}
