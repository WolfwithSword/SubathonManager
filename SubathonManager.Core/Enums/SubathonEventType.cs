namespace SubathonManager.Core.Enums;

public enum SubathonEventType
{
    TwitchSub, // remember subs can be of Value: 1000, 2000, 3000, Prime iirc... damnit looks like the TwitchLib doesnt separate Prime??
    TwitchCheer, // remember 100 is 1$, either in UI we say per 100 bits and divide, or we make em divide
    TwitchGiftSub,
    TwitchRaid,
    TwitchFollow,
    StreamElementsDonation,
    Command, // from any chat or ui
    Unknown,
    StreamLabsDonation
    
    //KoFiDonation,
    //KoFiSub,
    //YouTubeMembership,
    //YouTubeDonation,
    // any new must be added after the last
}