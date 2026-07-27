using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Models;
using SubathonManager.UI.Services;

namespace SubathonManager.UI.Views;

public partial class ObsAddSourceDialog : Window
{
    private readonly Route _route;
    private readonly string _url;
    private readonly ILogger? _logger = AppServices.Provider.GetService<ILogger<ObsAddSourceDialog>>();

    public ObsAddSourceDialog()
    {
        InitializeComponent();
        _route = new Route();
        _url = string.Empty;
    }

    public ObsAddSourceDialog(Route route, string url, List<string> scenes, string currentScene)
    {
        InitializeComponent();
        _route = route;
        _url = url;

        SourceNameBox.Text = $"[SMO] - {route.Name}";

        var sorted = scenes.OrderBy(s => s).ToList();
        SceneComboBox.ItemsSource = sorted;
        SceneComboBox.SelectedItem = sorted.Contains(currentScene) ? currentScene : sorted.FirstOrDefault();
    }

    private async void AddSource_Click(object? sender, RoutedEventArgs e)
    {
        string sourceName = SourceNameBox.Text?.Trim() ?? string.Empty;
        string? selectedScene = SceneComboBox.SelectedItem as string;

        if (string.IsNullOrWhiteSpace(sourceName) || string.IsNullOrWhiteSpace(selectedScene))
            return;

        try
        {
            await ServiceManager.OBS.AddBrowserSource(
                sourceName, _url, _route.Width, _route.Height, selectedScene,
                FitToScreenCheckBox.IsChecked ?? false);
            Close();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[OBS] AddBrowserSource failed");
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
