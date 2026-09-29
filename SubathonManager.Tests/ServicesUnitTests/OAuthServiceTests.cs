using System.Globalization;
using System.Net;
using System.Text;
using Moq;
using Moq.Protected;
using SubathonManager.Services;
using SubathonManager.Tests.Utility;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.ServicesUnitTests;

public class OAuthServiceTests {
    private static readonly OAuthProvider Full = new("tiltify", "SM.T.Access", "SM.T.Refresh", "SM.T.Expiry",
        "SM.T.ClientId", DefaultLifetime: TimeSpan.FromHours(2), RefreshMargin: TimeSpan.FromMinutes(10));

    private static readonly OAuthProvider Jwt = new("fourthwall", "SM.F.Access", "SM.F.Refresh");
    private static readonly OAuthProvider AccessOnly = new("twitch", "SM.Tw.Access");

    private static (OAuthService Service, InMemorySecureStorage Storage, List<string> Opened) MakeService(
        Dictionary<string, string>? seed = null, HttpMessageHandler? handler = null) {
        var factory = new Mock<IHttpClientFactory>();
        if (handler != null)
            factory.Setup(f => f.CreateClient(nameof(OAuthService)))
                .Returns(() => new HttpClient(handler, false));

        var storage = new InMemorySecureStorage(seed);
        List<string> opened = [];
        var service = new OAuthService(null, factory.Object, storage) { OpenBrowser = opened.Add };
        return (service, storage, opened);
    }

