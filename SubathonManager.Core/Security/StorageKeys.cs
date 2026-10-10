namespace SubathonManager.Core.Security;

public static class StorageKeys {
    public const string TwitchAccessToken = "SM.Twitch.AccessToken";

    // + lowercase secret name
    public const string ActionSecretPrefix = "SM.Actions.Secrets.";

    public const string FourthWallAccessToken = "SM.FourthWall.AccessToken";
    public const string FourthWallRefreshToken = "SM.FourthWall.RefreshToken";

    public const string StreamLabsSocketToken = "SM.StreamLabs.SocketToken";
    public const string StreamElementsJwt = "SM.StreamElements.JWTToken";
    public const string KoFiVerificationToken = "SM.KoFi.VerificationToken";

    public const string GoAffProEmail = "SM.GoAffPro.Email";
    public const string GoAffProPassword = "SM.GoAffPro.Password";

    public const string OBSWebSocketPassword = "SM.OBS.WebSocket.Password";

    public const string VTubeStudioAuthToken = "SM.VTubeStudio.AuthToken";

    public const string TipeeeStreamAccessToken = "SM.TipeeeStream.AccessToken";
    public const string TipeeeStreamRefreshToken = "SM.TipeeeStream.RefreshToken";
    public const string TipeeeStreamApiKey = "SM.TipeeeStream.ApiKey";

    public const string TangiaEventKey = "SM.Tangia.EventKey";

    public const string PallyApiKey = "SM.PallyGG.ApiKey";

    public const string TreatStreamAccessToken = "SM.TreatStream.AccessToken";
    public const string TreatStreamRefreshToken = "SM.TreatStream.RefreshToken";
    public const string TreatStreamTokenExpiry = "SM.TreatStream.TokenExpiry";
    public const string TreatStreamClientId = "SM.TreatStream.ClientId";

    public const string TiltifyAccessToken = "SM.Tiltify.AccessToken";
    public const string TiltifyRefreshToken = "SM.Tiltify.RefreshToken";
    public const string TiltifyTokenExpiry = "SM.Tiltify.TokenExpiry";

    public const string PatreonAccessToken = "SM.Patreon.AccessToken";
    public const string PatreonRefreshToken = "SM.Patreon.RefreshToken";
    public const string PatreonTokenExpiry = "SM.Patreon.TokenExpiry";
    public const string PatreonWebhookId = "SM.Patreon.WebhookId";
    public const string PatreonWebhookSecret = "SM.Patreon.WebhookSecret";
}