using SubathonManager.Core.Models;
using SubathonManager.Core.Enums;
using System.Text.RegularExpressions;
using SubathonManager.Core.Events;
namespace SubathonManager.Integration;

public static class BlerpChatService
{
    public static bool ParseMessage(string message, SubathonEventSource source)
    {
        // user Blerp is verified prior to here
        var regex = new Regex(
            @"^([^\s]+)\s+\w+\s+(\d+)\s+(bits|beets)\b",
            RegexOptions.IgnoreCase
        );
        var match = regex.Match(message);

        if (!match.Success)
            return false;
        
        var user = match.Groups[1].Value;
        var amount = match.Groups[2].Value; 
        var currency = match.Groups[3].Value;
        
        SubathonEvent subathonEvent = new SubathonEvent()
        {
            User = user,
            Currency = currency.ToLower(),
            Value = $"{amount}",
            Source = source == SubathonEventSource.Simulated ? SubathonEventSource.Simulated : SubathonEventSource.Blerp,
            EventType = currency.Equals("bits", StringComparison.OrdinalIgnoreCase)
                ? SubathonEventType.BlerpBits : SubathonEventType.BlerpBeets
        };
        
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        return true;
    }

    public static void SimulateBlerpMessage(long amount, string bitsBeets)
    {
        if (!bitsBeets.Equals("bits", StringComparison.CurrentCultureIgnoreCase) && 
            !bitsBeets.Equals("beets", StringComparison.CurrentCultureIgnoreCase))
            return;
        var message = $"SYSTEM used {amount} {bitsBeets} to play a thing";
        ParseMessage(message, SubathonEventSource.Simulated);
    }
}