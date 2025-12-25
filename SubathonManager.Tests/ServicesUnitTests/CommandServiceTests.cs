using System.Net;
using Moq;
using Moq.Protected;
using SubathonManager.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using IniParser.Model;
using SubathonManager.Core.Enums;

namespace SubathonManager.Tests.ServicesUnitTests;

public class CommandServiceTests
{
    public CommandServiceTests()
    {
        CommandService.SetConfig(null!);
        AppServices.Provider = null!;
    }
    private static void SetupServices()
    {
        CurrencyService CreateCurrencyService(string jsonResponse)
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
          
        string fakeJson = @"{
            ""usd"": {""code"": ""USD"", ""rate"": 1.0},
            ""gbp"": {""code"": ""GBP"", ""rate"": 0.9},
            ""cad"": {""code"": ""GBP"", ""rate"": 0.8},
            ""twd"": {""code"": ""GBP"", ""rate"": 0.7},
            ""aud"": {""code"": ""GBP"", ""rate"": 0.6},
        }";
        var services = new ServiceCollection();
        var currencyMock = CreateCurrencyService(fakeJson);

        currencyMock.SetRates(new Dictionary<string, double>
        {
            { "USD", 1.0 }, { "GBP", 0.9 }, {"CAD", 0.8}, {"TWD", 0.6}, {"AUD", 0.5}
        });
        
