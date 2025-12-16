using System.Net;
using Moq;
using Moq.Protected;
using SubathonManager.Services;
using Microsoft.Extensions.Logging;
using System.Reflection;
using IniParser.Model;
namespace SubathonManager.Tests.ServicesUnitTests;

public class CurrencyServiceTests
{
    private static IConfig MockConfig(Dictionary<(string, string), string>? values = null)
    {
        var mock = new Mock<IConfig>();
        mock.Setup(c => c.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string s, string k, string d) =>
                values != null && values.TryGetValue((s, k), out var v) ? v : d);
            
        var kd = new KeyData("Primary");
        kd.Value = "USD";
        mock.Setup(c => c.GetSection("Currency")).Returns(() =>
        {
            var kdc = new KeyDataCollection();
            kdc.AddKey(kd);
            return kdc;
        });
            
        return mock.Object;
    }
    
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
        var mockConfig = MockConfig(new()
        {
            { ("Currency", "Primary"), "USD" }
        });
        return new CurrencyService(loggerMock.Object, mockConfig, httpClient);
    }
    
    [Fact]
    public void Constructor_Uppercases_DefaultCurrency()
    {
        var config = new Mock<IConfig>();
        config.Setup(c => c.Get("Currency", "Primary", "USD")).Returns("usd");

        var service = new CurrencyService(null, config.Object, new HttpClient());

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
        var config = new Mock<IConfig>();
        config.Setup(c => c.Get("Currency", "Primary", "USD")).Returns("USD");

        var service = new CurrencyService(null, config.Object, new HttpClient());

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

        var config = new Mock<IConfig>();
        config.Setup(c => c.Get("Currency", "Primary", "USD")).Returns("USD");

        var service = new CurrencyService(null, config.Object, new HttpClient());

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

        var config = new Mock<IConfig>();
        config.Setup(c => c.Get("Currency", "Primary", "USD")).Returns("USD");

        var service = new CurrencyService(null, config.Object);
        typeof(CurrencyService).GetField("_dataDirectory", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, Path.GetTempPath());

        await service.StartAsync();

        Assert.True(service.IsValidCurrency("USD"));
        File.Delete(tempPath);
    }
    
    [Fact]
    public async Task ConvertAsync_Throws_ForUnknownCurrency()
    {
        var service = CreateService("{}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConvertAsync(10, "ABC", "USD"));
    }
    
    [Fact]
    public void IsExpired_ReturnsTrue_WhenFileOld()
    {
        var config = new Mock<IConfig>();
        config.Setup(c => c.Get("Currency", "Primary", "USD")).Returns("USD");

        var service = new CurrencyService(null, config.Object);

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