    private static Mock<HttpMessageHandler> MockHandler(HttpStatusCode status, string json,
        Action<HttpRequestMessage>? capture = null) {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capture?.Invoke(req))
            .ReturnsAsync(() => new HttpResponseMessage(status) { Content = new StringContent(json) });
        return handler;
    }

    private static string MakeJwt(DateTime expiresUtc) {
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{{\"exp\":{new DateTimeOffset(expiresUtc).ToUnixTimeSeconds()}}}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"e30.{payload}.sig";
    }

    private static string Iso(DateTime utc) => utc.ToString("O", CultureInfo.InvariantCulture);

    [Fact]
    public async Task AuthorizeAsync_StoresTokensFromMatchingCallback() {
        (OAuthService service, InMemorySecureStorage storage, List<string> opened) = MakeService(
            new Dictionary<string, string> { ["SM.T.Access"] = "stale", ["SM.T.ClientId"] = "stale" });

        Task<bool> login = service.AuthorizeAsync(Full, TestContext.Current.CancellationToken);

        Assert.Equal(["https://oauth.subathonmanager.app/auth/tiltify/login"], opened);
        Assert.False(service.HasTokens(Full));
        Assert.False(storage.Exists("SM.T.ClientId"));
        Assert.False(service.HandleCallback("subathonmanager://oauth/twitch?access_token=wrong"));
        Assert.True(service.HandleCallback(
            "subathonmanager://oauth/Tiltify?access_token=acc&refresh_token=ref&expires_in=600&client_id=cid"));

        Assert.True(await login);
        Assert.True(service.HasTokens(Full));
        Assert.Equal("acc", service.GetAccessToken(Full));
        Assert.Equal("ref", service.GetRefreshToken(Full));
        Assert.Equal("cid", service.GetClientId(Full));
        Assert.InRange(service.GetExpiry(Full)!.Value, DateTime.UtcNow.AddMinutes(9), DateTime.UtcNow.AddMinutes(11));
    }

    [Theory]
    [InlineData("subathonmanager://oauth/fourthwall?error=access_denied")]
    [InlineData("subathonmanager://oauth/fourthwall")]
    [InlineData("subathonmanager://oauth/fourthwall?access_token=acc")]
    public async Task AuthorizeAsync_ErrorOrIncompleteCallback_StoresNothing(string url) {
        (OAuthService service, _, _) = MakeService();

        Task<bool> login = service.AuthorizeAsync(Jwt, TestContext.Current.CancellationToken);
        Assert.True(service.HandleCallback(url));

        Assert.False(await login);
        Assert.False(service.HasTokens(Jwt));
    }

    [Fact]
    public async Task AuthorizeAsync_AccessOnlyProvider_NeedsNoRefreshToken() {
        (OAuthService service, _, _) = MakeService();

        Task<bool> login = service.AuthorizeAsync(AccessOnly, TestContext.Current.CancellationToken);
        Assert.True(service.HandleCallback("subathonmanager://oauth/twitch?access_token=acc"));

        Assert.True(await login);
        Assert.True(service.HasTokens(AccessOnly));
        Assert.Null(service.GetRefreshToken(AccessOnly));
    }

    [Fact]
    public async Task AuthorizeAsync_NewAttemptSupersedesPrevious() {
        (OAuthService service, _, List<string> opened) = MakeService();

        Task<bool> first = service.AuthorizeAsync(Jwt, TestContext.Current.CancellationToken);
        Task<bool> second = service.AuthorizeAsync(Jwt, TestContext.Current.CancellationToken);

        Assert.False(await first);
        Assert.Equal(2, opened.Count);

        Assert.True(service.HandleCallback("subathonmanager://oauth/fourthwall?access_token=a&refresh_token=r"));
        Assert.True(await second);
        Assert.Equal("a", service.GetAccessToken(Jwt));
        Assert.False(service.HandleCallback("subathonmanager://oauth/fourthwall?access_token=late"));
    }

    [Fact]
    public async Task AuthorizeAsync_TimesOutOrCancels_ReturnsFalse() {
        (OAuthService service, _, _) = MakeService();
        service.CallbackTimeout = TimeSpan.FromMilliseconds(50);
        Assert.False(await service.AuthorizeAsync(Full, TestContext.Current.CancellationToken));

        service.CallbackTimeout = TimeSpan.FromMinutes(1);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        Assert.False(await service.AuthorizeAsync(Full, cts.Token));

        Assert.False(service.HandleCallback("subathonmanager://oauth/tiltify?access_token=late"));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("subathonmanager://overlay?url=https://example.com/a.smo")]
    public void HandleCallback_NonOAuthUrl_ReturnsFalse(string url) {
        (OAuthService service, _, _) = MakeService();
        Assert.False(service.HandleCallback(url));
    }

    [Fact]
    public void RevokeTokens_DeletesOnlyConfiguredKeys() {
        (OAuthService service, InMemorySecureStorage storage, _) = MakeService(new Dictionary<string, string> {
            ["SM.F.Access"] = "a", ["SM.F.Refresh"] = "r", ["SM.F.Other"] = "keep"
        });
        Assert.True(service.HasTokens(Jwt));

        service.RevokeTokens(Jwt);

        Assert.False(service.HasTokens(Jwt));
        Assert.Equal(2, storage.DeleteCount);
        Assert.True(storage.Exists("SM.F.Other"));
    }

    [Fact]
    public void StoreTokens_MissingExpiresIn_UsesDefaultLifetime() {
        (OAuthService service, _, _) = MakeService();
        service.StoreTokens(Full, new() { AccessToken = "a", RefreshToken = "r" });
        Assert.InRange(service.GetExpiry(Full)!.Value, DateTime.UtcNow.AddMinutes(115),
            DateTime.UtcNow.AddMinutes(125));
    }

    [Fact]
    public void NeedsRefresh_UsesStoredExpiryWithMargin() {
        (OAuthService service, _, _) = MakeService(new Dictionary<string, string> {
            ["SM.T.Access"] = "a", ["SM.T.Refresh"] = "r", ["SM.T.Expiry"] = Iso(DateTime.UtcNow.AddMinutes(30))
        });
        Assert.False(service.NeedsRefresh(Full));

        service.StoreTokens(Full, new() { AccessToken = "a", ExpiresIn = "300" });
        Assert.True(service.NeedsRefresh(Full));
    }

    [Fact]
    public void NeedsRefresh_MissingStoredExpiry_RefreshesOnlyWhenExpiryIsTracked() {
        (OAuthService service, _, _) = MakeService(new Dictionary<string, string> {
            ["SM.T.Access"] = "a", ["SM.T.Refresh"] = "r", ["SM.F.Access"] = "not-a-jwt", ["SM.F.Refresh"] = "r"
        });
        Assert.True(service.NeedsRefresh(Full));
        Assert.False(service.NeedsRefresh(Jwt));
        Assert.False(service.NeedsRefresh(AccessOnly));
    }

    [Fact]
    public void NeedsRefresh_ReadsExpiryFromJwtWhenNoExpiryKey() {
        (OAuthService service, _, _) = MakeService(new Dictionary<string, string> {
            ["SM.F.Access"] = MakeJwt(DateTime.UtcNow.AddMinutes(5)), ["SM.F.Refresh"] = "r"
        });
        Assert.False(service.NeedsRefresh(Jwt));

        service.StoreTokens(Jwt, new() { AccessToken = MakeJwt(DateTime.UtcNow.AddSeconds(30)) });
        Assert.True(service.NeedsRefresh(Jwt));
        Assert.Equal("r", service.GetRefreshToken(Jwt));
    }

    [Theory]
    [InlineData("""{"access_token":"new","refresh_token":"newref","expires_in":3600,"client_id":"cid"}""", "newref",
        "cid")]
    [InlineData("""{"access_token":"new","refresh_token":null}""", "old", "oldcid")]
    public async Task RefreshAsync_StoresTokensAndKeepsOldValuesWhenMissing(string json, string expectedRefresh,
        string expectedClientId) {
        HttpRequestMessage? sent = null;
        string? sentBody = null;
        Mock<HttpMessageHandler> handler = MockHandler(HttpStatusCode.OK, json, req => {
            sent = req;
            sentBody = req.Content!.ReadAsStringAsync().Result;
        });
        (OAuthService service, _, _) = MakeService(new Dictionary<string, string> {
            ["SM.T.Access"] = "a", ["SM.T.Refresh"] = "old", ["SM.T.ClientId"] = "oldcid"
        }, handler.Object);

        Assert.True(await service.RefreshAsync(Full, TestContext.Current.CancellationToken));

        Assert.Equal("https://oauth.subathonmanager.app/auth/tiltify/refresh", sent?.RequestUri?.ToString());
        Assert.Equal("refresh_token=old", sentBody);
        Assert.Equal("new", service.GetAccessToken(Full));
        Assert.Equal(expectedRefresh, service.GetRefreshToken(Full));
        Assert.Equal(expectedClientId, service.GetClientId(Full));
        Assert.NotNull(service.GetExpiry(Full));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}""")]
    [InlineData(HttpStatusCode.OK, """{"refresh_token":"r"}""")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task RefreshAsync_FailedOrInvalidResponse_LeavesTokensUntouched(HttpStatusCode status, string json) {
        (OAuthService service, _, _) = MakeService(new Dictionary<string, string> {
            ["SM.F.Access"] = "a", ["SM.F.Refresh"] = "old"
        }, MockHandler(status, json).Object);

        Assert.False(await service.RefreshAsync(Jwt, TestContext.Current.CancellationToken));
        Assert.Equal("a", service.GetAccessToken(Jwt));
        Assert.Equal("old", service.GetRefreshToken(Jwt));
    }

    [Fact]
    public async Task RefreshAsync_NoRefreshToken_ReturnsFalse() {
        (OAuthService service, _, _) = MakeService(new Dictionary<string, string> { ["SM.Tw.Access"] = "a" });
        Assert.False(await service.RefreshAsync(AccessOnly, TestContext.Current.CancellationToken));
        Assert.False(await service.RefreshAsync(Jwt, TestContext.Current.CancellationToken));
    }
}
