namespace SubathonManager.Core.Enums;

public enum ProcessSearch {
    [ProcessSearchMeta(QueryNames = ["obs", "obs64", "OBS"])]
    OBS,

    [ProcessSearchMeta(QueryNames = ["VTube Studio", "VTubeStudio"])]
    VTubeStudio,

    [ProcessSearchMeta(QueryNames = ["Streamer.bot"])]
    StreamerBot,

    [ProcessSearchMeta(QueryNames = ["Stream Deck", "StreamDeck"])]
    StreamDeck,

    [ProcessSearchMeta(QueryNames = ["MixItUp", "Mix It Up"])]
    MixItUp
}