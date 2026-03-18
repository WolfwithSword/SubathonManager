using System.Net;
using Moq;
using Moq.Protected;
using SubathonManager.Services;
using Microsoft.Extensions.Logging;
using System.Reflection;
using SubathonManager.Tests.Utility;

namespace SubathonManager.Tests.ServicesUnitTests;

[Collection("ServicesTests")]
public class CurrencyServiceTests
{
    private static string _usdRatesJson = """
                                     {
                                        "aud": {
                                            "code": "AUD",
                                            "alphaCode": "AUD",
                                            "numericCode": "036",
                                            "name": "Australian Dollar",
                                            "rate": 1.4054193131525,
                                            "date": "Fri, 27 Feb 2026 22:55:05 GMT",
                                            "inverseRate": 0.71153142029685
                                        },
                                        "cad": {
                                            "code": "CAD",
                                            "alphaCode": "CAD",
                                            "numericCode": "124",
                                            "name": "Canadian Dollar",
                                            "rate": 1.366083014878,
                                            "date": "Fri, 27 Feb 2026 22:55:05 GMT",
                                            "inverseRate": 0.73201993517888
                                        },
                                        "chf": {
                                            "code": "CHF",
                                            "alphaCode": "CHF",
                                            "numericCode": "756",
                                            "name": "Swiss Franc",
                                            "rate": 0.77144575655376,
                                            "date": "Fri, 27 Feb 2026 22:55:05 GMT",
                                            "inverseRate": 1.2962674193287
                                        },
                                        "eur": {
                                            "code": "EUR",
                                            "alphaCode": "EUR",
                                            "numericCode": "978",
                                            "name": "Euro",
                                            "rate": 0.84700640361988,
                                            "date": "Fri, 27 Feb 2026 22:55:05 GMT",
                                            "inverseRate": 1.1806286183036
                                        },
                                        "gbp": {
                                            "code": "GBP",
                                            "alphaCode": "GBP",
                                            "numericCode": "826",
                                            "name": "U.K. Pound Sterling",
                                            "rate": 0.74186273487082,
                                            "date": "Fri, 27 Feb 2026 22:55:05 GMT",
                                            "inverseRate": 1.3479582583079
                                        },
                                        "hkd": {
                                            "code": "HKD",
                                            "alphaCode": "HKD",
                                            "numericCode": "344",
                                            "name": "Hong Kong Dollar",
                                            "rate": 7.8234994342691,
                                            "date": "Fri, 27 Feb 2026 22:55:05 GMT",
                                            "inverseRate": 0.1278200386415
                                        },
                                        "jpy": {
                                            "code": "JPY",
                                            "alphaCode": "JPY",
                                            "numericCode": "392",
                                            "name": "Japanese Yen",
                                            "rate": 155.93449086661,
                                            "date": "Fri, 27 Feb 2026 22:55:05 GMT",
                                            "inverseRate": 0.0064129494022938
                                        },
                                        "krw": {
                                            "code": "KRW",
                                            "alphaCode": "KRW",
                                            "numericCode": "410",
                                            "name": "South Korean Won",
                                            "rate": 1438.7412702824,
                                            "date": "Fri, 27 Feb 2026 22:55:05 GMT",
                                            "inverseRate": 0.00069505200181246
                                        },
                                        "php": {
                                            "code": "PHP",
                                            "alphaCode": "PHP",
                                            "numericCode": "608",
                                            "name": "Philippine Peso",
                                            "rate": 57.627923058455,
                                            "date": "Fri, 27 Feb 2026 22:55:05 GMT",
                                            "inverseRate": 0.017352698950917
                                        },
                                        "twd": {
                                            "code": "TWD",
                                            "alphaCode": "TWD",
                                            "numericCode": "901",
                                            "name": "New Taiwan Dollar ",
                                            "rate": 31.2964165403,
                                            "date": "Fri, 27 Feb 2026 22:55:05 GMT",
                                            "inverseRate": 0.031952539956525
                                        },
                                        "vnd": {
                                            "code": "VND",
                                            "alphaCode": "VND",
                                            "numericCode": "704",
                                            "name": "Vietnamese Dong",
                                            "rate": 26050.631227597,
                                            "date": "Fri, 27 Feb 2026 22:55:05 GMT",
                                            "inverseRate": 0.000038386785765891
                                        }
                                    }
                                    """;
    
    private readonly MockWebServerHost _mockWebServerHost = new MockWebServerHost().OnGet("/daily/usd.json", _usdRatesJson);
    
