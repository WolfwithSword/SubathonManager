using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Models;

namespace SubathonManager.Core.Events;

[ExcludeFromCodeCoverage]
public static class WheelEvents
{
    public static event Action<WheelSet, int>? WheelSpinStarted;
    public static event Action<WheelSet, WheelItem?, WheelSpinHistory, int>? WheelSpinResult;
    public static event Action<WheelSpinHistory, int>? WheelSpinStatusChanged;
    public static event Action<WheelSet, int>? WheelDataChanged;
    public static event Action<int>? OnSpinsOwedUpdateFromEvent;

    public static event Action<WheelSpinTrigger, WheelSpinTriggerHistory, int>? WheelSpinTriggerFired;
    public static event Action? WheelSpinTriggersChanged;

    public static void RaiseSpinsOwedUpdateFromEvent(int amount)
    {
        OnSpinsOwedUpdateFromEvent?.Invoke(amount);
    }

    public static void RaiseWheelSpinStarted(WheelSet wheel, int delaySeconds)
        => WheelSpinStarted?.Invoke(wheel, delaySeconds);

    public static void RaiseWheelSpinResult(WheelSet wheel, WheelItem? item, WheelSpinHistory history, int spinsOwed)
        => WheelSpinResult?.Invoke(wheel, item, history, spinsOwed);

    public static void RaiseWheelSpinStatusChanged(WheelSpinHistory history, int spinsOwed)
        => WheelSpinStatusChanged?.Invoke(history, spinsOwed);

    public static void RaiseWheelDataChanged(WheelSet wheel, int spinsOwed)
        => WheelDataChanged?.Invoke(wheel, spinsOwed);

    public static void RaiseWheelSpinTriggerFired(WheelSpinTrigger trigger, WheelSpinTriggerHistory history, int newSpinsOwed)
        => WheelSpinTriggerFired?.Invoke(trigger, history, newSpinsOwed);

    public static void RaiseWheelSpinTriggersChanged()
        => WheelSpinTriggersChanged?.Invoke();
}
