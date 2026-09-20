using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;

namespace SubathonManager.Services;

public class CurrencyService : IAppService {
    private readonly IConfig _config;

    private readonly HttpClient _httpClient;

    private readonly ILogger? _logger;
    private readonly TimeSpan _refreshInterval = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private readonly string _dataDirectory = Path.GetFullPath(Path.Combine(string.Empty
        , "data/currency"));

    internal string BaseUrl = "http://www.floatrates.com/daily/";

    internal Dictionary<string, double> Rates = new();

    public CurrencyService(ILogger<CurrencyService>? logger, IConfig config, HttpClient httpClient) {
        _logger = logger;
        _config = config;
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Directory.CreateDirectory(_dataDirectory);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default) {
        string currencyFilePath = CurrencyFilePath();
        if (File.Exists(currencyFilePath))
            try {
                await LoadFromFileAsync();
            }
            catch (Exception ex) {
                _logger?.LogError(ex, "Failed to load cached rates from file");
            }

        if (IsExpired())
            try {
                await FetchBaseAsync();
            }
            catch (Exception ex) {
                _logger?.LogError(ex, "Failed to fetch new rates");
            }

        if (Rates.Count == 0) {
            _logger?.LogError(
                "No exchange rates available (failed to load or fetch). CurrencyService will remain available but conversions may fail.");
            ErrorMessageEvents.RaiseErrorEvent("ERROR", "SYSTEM",
                "Could not fetch exchange rates for Currency Service. Failures may occur.", DateTime.Now);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default) {
        return Task.CompletedTask;
    }

    private string CurrencyFilePath() {
        string defaultCurrency = _config.Get("Currency", "Primary", "USD")!.ToUpperInvariant().Trim();
        string currencyFile = Path.Combine(_dataDirectory, $"{defaultCurrency.ToLowerInvariant().Trim()}.json");
        return currencyFile;
    }

    public async Task<List<string>> GetValidCurrenciesAsync() {
        if (Rates.Count == 0)
            await FetchBaseAsync();
        string defaultCurrency = _config.Get("Currency", "Primary", "USD")!.ToUpperInvariant().Trim();
        List<string> currencies = Rates.Keys.ToList();
        currencies.Add(defaultCurrency);
        return currencies;
    }

    public bool IsValidCurrency(string? currency) {
        if (string.IsNullOrWhiteSpace(currency))
            return false;

        currency = currency.ToUpperInvariant().Trim();

        string defaultCurrency = _config.Get("Currency", "Primary", "USD")!.ToUpperInvariant().Trim();
        return currency == defaultCurrency || Rates.ContainsKey(currency);
    }

    private bool IsExpired() {
        string currencyFilePath = CurrencyFilePath();
        if (!File.Exists(currencyFilePath)) return true;
        DateTime lastUpdated = File.GetLastWriteTimeUtc(currencyFilePath);
        return lastUpdated < DateTime.UtcNow - _refreshInterval;
    }

    private async Task FetchBaseAsync() {
        await _refreshLock.WaitAsync();
        try {
            if (!IsExpired())
                return;
            string defaultCurrency = _config
                .Get("Currency", "Primary", "USD")!
                .ToUpperInvariant()
                .Trim();

            string url = BaseUrl + $"{defaultCurrency.ToLowerInvariant()}.json";
            string json = await _httpClient.GetStringAsync(url);

            string path = CurrencyFilePath();
            await File.WriteAllTextAsync(path, json);

            ParseRatesAsync(json);
        }
        finally {
            _refreshLock.Release();
        }
    }

    private async Task LoadFromFileAsync() {
        string currencyFilePath = CurrencyFilePath();
        string json = await File.ReadAllTextAsync(currencyFilePath);
        ParseRatesAsync(json);
    }

    private void ParseRatesAsync(string json) {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Rates.Clear();

        foreach (JsonElement item in root.EnumerateObject().Select(kvp => kvp.Value)) {
            if (!item.TryGetProperty("code", out JsonElement codeProp) ||
                !item.TryGetProperty("rate", out JsonElement rateProp)) continue;
            string code = codeProp.GetString()!.ToUpperInvariant();
            double rate = rateProp.ValueKind == JsonValueKind.String
                ? double.Parse(rateProp.GetString()!, CultureInfo.InvariantCulture)
                : rateProp.GetDouble();
            Rates[code] = rate;
        }
    }

    public async Task<double> ConvertAsync(double amount, string fromCurrency, string? toCurrency = null) {
        fromCurrency = fromCurrency.ToUpperInvariant().Trim();
        string defaultCurrency = _config.Get("Currency", "Primary", "USD")!.ToUpperInvariant().Trim();
        toCurrency = string.IsNullOrWhiteSpace(toCurrency)
            ? defaultCurrency
            : toCurrency.ToUpperInvariant().Trim();
        if (fromCurrency == toCurrency)
            return amount;

        try {
            await FetchBaseAsync();
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "Failed to refresh rates, using cached data");
        }

        if (!IsValidCurrency(fromCurrency)) {
            var message = $"{fromCurrency} is not a valid currency. Cannot convert {amount}";
            if (fromCurrency.ToUpperInvariant() is "ITEMS" or "ORDER" or "MEMBER") {
                _logger?.LogDebug(message);
                return 0;
            }

            _logger?.LogError(message);

            ErrorMessageEvents.RaiseErrorEvent("ERROR", "CurrencyService",
                message, DateTime.Now);
            return 0;
        }

        if (!IsValidCurrency(toCurrency)) {
            var message = $"{toCurrency} is not a valid target currency. Cannot convert {amount} {fromCurrency}";
            _logger?.LogError(message);

            ErrorMessageEvents.RaiseErrorEvent("ERROR", "CurrencyService",
                message, DateTime.Now);
            return 0;
        }

        try {
            var fromRate = 1.0;
            if (fromCurrency != defaultCurrency && !Rates.TryGetValue(fromCurrency, out fromRate))
                throw new InvalidOperationException($"Rate for {fromCurrency} not found.");

            double baseAmount = amount / fromRate;

            if (toCurrency == defaultCurrency)
                return baseAmount;

            if (!Rates.TryGetValue(toCurrency, out double toRate))
                throw new InvalidOperationException($"Rate for {toCurrency} not found.");

            return baseAmount * toRate;
        }
        catch (Exception ex) {
            var message = $"Failed to convert {amount} {fromCurrency} to {toCurrency}";
            _logger?.LogError(ex, message);
            ErrorMessageEvents.RaiseErrorEvent("ERROR", "CurrencyService",
                message, DateTime.Now);
        }

        return 0;
    }

    internal void SetRates(Dictionary<string, double> rates) {
        Rates = rates;
    }
}