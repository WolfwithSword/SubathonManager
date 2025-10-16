namespace SubathonManager.Core.Enums;

public enum SubathonEventType
{
    TwitchSub, // remember subs can be of Value: 1000, 2000, 3000, Prime iirc... damnit looks like the TwitchLib doesnt separate Prime??
    TwitchCheer, // remember 100 is 1$, either in UI we say per 100 bits and divide, or we make em divide
    TwitchGiftSub,
    TwitchRaid,
    TwitchFollow,
    StreamElementsDonation,
    KoFiDonation,
    KoFiSub,
    Command, // from any chat
    YouTubeMembership,
    YouTubeDonation,
    Unknown
}