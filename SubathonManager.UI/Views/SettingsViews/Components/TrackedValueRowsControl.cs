using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SubathonManager.UI.Controls;
using SubathonManager.UI.UiUtils;
using SubathonManager.UI.Validation;

namespace SubathonManager.UI.Views.SettingsViews.Components;

public class TrackedValueRowsControl : UserControl
{
    private readonly StackPanel _rowsPanel = new();
    private readonly StackPanel _headerPanel = new()
    {
        Orientation = Orientation.Horizontal,
        IsVisible = false,
        Margin = new Thickness(0, 0, 0, 2)
    };
    private readonly Button _addBtn;
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

    public Action<Control>? WireInput { get; set; }

    public event Action<TrackedValueRow>? RowAdded;
    public event Action<TrackedValueRow>? RowDeleted;

    public IReadOnlyList<TrackedValueRow> Rows => _rows;

    public TrackedValueRowsControl()
    {
        _addBtn = new Button
        {
            Content = "Add Url",
            Width = 120, Height = 32,
            Margin = new Thickness(0, 6, 0, 4),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        _addBtn.SetDynamicResource(Button.ForegroundProperty, "TextFillColorPrimaryBrush");
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
            _headerPanel.IsVisible = false;
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
            secondsLabel.SetDynamicResource(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            pointsLabel.SetDynamicResource(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            _headerPanel.Children.Add(secondsLabel);
            _headerPanel.Children.Add(pointsLabel);
        }
        _headerPanel.IsVisible = true;
    }

    public TrackedValueRow AddRow(object? item = null, string key = "", string? name = null,
        string seconds = "", string points = "")
    {
        var rowGrid = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        var panelRow = new StackPanel { Orientation = Orientation.Horizontal };

        var keyBox = new TextBox
        {
            Width = KeyBoxWidth,
            Text = key,
            PlaceholderText = KeyPlaceholder,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        if (KeyToolTip != null) ToolTip.SetTip(keyBox, KeyToolTip);

        TextBox? nameBox = null;
        if (ShowNameBox)
        {
            nameBox = new TextBox
            {
                Width = NameBoxWidth,
                Text = name ?? "",
                PlaceholderText = NamePlaceholder,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            if (NameToolTip != null) ToolTip.SetTip(nameBox, NameToolTip);
        }

        var secondsBox = new TextBox
        {
            Width = OverrideBoxWidth,
            Text = seconds,
            PlaceholderText = OverridePlaceholder,
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        ToolTip.SetTip(secondsBox, SecondsToolTip);
        var pointsBox = new TextBox
        {
            Width = OverrideBoxWidth,
            Text = points,
            PlaceholderText = OverridePlaceholder,
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        ToolTip.SetTip(pointsBox, PointsToolTip);
        NumericInputBehaviour.SetMode(secondsBox, NumericInputBehaviour.NumericMode.SignedDecimal);
        NumericInputBehaviour.SetMode(pointsBox, NumericInputBehaviour.NumericMode.SignedDecimal);

        var deleteBtn = new Button
        {
            Content = new SymIcon { Glyph = "Delete20", HorizontalAlignment = HorizontalAlignment.Center },
            Foreground = Brushes.Red,
            Cursor = new Cursor(StandardCursorType.Hand),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Width = 32, Height = 32,
            Margin = new Thickness(0, 0, 12, 0)
        };
        ToolTip.SetTip(deleteBtn, "Delete");

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
    public required TextBox KeyBox { get; init; }
    public TextBox? NameBox { get; init; }
    public required TextBox SecondsBox { get; init; }
    public required TextBox PointsBox { get; init; }
    public required StackPanel InfoPanel { get; init; }
    public required Grid RowGrid { get; init; }
}
