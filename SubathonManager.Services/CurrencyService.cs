using System.Text.Json;
using SubathonManager.Core.Events;
using SubathonManager.Core.Enums;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Interfaces;

namespace SubathonManager.Services;

public class CurrencyService : IAppService
{
    private string _baseUrl = "http://www.floatrates.com/daily/";

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _refreshInterval = TimeSpan.FromHours(24);

    internal Dictionary<string, double> Rates = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string _dataDirectory = Path.GetFullPath(Path.Combine(string.Empty
        , "data/currency"));

    private readonly ILogger? _logger;
    private readonly IConfig _config;

    public CurrencyService(ILogger<CurrencyService>? logger, IConfig config, HttpClient httpClient)
    {
        _logger = logger;
        _config = config;
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Directory.CreateDirectory(_dataDirectory);
    }

    private string CurrencyFilePath()
    {
        var defaultCurrency = _config.Get("Currency", "Primary", "USD")!.ToUpperInvariant().Trim();
        string currencyFile = Path.Combine(_dataDirectory, $"{defaultCurrency.ToLowerInvariant().Trim()}.json");
        return currencyFile;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var currencyFilePath = CurrencyFilePath();
        if (File.Exists(currencyFilePath))
        {
            try
            {
                await LoadFromFileAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load cached rates from file");
            }
        }
        if (IsExpired())
        {
            try
            {
                await FetchBaseAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to fetch new rates");
            }
        }

        if (Rates.Count == 0)
        {
            _logger?.LogError("No exchange rates available (failed to load or fetch). CurrencyService will remain available but conversions may fail.");
            ErrorMessageEvents.RaiseErrorEvent("ERROR", "SYSTEM",
                "Could not fetch exchange rates for Currency Service. Failures may occur.", DateTime.Now);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
    
    public async Task<List<string>> GetValidCurrenciesAsync()
    {
        if (Rates.Count == 0)
            await FetchBaseAsync();
        var defaultCurrency = _config.Get("Currency", "Primary", "USD")!.ToUpperInvariant().Trim();
        var currencies = Rates.Keys.ToList();
        currencies.Add(defaultCurrency);
        return currencies;
    }
    
    public bool IsValidCurrency(string? currency)
    {   
        if (string.IsNullOrWhiteSpace(currency))
            return false;

        currency = currency.ToUpperInvariant().Trim();

        var defaultCurrency = _config.Get("Currency", "Primary", "USD")!.ToUpperInvariant().Trim();
        return currency == defaultCurrency || Rates.ContainsKey(currency);
    }
    
    private bool IsExpired() {
        var currencyFilePath = CurrencyFilePath();
        if (!File.Exists(currencyFilePath)) return true;
        var lastUpdated = File.GetLastWriteTimeUtc(currencyFilePath);
        return lastUpdated < DateTime.UtcNow - _refreshInterval;
    }
    
    private async Task FetchBaseAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            if (!IsExpired())
                return;
            var defaultCurrency = _config
                .Get("Currency", "Primary", "USD")!
                .ToUpperInvariant()
                .Trim();

            string url = _baseUrl + $"{defaultCurrency.ToLowerInvariant()}.json";
            string json = await _httpClient.GetStringAsync(url);

            var path = CurrencyFilePath();
            await File.WriteAllTextAsync(path, json);

            ParseRatesAsync(json);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task LoadFromFileAsync()
    {
        var currencyFilePath = CurrencyFilePath();
        string json = await File.ReadAllTextAsync(currencyFilePath);
        ParseRatesAsync(json);
    }
    
    private void ParseRatesAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Rates.Clear();

        foreach (var kvp in root.EnumerateObject())
        {
            var item = kvp.Value;
            if (item.TryGetProperty("code", out var codeProp) &&
                item.TryGetProperty("rate", out var rateProp))
            {
                string code = codeProp.GetString()!.ToUpperInvariant();
                double rate = rateProp.GetDouble();
                Rates[code] = rate;
            }
        }
    }
    
    public async Task<double> ConvertAsync(double amount, string fromCurrency, string? toCurrency = null)
    {
        fromCurrency = fromCurrency.ToUpperInvariant().Trim();
        var defaultCurrency = _config.Get("Currency", "Primary", "USD")!.ToUpperInvariant().Trim();
        toCurrency ??=  defaultCurrency;
        if (fromCurrency == toCurrency)
            return amount;

        try
        {
            await FetchBaseAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to refresh rates, using cached data");
        }

        if (!IsValidCurrency(fromCurrency))
        {
            var message = fromCurrency + " is not a valid currency. Cannot convert";
            _logger?.LogError(message);
            
            ErrorMessageEvents.RaiseErrorEvent("ERROR", nameof(SubathonEventSource.Twitch), 
                message, DateTime.Now);
            return 0;
        }

        try
        {
            if (!Rates.TryGetValue(fromCurrency, out var fromRate))
                throw new InvalidOperationException($"Rate for {fromCurrency} not found.");

            double baseAmount = amount / fromRate;

            if (toCurrency == defaultCurrency)
                return baseAmount;

            if (!Rates.TryGetValue(toCurrency, out var toRate))
                throw new InvalidOperationException($"Rate for {toCurrency} not found.");

            return baseAmount * toRate;
        }
        catch (Exception ex)
        {
            var message = $"Failed to convert {amount} {fromCurrency} to {toCurrency}";
            _logger?.LogError(ex, message);
            ErrorMessageEvents.RaiseErrorEvent("ERROR", nameof(SubathonEventSource.Twitch), 
                message, DateTime.Now);
        }

        return 0;
    }
    
    internal void SetRates(Dictionary<string, double> rates)
    {
        Rates = rates;
    }
    
}