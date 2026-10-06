using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Objects;
using SubathonManager.UI.Controls;
using SubathonManager.UI.Services;
using SubathonManager.UI.UiUtils;

namespace SubathonManager.UI.Views.Actions;

public partial class ActionsView {
    private static readonly IBrush TriggerAccent = new SolidColorBrush(Color.FromRgb(0x1C, 0xB8, 0x96));

    private void RefreshTriggers() {
        List<(CustomAction Action, ActionNode Node)> steps = ServiceManager.Actions.TriggerSteps().ToList();
        TriggersPanel.Children.Clear();
        TriggersStatusText.Text = steps.Count == 0 ? "No actions have triggers yet" : "";

        foreach (SubathonTrigger trigger in Enum.GetValues<SubathonTrigger>().OrderBy(t => t.GetOrderNumber())) {
            List<(CustomAction Action, ActionNode Node)> matching =
                steps.Where(s => s.Node.Step.Trigger == trigger).ToList();

            var section = new StackPanel { Spacing = 6 };
            var header = new StackPanel {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = {

                    new Border {
                        MinWidth = 22, CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1),
                        Background = matching.Count > 0 ? TriggerAccent : Brushes.Gray,
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock {
                            Text = $"{matching.Count}", FontSize = 11, FontWeight = FontWeight.Bold,
                            Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center
                        }
                    },

                    new TextBlock {
                        Text = trigger.GetLabel(), FontSize = 15, FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    },

                    new TextBlock {
                        Text = trigger.GetDescription(), FontSize = 12, Foreground = Brushes.Gray,
                        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            };

            SubathonTrigger captured = trigger;
            var expander = new Expander {
                Header = header, Content = section, IsExpanded = _openTriggers.Contains(trigger),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };

            expander.Expanded += (_, _) => _openTriggers.Add(captured);
            expander.Collapsed += (_, _) => _openTriggers.Remove(captured);

            if (matching.Count == 0)
                section.Children.Add(new TextBlock {
                    Text = "No actions", FontSize = 12, Foreground = Brushes.Gray
                });

            foreach ((CustomAction action, ActionNode node) in matching.OrderBy(m => m.Action.Name,
                         StringComparer.OrdinalIgnoreCase))
                section.Children.Add(BuildTriggerRow(action, node));

            TriggersPanel.Children.Add(expander);
        }
    }

    private readonly HashSet<SubathonTrigger> _openTriggers = [];

    private Control BuildTriggerRow(CustomAction action, ActionNode node) {
        bool valid = node.Step.IsValid(out string error) && action.Graph.IsValid(out error);
        var name = new TextBlock { Text = action.Name, FontWeight = FontWeight.SemiBold, FontSize = 14 };
        var summary = new TextBlock {
            Text = node.Step.Describe(), FontSize = 12, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap
        };
        var problem = new TextBlock {
            Text = valid ? "" : $"Won't run: {error}", FontSize = 12, Foreground = Brushes.OrangeRed,
            TextWrapping = TextWrapping.Wrap, IsVisible = !valid
        };
        if (valid && action.Disabled) {
            problem.Text = "Action is disabled";
            problem.Foreground = Brushes.Gray;
            problem.IsVisible = true;
        }

        var enabled = new ToggleSwitch {
            IsChecked = !node.Disabled, OnContent = "On", OffContent = "Off", VerticalAlignment = VerticalAlignment.Center
        };

        enabled.IsCheckedChanged += async (_, _) => {
            CustomAction copy = action.Clone();
            if (copy.Graph.Nodes.FirstOrDefault(n => n.Id == node.Id) is not { } target) return;
            target.Disabled = enabled.IsChecked != true;
            try {
                await ServiceManager.Actions.SaveCustomActionAsync(copy);
                TriggersStatusText.Text = $"{(target.Disabled ? "Turned off" : "Turned on")} a trigger in \"{action.Name}\"";
            }
            catch (Exception ex) {
                _logger?.LogError(ex, "[Actions] Saving {Name} failed", action.Name);
                TriggersStatusText.Text = $"Could not save \"{action.Name}\": {ex.Message}";
            }
        };

        var test = new Button {
            Classes = { "opaquesecondary", "iconbtn" }, Width = 32, Height = 32, Padding = new Thickness(2),
            Content = new SymIcon { Glyph = "Play20" }, IsEnabled = valid
        };

        test.Click += async (_, _) => {
            TriggersStatusText.Text = $"Testing \"{action.Name}\"...";
            ActionRunResult result = await ServiceManager.Actions.TestTriggerAsync(action, node.Id);
            TriggersStatusText.Text = result switch {
                ActionRunResult.Done => $"\"{action.Name}\" finished",
                ActionRunResult.Skipped => $"\"{action.Name}\" is already running - skipped",
                ActionRunResult.Cancelled => $"\"{action.Name}\" was cancelled",
                _ => $"\"{action.Name}\" stopped at a step that could not be run - check the logs"
            };
        };

        var open = new Button {
            Classes = { "opaquesecondary", "iconbtn" }, Width = 32, Height = 32, Padding = new Thickness(2),
            Content = new SymIcon { Glyph = "Edit20" }
        };
        ToolTip.SetTip(open, "Open in the editor");
        open.Click += (_, _) => OpenEditor(action);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(new StackPanel { Children = { name, summary, problem } });
        Grid.SetColumn(enabled, 1);
        grid.Children.Add(enabled);
        Grid.SetColumn(test, 2);
        grid.Children.Add(test);
        Grid.SetColumn(open, 3);
        grid.Children.Add(open);

        var card = new Border {
            Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Child = grid, Opacity = node.Disabled || action.Disabled ? 0.6 : 1
        };
        card.SetDynamicResource(Border.BorderBrushProperty, "UiBrushBorder");
        card.SetDynamicResource(Border.BackgroundProperty, "UiBrushControlBackground");
        return card;
    }
}