        services.AddSingleton(currencyMock);
        AppServices.Provider = services.BuildServiceProvider();
    }
    
    
    private static IConfig MockConfig(Dictionary<(string, string), string>? values = null)
    {
        var mock = new Mock<IConfig>();
        
        mock.Setup(c => c.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string s, string k, string d) =>
                values != null && values.TryGetValue((s, k), out var v) ? v : d);
            
        var kd0 = new KeyData("Primary");
        kd0.Value = "USD";
        mock.Setup(c => c.GetSection("Currency")).Returns(() =>
        {
            var kdc = new KeyDataCollection();
            kdc.AddKey(kd0);
            return kdc;
        });
        
        KeyDataCollection keyData = new KeyDataCollection();
        foreach (var cmd in Enum.GetNames<SubathonCommandType>())
        {
            mock.Setup(c => c.Get("Chat", $"Commands.{cmd}.name", It.IsAny<string>()))
                .Returns(cmd.ToLower());
            mock.Setup(c => c.Get("Chat", $"Commands.{cmd}.permissions.Mods", It.IsAny<string>()))
                .Returns("True");
            mock.Setup(c => c.Get("Chat", $"Commands.{cmd}.permissions.VIPs", It.IsAny<string>()))
                .Returns("False");
            mock.Setup(c => c.Get("Chat", $"Commands.{cmd}.permissions.Whitelist", It.IsAny<string>()))
                .Returns("speciallittleguy");
            KeyData kd = new KeyData($"Commands.{cmd}.name");
            kd.Value = cmd.ToLower();
            keyData.AddKey(kd);
            
            KeyData kd2 = new KeyData($"Commands.{cmd}.permissions.Mods");
            kd2.Value = "True";
            keyData.AddKey(kd2);
            
            KeyData kd3 = new KeyData($"Commands.{cmd}.permissions.VIPs");
            kd3.Value = "False";
            keyData.AddKey(kd3);
            
            KeyData kd4 = new KeyData($"Commands.{cmd}.permissions.Whitelist");
            kd4.Value = "speciallittleguy";
            keyData.AddKey(kd4);
        }

        mock.Setup(c => c.GetSection("Chat")).Returns(() => keyData);
        return mock.Object;
    }
    
    [Theory]
    [InlineData(null, null, false, false, false, "jimmy",  null, false)]
    [InlineData("pause", "", true, false, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("pause", "", false, true, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("resume", "", true, false, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("resume", "", false, true, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("addpoints", "5", false, true, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("addpoints", "5", true, false, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("addpoints", "5", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, true)]
    [InlineData("subtractpoints", "5", false, true, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("subtractpoints", "5", true, false, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("subtractpoints", "5", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, true)]
    [InlineData("setpoints", "50", false, true, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("setpoints", "50", true, false, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("setpoints", "50", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, true)]
    [InlineData("addtime", "50s", false, true, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("addtime", "5h5m", true, false, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("addtime", "10s", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, true)]
    [InlineData("addtime", "50", false, true, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("addtime", "5:05:00", true, false, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("addtime", "00:10", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, true)]
    [InlineData("subtracttime", "50s", false, true, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("subtracttime", "5h 5m", true, false, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("subtracttime", "10s", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, true)]
    [InlineData("settime", "50h", false, true, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("settime", "5h5m", true, false, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("settime", "10h", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, true)]
    [InlineData("lock", "", false, true, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("lock", "", true, false, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("lock", "", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, true)]
    [InlineData("unlock", "", false, true, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("unlock", "", true, false, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("unlock", "", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, true)]
    [InlineData("setmultiplier", "2xp 1h", false, true, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("setmultiplier", "2.5xpt", true, false, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("setmultiplier", "3xt 5h", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, true)]
    [InlineData("stopmultiplier", "", false, true, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("stopmultiplier", "", true, false, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("stopmultiplier", "", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, true)]
    [InlineData("refreshoverlays", "", false, true, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("refreshoverlays", "", true, false, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("refreshoverlays", "", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, true)]
    [InlineData("addmoney", "5 CAD", false, true, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("addmoney", "10.55 USD", true, false, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("addmoney", "50 TWD", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, true)]
    [InlineData("subtractmoney", "5 CAD", false, true, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("subtractmoney", "10.55 GBP", true, false, false, "John",  SubathonEventSource.Twitch, true)]
    [InlineData("subtractmoney", "50.77 AUD", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, true)]
    public void IsValidCommand_Works_HappyPaths(string? command, string? message, bool isBroadcaster, 
        bool isMod, bool isVip,
        string name, SubathonEventSource? source,
        bool? expected)
    {
        SetupServices();
        CommandService.SetConfig(MockConfig());
        bool outcome = CommandService.ChatCommandRequest(source ?? SubathonEventSource.Unknown, $"!{command} {message}".Trim(), 
            name, isBroadcaster, isMod, isVip, DateTime.Now);
        Assert.Equal(expected, outcome);
        
        CommandService.SetConfig(null!);
        AppServices.Provider = null!;
    }
    
    [Theory]
    [InlineData(null, null, false, false, false, "jimmy",  null, false)]
    [InlineData("pause", "", false, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("pause", "", false, false, false, "John",  SubathonEventSource.YouTube, false)]
    [InlineData("resume", "", false, false, false, "John",  SubathonEventSource.YouTube, false)]
    [InlineData("addpoints", "5", false, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("addpoints", "5", false, false, false, "jimmy",  SubathonEventSource.YouTube, false)]
    [InlineData("subtractpoints", "5", false, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("subtractpoints", "5", false, false, false, "jimmy",  SubathonEventSource.Twitch, false)]
    [InlineData("setpoints", "50", false, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("setpoints", "50", false, false, false, "jimmy",  SubathonEventSource.Twitch, false)]
    [InlineData("addtime", "50s", false, false, false, "John",  SubathonEventSource.YouTube, false)]
    [InlineData("addtime", "5h5m", false, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("addtime", "10s", false, false, false, "jimmy",  SubathonEventSource.Twitch, false)]
    [InlineData("addtime", "50", false, false, false, "John",  SubathonEventSource.YouTube, false)]
    [InlineData("addtime", "5:05:00", false, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("addtime", "00:10", false, false, false, "jimmy",  SubathonEventSource.YouTube, false)]
    [InlineData("subtracttime", "50s", false, false, false, "John",  SubathonEventSource.YouTube, false)]
    [InlineData("subtracttime", "5h5m", false, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("subtracttime", "10s", false, false, false, "jimmy",  SubathonEventSource.Twitch, false)]
    [InlineData("settime", "50h", false, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("settime", "5h5m", false, false, false, "John",  SubathonEventSource.YouTube, false)]
    [InlineData("settime", "10h", false, false, false, "jimmy",  SubathonEventSource.Twitch, false)]
    [InlineData("lock", "", false, false, false, "John",  SubathonEventSource.External, false)]
    [InlineData("lock", "", false, false, false, "jimmy",  SubathonEventSource.Twitch, false)]
    [InlineData("unlock", "", false, false, false, "John",  SubathonEventSource.External, false)]
    [InlineData("unlock", "", false, false, false, "jimmy",  SubathonEventSource.Twitch, false)]
    [InlineData("setmultiplier", "2xp 1h", false, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("setmultiplier", "2.5xpt", false, false, false, "John",  SubathonEventSource.External, false)]
    [InlineData("setmultiplier", "3xt 5h", false, false, false, "jimmy",  SubathonEventSource.Twitch, false)]
    [InlineData("stopmultiplier", "", false, false, false, "John",  SubathonEventSource.External, false)]
    [InlineData("stopmultiplier", "", false, false, false, "jimmy",  SubathonEventSource.External, false)]
    [InlineData("refreshoverlays", "", false, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("refreshoverlays", "", false, false, false, "jimmy",  SubathonEventSource.Twitch, false)]
    [InlineData("addmoney", "5 CAD", false, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("addmoney", "10.55 USD", false, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("addmoney", "50 TWD", false, false, false, "jimmy",  SubathonEventSource.External, false)]
    [InlineData("subtractmoney", "5 CAD", false, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("subtractmoney", "10.55 GBP", false, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("subtractmoney", "50.77 AUD", false, false, false, "jimmy",  SubathonEventSource.Twitch, false)]
    public void IsValidCommand_Works_InvalidPerms(string? command, string? message, bool isBroadcaster, 
        bool isMod, bool isVip,
        string name, SubathonEventSource? source,
        bool? expected)
    {
        SetupServices();
        CommandService.SetConfig(MockConfig());
        bool outcome = CommandService.ChatCommandRequest(source ?? SubathonEventSource.Unknown, $"!{command} {message}".Trim(), 
            name, isBroadcaster, isMod, isVip, DateTime.Now);
        Assert.Equal(expected, outcome);
        
        CommandService.SetConfig(null!);
        AppServices.Provider = null!;
    }
    
    [Theory]
    [InlineData(null, null, false, false, false, "jimmy",  null, false)]
    [InlineData("addpoints", "five", false, true, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("addpoints", "xdx", true, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("addpoints", "", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, false)]
    [InlineData("subtractpoints", "huh", false, true, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("subtractpoints", "apple", true, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("subtractpoints", "5.5", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, false)]
    [InlineData("setpoints", "5.6", false, true, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("setpoints", "", true, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("setpoints", "what's a mustard", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, false)]
    [InlineData("addtime", "year", false, true, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("addtime", "", true, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("addtime", "1a", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, false)]
    [InlineData("addtime", "1y", false, true, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("addtime", "5x:8h", true, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("addtime", "some", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, false)]
    [InlineData("subtracttime", "50f", false, true, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("subtracttime", "5wq", true, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("subtracttime", "i", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, false)]
    [InlineData("settime", "", false, true, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("settime", "t5", true, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("settime", "", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, false)]
    [InlineData("setmultiplier", "2x 1h", false, true, false, "John",  SubathonEventSource.Twitch, true)] // isValid but throws stopmult
    [InlineData("setmultiplier", "pt", true, false, false, "John",  SubathonEventSource.Twitch, true)]// isValid but throws stopmult
    [InlineData("setmultiplier", "3t 5h", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, true)]// isValid but throws stopmult
    [InlineData("addmoney", "5", false, true, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("addmoney", "10.55 FAKE", true, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("addmoney", "50 cheeseburger", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, false)]
    [InlineData("subtractmoney", "5 www", false, true, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("subtractmoney", "", true, false, false, "John",  SubathonEventSource.Twitch, false)]
    [InlineData("subtractmoney", "x AUD", false, false, false, "speciallittleguy",  SubathonEventSource.Twitch, false)]
    public void IsValidCommand_Works_InvalidParams(string? command, string? message, bool isBroadcaster, 
        bool isMod, bool isVip,
        string name, SubathonEventSource? source,
        bool? expected)
    {
        SetupServices();
        CommandService.SetConfig(MockConfig());
        bool outcome = CommandService.ChatCommandRequest(source ?? SubathonEventSource.Unknown, $"!{command} {message}".Trim(), 
            name, isBroadcaster, isMod, isVip, DateTime.Now);
        Assert.Equal(expected, outcome);
        
        CommandService.SetConfig(null!);
        AppServices.Provider = null!;
    }
}