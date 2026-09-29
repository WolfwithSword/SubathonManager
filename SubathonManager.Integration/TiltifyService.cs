using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Http.HttpClientLibrary;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;
using SubathonManager.Services;
using Tiltify.Client.Generated;
using Tiltify.Client.Generated.Api.Public.CurrentUser;
using Tiltify.Client.Generated.Models;

namespace SubathonManager.Integration;

public class TiltifyService(
    ILogger<TiltifyService>? logger,
    IConfig config,
    ITimerService timerService,
    OAuthService oAuth) : IAppService, IDisposable {
    private const string ApiBase = "https://v5api.tiltify.com";
    internal const string CampaignIdsKey = "CampaignIds";
    internal const string TeamCampaignIdsKey = "TeamCampaignIds";

    private static readonly OAuthProvider OAuthKeys = new("tiltify", StorageKeys.TiltifyAccessToken,
        StorageKeys.TiltifyRefreshToken, StorageKeys.TiltifyTokenExpiry,
        DefaultLifetime: TimeSpan.FromHours(2), RefreshMargin: TimeSpan.FromMinutes(10));

    private readonly string _configSection = "Tiltify";
    private readonly Dictionary<Guid, DateTimeOffset> _seen = new();

    private readonly Dictionary<Guid, DateTimeOffset> _watermarks = new();
    private HttpClientRequestAdapter? _adapter;

    private TiltifyApiClient? _client;
    private bool _disposed;
    private CancellationTokenSource? _pollCts;
    private IDisposable? _refreshTimerHandle;
    private Guid? _userId;
    private string? _username;
    internal int MaxPagesPerPoll = 20;
    internal int PageSize = 100;

    internal TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    internal TimeSpan PollOverlap = TimeSpan.FromMinutes(2);

    public IReadOnlyList<CampaignOption> KnownCampaigns { get; private set; } = [];

    private string? AccessToken => oAuth.GetAccessToken(OAuthKeys);

    public async Task StartAsync(CancellationToken ct = default) {
        if (!HasTokens()) {
            logger?.LogInformation("[Tiltify] Not configured. Integration disabled.");
            BroadcastStatus(false);
            return;
        }

        await InitializeAsync(ct);
    }

    public Task StopAsync(CancellationToken ct = default) {
        _refreshTimerHandle?.Dispose();
        _refreshTimerHandle = null;
        StopPolling();
        _client = null;
        _adapter = null;
        BroadcastStatus(false);
        return Task.CompletedTask;
    }

    [ExcludeFromCodeCoverage]
    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _refreshTimerHandle?.Dispose();
        StopPolling();
        GC.SuppressFinalize(this);
    }

    [ExcludeFromCodeCoverage]
    public async Task ConnectAsync(CancellationToken ct = default) {
        await StopAsync(ct);
        if (!await oAuth.AuthorizeAsync(OAuthKeys, ct)) {
            BroadcastStatus(false);
            return;
        }

        await InitializeAsync(ct);
    }

    [ExcludeFromCodeCoverage]
    private async Task InitializeAsync(CancellationToken ct = default) {
        if (oAuth.NeedsRefresh(OAuthKeys) && !await oAuth.RefreshAsync(OAuthKeys, ct) &&
            !await oAuth.AuthorizeAsync(OAuthKeys, ct)) {
            BroadcastStatus(false);
            return;
        }

        _adapter = new HttpClientRequestAdapter(new TiltifyBearerAuthProvider(() => AccessToken)) {
            BaseUrl = ApiBase
        };
        _client = new TiltifyApiClient(_adapter);

        try {
            CurrentUserGetResponse? me = await _client.Api.Public.CurrentUser.GetAsync(cancellationToken: ct);
            _userId = me?.Data?.Id;
            _username = me?.Data?.Username;
        }
        catch (Exception ex) {
            logger?.LogWarning(ex, "[Tiltify] Failed to fetch current user");
            BroadcastStatus(false);
            return;
        }

        if (_userId == null) {
            logger?.LogWarning("[Tiltify] Current user had no id");
            BroadcastStatus(false);
            return;
        }

        _refreshTimerHandle?.Dispose();
        _refreshTimerHandle = timerService.Register(
            $"{nameof(TiltifyService)}.TokenRefresh",
            TimeSpan.FromMinutes(5),
            async token => {
                if (!oAuth.NeedsRefresh(OAuthKeys)) return;
                if (await oAuth.RefreshAsync(OAuthKeys, token)) return;
                logger?.LogWarning("[Tiltify] Periodic token refresh failed - disconnecting");
                await StopAsync(token);
            });

        logger?.LogInformation("[Tiltify] Connected as {User}", _username);
        await GetAvailableCampaignsAsync(ct);
        BroadcastStatus(true);
        StartPolling();
    }

    public List<(Guid Id, bool IsTeam)> GetSelectedCampaigns() {
        return ParseIds(config.Get(_configSection, CampaignIdsKey))
            .Select(id => (id, false))
            .Concat(ParseIds(config.Get(_configSection, TeamCampaignIdsKey)).Select(id => (id, true)))
            .ToList();
    }

    public bool SetSelectedCampaigns(IEnumerable<CampaignOption> campaigns) {
        List<CampaignOption> list = campaigns.ToList();
        var hasUpdated = false;
        hasUpdated |= config.Set(_configSection, CampaignIdsKey,
            string.Join(',', list.Where(c => !c.IsTeam).Select(c => c.Id)));
        hasUpdated |= config.Set(_configSection, TeamCampaignIdsKey,
            string.Join(',', list.Where(c => c.IsTeam).Select(c => c.Id)));
        if (hasUpdated && _client != null) StartPolling();
        return hasUpdated;
    }

    private static IEnumerable<Guid> ParseIds(string? raw) {
        return (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out Guid g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct();
    }

    [ExcludeFromCodeCoverage]
    public async Task<List<CampaignOption>> GetAvailableCampaignsAsync(CancellationToken ct = default) {
        List<CampaignOption> options = [];
        if (_client == null || _adapter == null || _userId == null) return options;

        try {
            RequestInformation campaignsReq = _client.Api.Public.Users[_userId?.ToString()].Campaigns
                .ToGetRequestInformation(r => r.QueryParameters.Limit = 100);
            (List<Campaign> campaigns, _) = await GetPageAsync(campaignsReq, Campaign.CreateFromDiscriminatorValue, ct);
            options.AddRange(campaigns.Where(c => c.Id != null)
                .Select(c => new CampaignOption(c.Id!.Value, c.Name ?? c.Slug ?? c.Id.ToString()!, false)));

            RequestInformation teamsReq = _client.Api.Public.Users[_userId?.ToString()].Teams
                .ToGetRequestInformation(r => r.QueryParameters.Limit = 100);
            (List<Team> teams, _) = await GetPageAsync(teamsReq, Team.CreateFromDiscriminatorValue, ct);

            foreach (Team team in teams.Where(t => t.Id != null)) {
                RequestInformation teamCampaignsReq = _client.Api.Public.Teams[team.Id?.ToString()].Team_campaigns
                    .ToGetRequestInformation(r => r.QueryParameters.Limit = 100);
                (List<TeamCampaign> teamCampaigns, _) =
                    await GetPageAsync(teamCampaignsReq, TeamCampaign.CreateFromDiscriminatorValue, ct);
                options.AddRange(teamCampaigns.Where(c => c.Id != null)
                    .Select(c => new CampaignOption(c.Id!.Value, $"{team.Name}: {c.Name ?? c.Slug}", true)));
            }
        }
        catch (Exception ex) {
            logger?.LogWarning(ex, "[Tiltify] Failed to list campaigns");
        }

        if (options.Count > 0) KnownCampaigns = options.ToList();
        return options;
    }

    private string? GetCampaignName(Guid? id) {
        return id == null ? null : KnownCampaigns.FirstOrDefault(c => c.Id == id)?.Name;
    }

    private void StartPolling() {
        StopPolling();
        _watermarks.Clear();
        _seen.Clear();
        _pollCts = new CancellationTokenSource();
        CancellationToken token = _pollCts.Token;
        _ = Task.Run(() => PollAsync(token), token);
    }

    private void StopPolling() {
        if (_pollCts is { IsCancellationRequested: false }) _pollCts.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    [ExcludeFromCodeCoverage]
    private async Task PollAsync(CancellationToken ct) {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        List<(Guid Id, bool IsTeam)> campaigns = GetSelectedCampaigns();
        if (campaigns.Count == 0) {
            logger?.LogInformation("[Tiltify] No campaigns selected, not polling");
            return;
        }

        logger?.LogInformation("[Tiltify] Polling {Count} campaign(s) for donations...", campaigns.Count);
        while (!ct.IsCancellationRequested) {
            foreach ((Guid id, bool isTeam) in campaigns)
                try {
                    await PollCampaignAsync(id, isTeam, _watermarks.GetValueOrDefault(id, startedAt), ct);
                }
                catch (OperationCanceledException) {
                    return;
                }
                catch (ApiException ex) when (ex.ResponseStatusCode == (int)HttpStatusCode.Unauthorized) {
                    logger?.LogWarning("[Tiltify] Unauthorized while polling, refreshing token");
                    if (await oAuth.RefreshAsync(OAuthKeys, ct)) continue;
                    await StopAsync(ct);
                    return;
                }
                catch (Exception ex) {
                    logger?.LogWarning(ex, "[Tiltify] Failed to poll campaign {Campaign}", id);
                }

            PruneSeen();
            try {
                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException) {
                return;
            }
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task PollCampaignAsync(Guid campaignId, bool isTeam, DateTimeOffset since, CancellationToken ct) {
        if (_client == null) return;
        var completedAfter = (since - PollOverlap).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        string? cursor = null;
        List<Donation> donations = [];

        for (var page = 0; page < MaxPagesPerPoll; page++) {
            string? pageCursor = cursor;
            RequestInformation req = isTeam
                ? _client.Api.Public.Team_campaigns[campaignId.ToString()].Donations.ToGetRequestInformation(r => {
                    r.QueryParameters.CompletedAfter = completedAfter;
                    r.QueryParameters.Limit = PageSize;
                    r.QueryParameters.After = pageCursor;
                })
                : _client.Api.Public.Campaigns[campaignId.ToString()].Donations.ToGetRequestInformation(r => {
                    r.QueryParameters.CompletedAfter = completedAfter;
                    r.QueryParameters.Limit = PageSize;
                    r.QueryParameters.After = pageCursor;
                });

            (List<Donation> items, string? next) = await GetPageAsync(req, Donation.CreateFromDiscriminatorValue, ct);
            donations.AddRange(items);
            if (items.Count < PageSize || string.IsNullOrWhiteSpace(next)) break;
            cursor = next;
        }

        foreach (Donation donation in donations.OrderBy(d => d.CompletedAt ?? DateTimeOffset.MinValue)) {
            if (donation.Id == null || donation.CompletedAt == null) continue;
            if (donation.CompletedAt > _watermarks.GetValueOrDefault(campaignId, since))
                _watermarks[campaignId] = donation.CompletedAt.Value;
            if (!_seen.TryAdd(donation.Id.Value, donation.CompletedAt.Value)) continue;
            HandleDonation(donation, campaignId);
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task<(List<T> Items, string? After)> GetPageAsync<T>(RequestInformation req,
        ParsableFactory<T> factory, CancellationToken ct) where T : IParsable {
        if (_adapter == null) return ([], null);
        await using var stream = await _adapter.SendPrimitiveAsync<Stream>(req, cancellationToken: ct);
        if (stream == null) return ([], null);

        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        List<T> items = doc.RootElement.TryGetProperty("data", out JsonElement data) &&
                        data.ValueKind == JsonValueKind.Array
            ? (await KiotaJsonSerializer.DeserializeCollectionAsync(data.GetRawText(), factory, ct)).ToList()
            : [];
        string? after = doc.RootElement.TryGetProperty("metadata", out JsonElement meta) &&
                        meta.TryGetProperty("after", out JsonElement a) && a.ValueKind == JsonValueKind.String
            ? a.GetString()
            : null;
        return (items, after);
    }

    private void PruneSeen() {
        if (_seen.Count == 0) return;
        DateTimeOffset cutoff = (_watermarks.Count > 0 ? _watermarks.Values.Min() : DateTimeOffset.UtcNow)
                                - PollOverlap - PollOverlap;
        foreach (Guid id in _seen.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
            _seen.Remove(id);
    }

    internal void HandleDonation(Donation donation, Guid? polledCampaignId = null) {
        try {
            if (!double.TryParse(donation.Amount?.Value, NumberStyles.Any, CultureInfo.InvariantCulture,
                    out double amount)) return;
            string? currency = donation.Amount?.Currency;

            var ev = new SubathonEvent {
                Id = donation.Id ?? Guid.NewGuid(),
                Source = SubathonEventSource.Tiltify,
                EventType = SubathonEventType.TiltifyDonation,
                User = !string.IsNullOrWhiteSpace(donation.DonorName) ? donation.DonorName : "Anonymous",
                Value = amount.ToString("F2", CultureInfo.InvariantCulture),
                Currency = !string.IsNullOrWhiteSpace(currency) ? currency : "USD",
                EventTimestamp = donation.CompletedAt?.LocalDateTime ?? DateTime.Now,
                EventTypeMeta = (donation.CampaignId ?? polledCampaignId)?.ToString(),
                TertiaryValue = GetCampaignName(donation.CampaignId) ?? GetCampaignName(polledCampaignId) ?? ""
            };

            SubathonEvents.RaiseSubathonEventCreated(ev);
            logger?.LogDebug("[Tiltify] Raised donation {Id} from {User}", ev.Id, ev.User);

            foreach (DonationMatch match in donation.DonationMatches ?? []) {
                if (match.Active != true || match.Id == null) continue;
                if (!double.TryParse(match.Amount?.Value, NumberStyles.Any, CultureInfo.InvariantCulture,
                        out double matchAmount) || matchAmount <= 0) continue;
                string? matchCurrency = match.Amount?.Currency;

                var matchEv = new SubathonEvent {
                    Id = match.Id.Value,
                    Source = SubathonEventSource.Tiltify,
                    EventType = SubathonEventType.TiltifyDonation,
                    User = !string.IsNullOrWhiteSpace(match.MatchedBy) ? match.MatchedBy : "Donation Match",
                    Value = matchAmount.ToString("F2", CultureInfo.InvariantCulture),
                    Currency = !string.IsNullOrWhiteSpace(matchCurrency) ? matchCurrency : ev.Currency,
                    EventTimestamp = match.CompletedAt?.LocalDateTime ?? ev.EventTimestamp,
                    EventTypeMeta = ev.EventTypeMeta,
                    TertiaryValue = ev.TertiaryValue
                };

                SubathonEvents.RaiseSubathonEventCreated(matchEv);
                logger?.LogDebug("[Tiltify] Raised donation match {Id} from {User} on donation {Donation}",
                    matchEv.Id, matchEv.User, ev.Id);
            }
        }
        catch (Exception ex) {
            logger?.LogWarning(ex, "[Tiltify] Failed to consume donation {Id}", donation.Id);
        }
    }

    public static void SimulateDonation(string amount, string currency, CampaignOption? campaign = null) {
        if (!double.TryParse(amount, NumberStyles.Any, CultureInfo.InvariantCulture, out double amt)) return;
        var ev = new SubathonEvent {
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.TiltifyDonation,
            User = "SYSTEM",
            Value = amt.ToString("F2", CultureInfo.InvariantCulture),
            Currency = !string.IsNullOrWhiteSpace(currency) ? currency : "USD",
            EventTimestamp = DateTime.Now,
            EventTypeMeta = campaign?.Id.ToString(),
            TertiaryValue = campaign?.Name ?? ""
        };
        SubathonEvents.RaiseSubathonEventCreated(ev);
    }

    public bool HasTokens() {
        return oAuth.HasTokens(OAuthKeys);
    }

    public void RevokeTokens() {
        oAuth.RevokeTokens(OAuthKeys);
    }

    private void BroadcastStatus(bool connected) {
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection {
            Source = SubathonEventSource.Tiltify,
            Service = nameof(SubathonEventSource.Tiltify),
            Name = connected ? _username ?? "User" : "",
            Status = connected,
            Configured = HasTokens()
        });
    }

    public record CampaignOption(Guid Id, string Name, bool IsTeam);

    [ExcludeFromCodeCoverage]
    private sealed class TiltifyBearerAuthProvider(Func<string?> tokenAccessor) : IAuthenticationProvider {
        public Task AuthenticateRequestAsync(RequestInformation request,
            Dictionary<string, object>? additionalAuthenticationContext = null,
            CancellationToken cancellationToken = default) {
            string? token = tokenAccessor();
            if (!string.IsNullOrWhiteSpace(token)) request.Headers.TryAdd("Authorization", $"Bearer {token}");
            return Task.CompletedTask;
        }
    }
}