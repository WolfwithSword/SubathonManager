namespace SubathonManager.Core.Events;

public class YouTubeEvents
{
    
    public static event Action<bool, string>? YouTubeConnectionUpdated;
    
    public static void RaiseYouTubeConnectionUpdate(bool status, string name)
    {
        YouTubeConnectionUpdated?.Invoke(status, name);
    }
}