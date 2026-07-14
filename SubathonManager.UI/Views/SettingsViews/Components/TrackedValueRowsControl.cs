using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SubathonManager.UI.Validation;

namespace SubathonManager.UI.Views.SettingsViews.Components;

public class TrackedValueRowsControl : UserControl
{
    private readonly StackPanel _rowsPanel = new();
    private readonly StackPanel _headerPanel = new()
    {
        Orientation = Orientation.Horizontal,
        Visibility = Visibility.Collapsed,
        Margin = new Thickness(0, 0, 0, 2)
    };
    private readonly Wpf.Ui.Controls.Button _addBtn;
    private readonly List<TrackedValueRow> _rows = new();

    public double KeyBoxWidth { get; set; } = 420;
    public string KeyPlaceholder { get; set; } = "";
    public string? KeyToolTip { get; set; }

    public bool ShowNameBox { get; set; }
    public double NameBoxWidth { get; set; } = 150;
    public string NamePlaceholder { get; set; } = "Name";
    public string? NameToolTip { get; set; }

    public double OverrideBoxWidth { get; set; } = 76;
    public string OverridePlaceholder { get; set; } = "Default";
    public string SecondsToolTip { get; set; } = "Seconds per override. Blank = use default.";
    public string PointsToolTip { get; set; } = "Points per override. Blank = use default.";

    public string AddButtonText
    {
        get => _addBtn.Content as string ?? "";
        set => _addBtn.Content = value;
    }
    
    public Action<DependencyObject>? WireInput { get; set; }

    public event Action<TrackedValueRow>? RowAdded;
    public event Action<TrackedValueRow>? RowDeleted;

    public IReadOnlyList<TrackedValueRow> Rows => _rows;

    public TrackedValueRowsControl()
    {
        _addBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Add Url",
            Width = 120, Height = 32,
            Margin = new Thickness(0, 6, 0, 4),
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Add24 },
            Cursor = System.Windows.Input.Cursors.Hand
        };
        _addBtn.SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        SettingsProperties.SetExcludeFromUnsaved(_addBtn, true);
        _addBtn.Click += (_, _) => RowAdded?.Invoke(AddRow());

        var root = new StackPanel();
        root.Children.Add(_headerPanel);
        root.Children.Add(_rowsPanel);
        root.Children.Add(_addBtn);
        Content = root;
    }

    private void UpdateHeader()
    {
        if (_rows.Count == 0)
        {
            _headerPanel.Visibility = Visibility.Collapsed;
            return;
        }
        if (_headerPanel.Children.Count == 0)
        {
            double offset = KeyBoxWidth + 8 + 4 + (ShowNameBox ? NameBoxWidth + 8 : 0);
            var secondsLabel = new TextBlock
            {
                Text = "Seconds",
                Width = OverrideBoxWidth + 4,
                Margin = new Thickness(offset, 0, 0, 0),
                FontSize = 11
            };
            var pointsLabel = new TextBlock
            {
                Text = "Points",
                Width = OverrideBoxWidth,
                Margin = new Thickness(4, 0, 0, 0),
                FontSize = 11
            };
            secondsLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            pointsLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            _headerPanel.Children.Add(secondsLabel);
            _headerPanel.Children.Add(pointsLabel);
        }
        _headerPanel.Visibility = Visibility.Visible;
    }

    public TrackedValueRow AddRow(object? item = null, string key = "", string? name = null,
        string seconds = "", string points = "")
    {
        var rowGrid = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        var panelRow = new StackPanel { Orientation = Orientation.Horizontal };

        var keyBox = new Wpf.Ui.Controls.TextBox
        {
            Width = KeyBoxWidth,
            Text = key,
            PlaceholderText = KeyPlaceholder,
            ToolTip = KeyToolTip,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        Wpf.Ui.Controls.TextBox? nameBox = null;
        if (ShowNameBox)
        {
            nameBox = new Wpf.Ui.Controls.TextBox
            {
                Width = NameBoxWidth,
                Text = name ?? "",
                PlaceholderText = NamePlaceholder,
                ToolTip = NameToolTip,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
        }

        var secondsBox = new Wpf.Ui.Controls.TextBox
        {
            Width = OverrideBoxWidth,
            Text = seconds,
            PlaceholderText = OverridePlaceholder,
            ToolTip = SecondsToolTip,
            ClearButtonEnabled = false,
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        var pointsBox = new Wpf.Ui.Controls.TextBox
        {
            Width = OverrideBoxWidth,
            Text = points,
            PlaceholderText = OverridePlaceholder,
            ToolTip = PointsToolTip,
            ClearButtonEnabled = false,
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        InputValidationBehavior.SetIsDecimalOnly(secondsBox, true);
        InputValidationBehavior.SetIsDecimalOnly(pointsBox, true);

        var deleteBtn = new Wpf.Ui.Controls.Button
        {
            ToolTip = "Delete",
            Icon = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24,
                Margin = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Center
            },
            Foreground = Brushes.Red,
            Cursor = System.Windows.Input.Cursors.Hand,
            Width = 36, Height = 36,
            Margin = new Thickness(0, 0, 12, 0)
        };

        var infoPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        panelRow.Children.Add(keyBox);
        if (nameBox != null) panelRow.Children.Add(nameBox);
        panelRow.Children.Add(secondsBox);
        panelRow.Children.Add(pointsBox);
        panelRow.Children.Add(deleteBtn);
        panelRow.Children.Add(infoPanel);
        rowGrid.Children.Add(panelRow);
        _rowsPanel.Children.Add(rowGrid);

        var row = new TrackedValueRow
        {
            Item = item,
            KeyBox = keyBox, NameBox = nameBox,
            SecondsBox = secondsBox, PointsBox = pointsBox,
            InfoPanel = infoPanel, RowGrid = rowGrid
        };
        _rows.Add(row);

        WireInput?.Invoke(keyBox);
        if (nameBox != null) WireInput?.Invoke(nameBox);
        WireInput?.Invoke(secondsBox);
        WireInput?.Invoke(pointsBox);

        deleteBtn.Click += (_, _) => RemoveRow(row);
        UpdateHeader();
        return row;
    }

    public void RemoveRow(TrackedValueRow row)
    {
        if (!_rows.Remove(row)) return;
        _rowsPanel.Children.Remove(row.RowGrid);
        UpdateHeader();
        RowDeleted?.Invoke(row);
    }

    public void ClearRows()
    {
        _rows.Clear();
        _rowsPanel.Children.Clear();
        UpdateHeader();
    }
}

public sealed class TrackedValueRow
{
    public object? Item { get; set; }
    public object? HostState { get; set; }
    public required Wpf.Ui.Controls.TextBox KeyBox { get; init; }
    public Wpf.Ui.Controls.TextBox? NameBox { get; init; }
    public required Wpf.Ui.Controls.TextBox SecondsBox { get; init; }
    public required Wpf.Ui.Controls.TextBox PointsBox { get; init; }
    public required StackPanel InfoPanel { get; init; }
    public required Grid RowGrid { get; init; }
}
