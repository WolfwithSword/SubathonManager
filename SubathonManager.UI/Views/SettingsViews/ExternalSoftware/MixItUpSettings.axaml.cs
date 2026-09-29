using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.UI.Services;
using SubathonManager.UI.UiUtils;

namespace SubathonManager.UI.Views.SettingsViews.ExternalSoftware;

public partial class MixItUpSettings : SettingsControl {
    private readonly Dictionary<MixItUpTrigger, TriggerRow> _rows = new();
    private readonly CheckBox _includeCommandsCheck = new() {
        Content = "Send Command events?", Margin = new Thickness(12, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    public MixItUpSettings() {
        InitializeComponent();
        BuildTriggerRows();
        Loaded += (_, _) => {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            UpdateStatus(Utils.GetConnection(SubathonEventSource.MixItUp, MixItUpService.ServiceName));
            RegisterUnsavedChangeHandlers();
        };
        Unloaded += (_, _) => { IntegrationEvents.ConnectionUpdated -= UpdateStatus; };
    }

    public override void Init(SettingsView host) {
        Host = host;
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        SuppressUnsavedChanges(() => {
            EnabledCheck.IsChecked = config.GetBool(MixItUpService.ConfigSection, "Enabled");
            _includeCommandsCheck.IsChecked =
                config.GetBool(MixItUpService.ConfigSection, MixItUpService.IncludeCommandsKey);
            ApiUrlBox.Text = config.Get(MixItUpService.ConfigSection, "ApiUrl", MixItUpService.DefaultApiUrl);
            foreach ((MixItUpTrigger trigger, TriggerRow row) in _rows)
                row.IdBox.Text = config.Get(MixItUpService.ConfigSection,
                    MixItUpService.CommandConfigKey(trigger), "");
        });
        RegisterUnsavedChangeHandlers();
    }

    private void BuildTriggerRows() {
        IEnumerable<MixItUpTrigger> triggers =
            Enum.GetValues<MixItUpTrigger>().OrderBy(t => t.GetOrderNumber());
        foreach (MixItUpTrigger trigger in triggers) {
            
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            var label = new TextBlock {
                Text = trigger.GetLabel(), Width = 120, VerticalAlignment = VerticalAlignment.Center
            };

            ToolTip.SetTip(label,
                $"{trigger.GetDescription()}\n\n{string.Join("\n", MixItUpService.IdentifierNames(trigger))}");

            var idBox = new TextBox {
                Width = 300, Height = 32, PlaceholderText = "Mix It Up Command ID",
                VerticalContentAlignment = VerticalAlignment.Center
            };
            TextBoxAssist.SetClear(idBox, true);

            var picker = new ComboBox {
                Width = 220, Height = 32, Margin = new Thickness(8, 0, 0, 0), IsEnabled = false,
                PlaceholderText = "Load commands to pick"
            };
            SettingsProperties.SetExcludeFromUnsaved(picker, true);

            var testBtn = new Button {
                Content = "Test", Height = 32, Width = 70, Margin = new Thickness(8, 0, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            ToolTip.SetTip(testBtn, "Run this Mix It Up command now with sample data");
            SettingsProperties.SetExcludeFromUnsaved(testBtn, true);

            var row = new TriggerRow(idBox, picker, testBtn);
            idBox.TextChanged += (_, _) => {
                ValidateIdBox(row);
                SyncPicker(row);
            };
            picker.SelectionChanged += (_, _) => {
                if (picker.SelectedItem is ComboBoxItem { Tag: Guid id }) idBox.Text = id.ToString();
            };
            testBtn.Click += async (_, _) => await TestTrigger(trigger, row);

            panel.Children.Add(label);
            panel.Children.Add(idBox);
            panel.Children.Add(picker);
            panel.Children.Add(testBtn);
            if (trigger == MixItUpTrigger.SubathonEvent) {
                ToolTip.SetTip(_includeCommandsCheck,
                    "Commands (add time, pause, etc.) also fire this. Don't enable it if this Mix It Up command calls Subathon Manager back, or it may loop");
                panel.Children.Add(_includeCommandsCheck);
            }

            TriggerRowsPanel.Children.Add(panel);
            var sep = new Separator {
                Height = 1,
                Margin = new Thickness(2, 4, 16, 4)
            };
            TriggerRowsPanel.Children.Add(sep);
            _rows[trigger] = row;
        }
    }

    private static void SyncPicker(TriggerRow row) {
        if (row.Picker.ItemsSource is not IEnumerable<ComboBoxItem> items) return;
        Guid.TryParse((row.IdBox.Text ?? "").Trim(), out Guid current);
        ComboBoxItem? match = items.FirstOrDefault(i => i.Tag is Guid id && id == current);
        if (!ReferenceEquals(row.Picker.SelectedItem, match)) row.Picker.SelectedItem = match;
    }

    private static void ValidateIdBox(TriggerRow row) {
        string text = (row.IdBox.Text ?? "").Trim();
        bool invalid = text.Length > 0 && !Guid.TryParse(text, out _);
        if (invalid) {
            row.IdBox.BorderBrush = Brushes.OrangeRed;
            ToolTip.SetTip(row.IdBox, "Not a valid Mix It Up command ID");
        }
        else {
            row.IdBox.ClearValue(BorderBrushProperty);
            ToolTip.SetTip(row.IdBox, null);
        }
    }

    private static async Task TestTrigger(MixItUpTrigger trigger, TriggerRow row) {
        row.TestBtn.IsEnabled = false;
        bool ok = await ServiceManager.MixItUp.TestTriggerAsync(trigger, row.IdBox.Text ?? "");
        await Dispatcher.UIThread.InvokeAsync(() => row.TestBtn.Content = ok ? "Sent" : "Failed");
        await Task.Delay(1500);
        await Dispatcher.UIThread.InvokeAsync(() => {
            row.TestBtn.Content = "Test";
            row.TestBtn.IsEnabled = true;
        });
    }

    private async void LoadCommands_Click(object? sender, RoutedEventArgs e) {
        LoadCommandsBtn.IsEnabled = false;
        SaveApiUrl();
        IReadOnlyList<MixItUpCommandInfo>? commands = await ServiceManager.MixItUp.GetCommandsAsync();
        await Dispatcher.UIThread.InvokeAsync(() => {
            LoadCommandsBtn.IsEnabled = true;
            LoadCommandsStatusText.IsVisible = true;
            if (commands == null) {
                LoadCommandsStatusText.Text =
                    "Couldn't reach Mix It Up. Check if it's running, the Developer API is connected, and the URL is correct";
                LoadCommandsStatusText.Foreground = Brushes.OrangeRed;
                return;
            }

            LoadCommandsStatusText.Text = $"Loaded {commands.Count} commands";
            LoadCommandsStatusText.ClearValue(TextBlock.ForegroundProperty);
            foreach (TriggerRow row in _rows.Values) {
                Guid.TryParse((row.IdBox.Text ?? "").Trim(), out Guid current);
                var items = commands.Select(c => new ComboBoxItem {
                    Content = c.IsEnabled ? c.DisplayName : $"{c.DisplayName} (disabled)", Tag = c.Id
                }).ToList();
                row.Picker.ItemsSource = items;
                row.Picker.SelectedItem = items.FirstOrDefault(i => i.Tag is Guid id && id == current);
                row.Picker.IsEnabled = items.Count > 0;
                row.Picker.PlaceholderText = "Pick a command";
            }
        });
    }

    private void SaveApiUrl() {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        if (config.Set(MixItUpService.ConfigSection, "ApiUrl", (ApiUrlBox.Text ?? "").Trim()))
            config.Save();
    }

    private async void CheckNow_Click(object? sender, RoutedEventArgs e) {
        CheckNowBtn.IsEnabled = false;
        SaveApiUrl();
        await ServiceManager.MixItUp.ProbeAsync();
        await Dispatcher.UIThread.InvokeAsync(() => CheckNowBtn.IsEnabled = true);
    }

    private void GenerateCommands_Click(object? sender, RoutedEventArgs e) {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        int port = int.TryParse(config.Get("Server", "Port", "14040"), out int parsed) ? parsed : 14040;
        try {
            MixItUpCommandExporter.WriteAll(MixItUpService.CommandsFolder, port);
            UiHelpers.OpenFolder(MixItUpService.CommandsFolder);
        }
        catch (Exception ex) {
            ErrorMessageEvents.RaiseErrorEvent("ERROR", nameof(SubathonEventSource.MixItUp),
                $"Failed to generate Mix It Up commands: {ex.Message}", DateTime.Now);
        }
    }

    internal override void UpdateStatus(IntegrationConnection? connection) {
        if (connection is not { Source: SubathonEventSource.MixItUp, Service: MixItUpService.ServiceName }) return;
        Dispatcher.UIThread.Post(() => {
            MixItUpStatusText.Text = connection.Status
                ? string.IsNullOrWhiteSpace(connection.Name) ? "Connected" : $"Connected ({connection.Name})"
                : "Not seen";
        });
    }

    protected internal override bool UpdateConfigValueSettings() {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        var hasUpdated = false;
        hasUpdated |= config.SetBool(MixItUpService.ConfigSection, "Enabled", EnabledCheck.IsChecked);
        hasUpdated |= config.SetBool(MixItUpService.ConfigSection, MixItUpService.IncludeCommandsKey,
            _includeCommandsCheck.IsChecked);
        hasUpdated |= config.Set(MixItUpService.ConfigSection, "ApiUrl", (ApiUrlBox.Text ?? "").Trim());
        foreach ((MixItUpTrigger trigger, TriggerRow row) in _rows)
            hasUpdated |= config.Set(MixItUpService.ConfigSection, MixItUpService.CommandConfigKey(trigger),
                (row.IdBox.Text ?? "").Trim());
        return hasUpdated;
    }

    public override bool UpdateValueSettings(AppDbContext db) {
        return false;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) {
    }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val) {
        return ("", "", null, null);
    }

    private sealed record TriggerRow(TextBox IdBox, ComboBox Picker, Button TestBtn);
}