    private CurrencyService CreateService(string jsonResponse)
    {
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(jsonResponse)
            });

        var httpClient = new HttpClient(handlerMock.Object);

        var loggerMock = new Mock<ILogger<CurrencyService>>();
        var mockConfig = MockConfig.MakeMockConfig(new()
        {
            { ("Currency", "Primary"), "USD" }
        });
        var service = new CurrencyService(loggerMock.Object, mockConfig, httpClient);
        service.BaseUrl = _mockWebServerHost.BaseUrl + "daily/";
        return service;
    }
    
    [Fact]
    public void Constructor_Uppercases_DefaultCurrency()
    {
        var mockConfig = MockConfig.MakeMockConfig(new()
        {
            { ("Currency", "Primary"), "usd" }
        });

        var service = new CurrencyService(null, mockConfig, new HttpClient());

        Assert.True(service.IsValidCurrency("USD"));
    }
    
    [Fact]
    public async Task ConvertAsync_ReturnsCorrectAmount()
    {
        string fakeJson = @"{
            ""usd"": {""code"": ""USD"", ""rate"": 1.0},
            ""eur"": {""code"": ""EUR"", ""rate"": 0.9}
        }";

        var service = CreateService(fakeJson);
        service.SetRates(new Dictionary<string, double> { { "USD", 1.0 }, { "EUR", 0.9 } });

        double result = await service.ConvertAsync(100, "USD", "EUR");

        Assert.Equal(90, result, precision: 2);
    }
    
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("USD", true)]
    [InlineData("eur", true)]
    [InlineData("jpy", false)]
    public void IsValidCurrency_Works_AsExpected(string? currency, bool expected)
    {
        string fakeJson = @"{
            ""usd"": {""code"": ""USD"", ""rate"": 1.0},
            ""eur"": {""code"": ""EUR"", ""rate"": 0.9}
        }";

        var service = CreateService(fakeJson);
        service.SetRates(new Dictionary<string, double> { { "USD", 1.0 }, { "EUR", 0.9 } });
        Assert.Equal(expected, service.IsValidCurrency(currency));
    }

    [Fact]
    public async Task ConvertAsync_SameCurrency_ReturnsSameAmount()
    {
        var mockConfig = MockConfig.MakeMockConfig(new()
        {
            { ("Currency", "Primary"), "USD" }
        });

        var service = new CurrencyService(null, mockConfig, new HttpClient());

        var result = await service.ConvertAsync(10, "USD", "USD");

        Assert.Equal(10, result);
    }

    [Fact]
    public void ParseRatesAsync_LoadsRates()
    {
        var json = """
                   {
                     "eur": { "code": "EUR", "rate": 0.9 },
                     "gbp": { "code": "GBP", "rate": 0.5 }
                   }
                   """;
        var mockConfig = MockConfig.MakeMockConfig(new()
        {
            { ("Currency", "Primary"), "USD" }
        });

        var service = new CurrencyService(null, mockConfig, new HttpClient());

        typeof(CurrencyService)
            .GetMethod("ParseRatesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, new object[] { json });

        Assert.True(service.IsValidCurrency("EUR"));
        Assert.True(service.IsValidCurrency("GBP"));
    }
    
    [Fact]
    public async Task FetchBaseAsync_UpdatesRatesAndPreventsConcurrentFetch()
    {
        string json = @"{ ""usd"": { ""code"": ""USD"", ""rate"": 1.0 }, ""eur"": { ""code"": ""EUR"", ""rate"": 0.9 } }";

        var service = CreateService(json);

        string tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        typeof(CurrencyService)
            .GetField("_dataDirectory", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, tempDir);

        // in parallel
        await Task.WhenAll(service.GetValidCurrenciesAsync(), service.GetValidCurrenciesAsync());

        Assert.True(service.IsValidCurrency("USD"));
        Assert.True(service.IsValidCurrency("EUR"));

        Directory.Delete(tempDir, true);
    }
    
    [Fact]
    public async Task StartAsync_LoadsFromFile_WhenFileExists()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "usd.json");
        await File.WriteAllTextAsync(tempPath, """
                                               { "usd": { "code": "USD", "rate": 1.0 } }
                                               """);

        var service = CreateService("{}");
        typeof(CurrencyService).GetField("_dataDirectory", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, Path.GetTempPath());

        await service.StartAsync();

        Assert.True(service.IsValidCurrency("USD"));
        File.Delete(tempPath);
        await service.StopAsync();
    }
    
    [Fact]
    public async Task ConvertAsync_Fails_ForUnknownCurrency()
    {
        var service = CreateService("{}");
        Assert.Equal(0, await service.ConvertAsync(10, "ABC", "USD"));
    }
    
    [Fact]
    public void IsExpired_ReturnsTrue_WhenFileOld()
    {
        var service =  CreateService("{}");

        string tempFile = Path.Combine(Path.GetTempPath(), "usd.json");
        File.WriteAllText(tempFile, "{ \"usd\": { \"code\": \"USD\", \"rate\": 1.0 } }");
        File.SetLastWriteTimeUtc(tempFile, DateTime.UtcNow - TimeSpan.FromDays(2));

        typeof(CurrencyService).GetField("_dataDirectory", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, Path.GetTempPath());

        var method = typeof(CurrencyService).GetMethod("IsExpired", BindingFlags.NonPublic | BindingFlags.Instance)!;
        bool expired = (bool)method.Invoke(service, null)!;

        Assert.True(expired);
        File.Delete(tempFile);
    }
    
    [Fact]
    public async Task GetValidCurrenciesAsync_IncludesDefaultCurrency()
    {
        var service = CreateService("{}");
        var list = await service.GetValidCurrenciesAsync();
        Assert.Contains("USD", list);
    }

}