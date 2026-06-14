using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Models;

namespace SubathonManager.Core.Events;

[ExcludeFromCodeCoverage]
public static class WheelEvents
{
    public static event Action<WheelSet, int>? WheelSpinStarted;
    public static event Action<WheelSet, WheelItem?, WheelSpinHistory>? WheelSpinResult;
    public static event Action<WheelSpinHistory>? WheelSpinStatusChanged;
    public static event Action<WheelSet, int>? WheelDataChanged;

    public static void RaiseWheelSpinStarted(WheelSet wheel, int delaySeconds)
        => WheelSpinStarted?.Invoke(wheel, delaySeconds);

    public static void RaiseWheelSpinResult(WheelSet wheel, WheelItem? item, WheelSpinHistory history)
        => WheelSpinResult?.Invoke(wheel, item, history);

    public static void RaiseWheelSpinStatusChanged(WheelSpinHistory history)
        => WheelSpinStatusChanged?.Invoke(history);

    public static void RaiseWheelDataChanged(WheelSet wheel, int spinsOwed)
        => WheelDataChanged?.Invoke(wheel, spinsOwed);
}
