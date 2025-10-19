namespace SubathonManager.Core.Events;

public class StreamElementsEvents
{
    public static event Action<bool>? StreamElementsConnectionChanged;

    public static void RaiseStreamElementsConnectionChanged(bool isConnected)
    {
        StreamElementsConnectionChanged?.Invoke(isConnected);
    }
}