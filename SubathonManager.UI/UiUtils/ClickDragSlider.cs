using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SubathonManager.UI.UiUtils;

public class ClickDragSlider : Slider
{
    private bool _isDragging = false;

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        _isDragging = true;
        CaptureMouse();
        SetValueFromMouse(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (!_isDragging) return;
        SetValueFromMouse(e.GetPosition(this));
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        _isDragging = false;
        ReleaseMouseCapture();
    }

    private void SetValueFromMouse(Point pos)
    {
        double ratio = Math.Clamp(pos.X / ActualWidth, 0, 1);
        Value = Minimum + ratio * (Maximum - Minimum);
    }
}