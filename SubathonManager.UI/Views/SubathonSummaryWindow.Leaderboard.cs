using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Web;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using SubathonManager.UI.UiUtils;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views;

public partial class SubathonSummaryWindow {
    private static readonly string[] LeaderboardMethods = [
        "(default for type)", "ByPoints", "ByCount", "ByValue", "ByAmount", "ByOrder", "ByItems"
    ];

    private static readonly HttpClient LeaderboardClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly ObservableCollection<LeaderboardRow> _lbRows = [];
    private string _lbUrl = string.Empty;

    private void InitLeaderboardTab() {
        LbTypePopout.EmptyText = "Pick at least one event type";
        LbTypePopout.SetOptions(BuildTypeOptions());

        LbMethodBox.ItemsSource = LeaderboardMethods;
        LbMethodBox.SelectedIndex = 0;
    }

    private string? BuildLeaderboardUrl() {
        List<string> selected = LbTypePopout.SelectedOptions.Select(t => t.Value).ToList();
        if (selected.Count == 0) return null;

        var query = new StringBuilder();
        query.Append("type=").Append(HttpUtility.UrlEncode(string.Join(',', selected)));

        string method = LbMethodBox.SelectedItem as string ?? LeaderboardMethods[0];
        if (method != LeaderboardMethods[0]) Append(query, "method", method);

        Append(query, "top", LbTopBox.Text);
        if (selected.Count == 1) Append(query, "meta", LbMetaBox.Text);
        Append(query, "blacklist", LbBlacklistBox.Text);
        Append(query, "alias", LbAliasBox.Text);
        if (PinSubathonId(LbIncludeSubathonBox)) Append(query, "subathon", Selected!.Id.ToString());

        return $"{ApiBaseUrl()}/api/data/leaderboard?{query}";
    }

    private static void Append(StringBuilder query, string key, string? value) {
        if (string.IsNullOrWhiteSpace(value)) return;
        query.Append('&').Append(key).Append('=').Append(HttpUtility.UrlEncode(value.Trim()));
    }

    private async void LbRun_Click(object? sender, RoutedEventArgs e) {
        _lbRows.Clear();
        LbRawBox.Text = string.Empty;

        string? url = BuildLeaderboardUrl();
        if (url == null) {
            LbStatus.Text = "Pick at least one event type first";
            SetLeaderboardUrl(string.Empty);
            return;
        }

        SetLeaderboardUrl(url);
        LbStatus.Text = "Querying...";

        string body;
        bool ok;
        try {
            HttpResponseMessage response = await LeaderboardClient.GetAsync(url);
            body = await response.Content.ReadAsStringAsync();
            ok = response.IsSuccessStatusCode;
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[Summary] Leaderboard request failed");
            LbStatus.Text = "Could not reach the web server. Is it running? (Settings -> Port)";
            return;
        }

        LbRawBox.Text = body;

        if (!ok) {
            LbStatus.Text = TryReadError(body) ?? "The request was rejected.";
            return;
        }

        try {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            foreach (JsonElement item in root.GetProperty("results").EnumerateArray())
                _lbRows.Add(new LeaderboardRow {
                    Rank = item.GetProperty("rank").GetInt32(),
                    User = item.GetProperty("user").GetString() ?? "",
                    Value = item.GetProperty("value").GetDouble().ToString("N2", CultureInfo.CurrentCulture),
                    Events = item.GetProperty("events").GetInt32(),
                    Count = item.GetProperty("count").GetInt64()
                });

            string unit = root.TryGetProperty("unit", out JsonElement unitEl) ? unitEl.GetString() ?? "" : "";
            double total = root.TryGetProperty("total", out JsonElement totalEl) ? totalEl.GetDouble() : 0;
            int users = root.TryGetProperty("user_count", out JsonElement usersEl) ? usersEl.GetInt32() : 0;

            LbExportBtn.IsEnabled = _lbRows.Count > 0;
            LbStatus.Text = $"{_lbRows.Count} shown of {users} users · " +
                            $"total {total.ToString("N2", CultureInfo.CurrentCulture)} {unit}";
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[Summary] Could not read leaderboard response");
            LbStatus.Text = "The response could not be read, see the raw JSON below";
        }
    }

    private static string? TryReadError(string body) {
        try {
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out JsonElement error) ? error.GetString() : null;
        }
        catch {
            return null;
        }
    }

    private void SetLeaderboardUrl(string url) {
        _lbUrl = url;
        LbUrlBox.Text = url;
        LbCopyUrlBtn.IsEnabled = url.Length > 0;
        LbOpenBtn.IsEnabled = url.Length > 0;
        LbExportBtn.IsEnabled = false;
    }

    private async void LbCopyUrl_Click(object? sender, RoutedEventArgs e) {
        if (_lbUrl.Length == 0) return;
        bool copied = await UiHelpers.TrySetClipboardTextAsync(_lbUrl);
        LbStatus.Text = copied ? "URL copied to clipboard" : "Could not access the clipboard";
    }

    private void LbOpen_Click(object? sender, RoutedEventArgs e) {
        if (_lbUrl.Length == 0) return;
        try {
            Process.Start(new ProcessStartInfo { FileName = _lbUrl, UseShellExecute = true });
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[Summary] Could not open leaderboard URL");
            LbStatus.Text = "Could not open browser";
        }
    }

    public sealed class LeaderboardRow {
        public int Rank { get; init; }
        public string User { get; init; } = "";
        public string Value { get; init; } = "";
        public int Events { get; init; }
        public long Count { get; init; }
    }
}
