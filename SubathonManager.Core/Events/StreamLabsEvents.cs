namespace SubathonManager.Core.Events;

public class StreamLabsEvents
{
    public static event Action<bool>? StreamLabsConnectionChanged;

    public static void RaiseStreamLabsConnectionChanged(bool isConnected)
    {
        StreamLabsConnectionChanged?.Invoke(isConnected);
    }
}