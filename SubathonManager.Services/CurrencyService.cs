using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;

namespace SubathonManager.Services;

public class CurrencyService
{
    private string _baseUrl = "http://www.floatrates.com/daily/";
    private string DefaultCurrency { get; set; }
    
    private readonly HttpClient _httpClient = new();
    private readonly TimeSpan _refreshInterval = TimeSpan.FromHours(24);

    private Dictionary<string, double> _rates = new();
    private DateTime _lastUpdated = DateTime.MinValue;

    private string _dataDirectory = Path.GetFullPath(Path.Combine(string.Empty
        , "data/currency"));

    private string _currencyFile;

    private readonly ILogger? _logger;
    private readonly IConfig _config;

    public CurrencyService(ILogger<CurrencyService>? logger, IConfig config)
    {
        _logger = logger;
        _config = config;
        DefaultCurrency = _config.Get("Currency", "Primary", "USD")!.ToUpperInvariant().Trim();
        Directory.CreateDirectory(_dataDirectory);
        _currencyFile = Path.Combine(_dataDirectory, $"{DefaultCurrency.ToLowerInvariant().Trim()}.json");
    }

    public async Task StartAsync()
    {
        DefaultCurrency = _config.Get("Currency", "Primary", "USD")!.ToUpperInvariant().Trim();
        _currencyFile = Path.Combine(_dataDirectory, $"{DefaultCurrency.ToLowerInvariant().Trim()}.json");
        if (File.Exists(_currencyFile))
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
        
        if (_rates.Count == 0)
            throw new InvalidOperationException("No exchange rates available (failed to load or fetch).");
    }
    
    public async Task<List<string>> GetValidCurrenciesAsync()
    {
        if (_rates.Count == 0)
            await FetchBaseAsync();
        
        var currencies = _rates.Keys.ToList();
        currencies.Add(DefaultCurrency);
        return currencies;
    }
    
    public bool IsValidCurrency(string? currency)
    {   
        if (string.IsNullOrWhiteSpace(currency))
            return false;

        currency = currency.ToUpperInvariant().Trim();

        return currency == DefaultCurrency || _rates.ContainsKey(currency);
    }
    
    private bool IsExpired() => DateTime.UtcNow - _lastUpdated > _refreshInterval;
    
    private async Task FetchBaseAsync()
    {
        string url = _baseUrl + $"{DefaultCurrency.ToLowerInvariant().Trim()}.json";
        string json = await _httpClient.GetStringAsync(url);
        await File.WriteAllTextAsync(_currencyFile, json);
        ParseRatesAsync(json);
        
    }

    private async Task LoadFromFileAsync()
    {
        string json = await File.ReadAllTextAsync(_currencyFile);
        ParseRatesAsync(json);
    }
    
    private void ParseRatesAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        _rates.Clear();

        foreach (var kvp in root.EnumerateObject())
        {
            var item = kvp.Value;
            if (item.TryGetProperty("code", out var codeProp) &&
                item.TryGetProperty("rate", out var rateProp))
            {
                string code = codeProp.GetString()!.ToUpperInvariant();
                double rate = rateProp.GetDouble();
                _rates[code] = rate;
            }
        }

        if (root.EnumerateObject().Any())
        {
            _lastUpdated = File.Exists(_currencyFile)
                ? File.GetLastWriteTimeUtc(_currencyFile)
                : DateTime.UtcNow;
        }
        else
        {
            _lastUpdated = DateTime.UtcNow;
        }
    }
    
    public async Task<double> ConvertAsync(double amount, string fromCurrency, string? toCurrency = null)
    {
        fromCurrency = fromCurrency.ToUpperInvariant().Trim();
        toCurrency ??= DefaultCurrency;
        if (fromCurrency == toCurrency)
            return amount;
        
        if (IsExpired())
        {
            try
            {
                await FetchBaseAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to refresh rates, using cached data");
            }
        }

        if (!_rates.TryGetValue(fromCurrency, out var fromRate))
            throw new InvalidOperationException($"Rate for {fromCurrency} not found.");

        double baseAmount = amount / fromRate;

        if (toCurrency == DefaultCurrency)
            return baseAmount;

        if (!_rates.TryGetValue(toCurrency, out var toRate))
            throw new InvalidOperationException($"Rate for {toCurrency} not found.");

        return baseAmount * toRate;
    }
}