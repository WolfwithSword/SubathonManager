namespace SubathonManager.Core.Enums;

public enum SubathonEventSource
{
    Twitch,
    StreamElements,
    KoFi,
    YouTube,
    Command, // can be from any chat
    Simulated, // buttons to test in UI? 
    Unknown, // default
    StreamLabs,
    External,
    Blerp
}
