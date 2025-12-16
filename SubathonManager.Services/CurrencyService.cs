using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;

namespace SubathonManager.Services;

public class CurrencyService
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

    public CurrencyService(ILogger<CurrencyService>? logger, IConfig config, 
        HttpClient? httpClient = null)
    {
        _logger = logger;
        _config = config;
        _httpClient = httpClient ?? new HttpClient();
        Directory.CreateDirectory(_dataDirectory);
    }

    private string CurrencyFilePath()
    {
        var defaultCurrency = _config.Get("Currency", "Primary", "USD")!.ToUpperInvariant().Trim();
        string currencyFile = Path.Combine(_dataDirectory, $"{defaultCurrency.ToLowerInvariant().Trim()}.json");
        return currencyFile;
    }

    public async Task StartAsync()
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
            throw new InvalidOperationException("No exchange rates available (failed to load or fetch).");
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

        if (!Rates.TryGetValue(fromCurrency, out var fromRate))
            throw new InvalidOperationException($"Rate for {fromCurrency} not found.");

        double baseAmount = amount / fromRate;

        if (toCurrency == defaultCurrency)
            return baseAmount;

        if (!Rates.TryGetValue(toCurrency, out var toRate))
            throw new InvalidOperationException($"Rate for {toCurrency} not found.");

        return baseAmount * toRate;
    }
    
    internal void SetRates(Dictionary<string, double> rates)
    {
        Rates = rates;
    }
}