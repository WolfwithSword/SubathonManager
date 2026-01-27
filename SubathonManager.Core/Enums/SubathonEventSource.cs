namespace SubathonManager.Core.Enums;

public enum SubathonEventSource
{
    // some may not actually be event sources in the future, but also integration sources
    Twitch,
    StreamElements,
    KoFi,
    YouTube,
    Command, // can be from any chat
    Simulated, // buttons to test in UI? 
    Unknown, // default
    StreamLabs,
    External,
    Blerp,
    Picarto
}
