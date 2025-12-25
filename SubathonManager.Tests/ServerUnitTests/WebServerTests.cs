using System.Text;
using Moq;
using IniParser.Model;
using SubathonManager.Core.Models;
using SubathonManager.Core.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Server;
using SubathonManager.Data;
namespace SubathonManager.Tests.ServerUnitTests;

[Collection("WebServerTests")]
public class WebServerTests
{
    
    [Theory]
    [InlineData("test.html", "text/html")]
    [InlineData("test/image.png", "image/png")]
    [InlineData("file.unknown", "application/octet-stream")]
    [InlineData("test.css", "text/css")]
    [InlineData("test.js", "application/javascript")]
    [InlineData("test.json", "application/json")]
    [InlineData("test/image.jpg", "image/jpeg")]
    [InlineData("test/image.gif", "image/gif")]
    [InlineData("test/image.webp", "image/webp")]
    [InlineData("test/image.avif", "image/avif")]
    [InlineData("test/image.bmp", "image/bmp")]
    [InlineData("test/image.svg", "image/svg+xml")]
    [InlineData("test/image.ico", "image/x-icon")]
    [InlineData("test/videos/video.mp4", "video/mp4")]
    [InlineData("test/video.m4v", "video/x-m4v")]
    [InlineData("test/video.webm", "video/webm")]
    [InlineData("test/video.ogv", "video/ogg")]
    [InlineData("test/sound.mp3", "audio/mpeg")]
    [InlineData("test/sound.wav", "audio/wav")]
    [InlineData("test/sound.ogg", "audio/ogg")]
    [InlineData("test/sound.opus", "audio/opus")]
    [InlineData("test/sound.m4a", "audio/mp4")]
    [InlineData("test/font.woff", "font/woff")]
    [InlineData("test/font.woff2", "font/woff2")]
    [InlineData("test/font.ttf", "font/ttf")]
    [InlineData("test/font.otf", "font/otf")]
    [InlineData("test/data.txt", "text/plain")]
    [InlineData("test/data.csv", "text/csv")]
    [InlineData("test/data.xml", "application/xml")]
    public void ContentType_IsCorrect(string file, string expected)
    {
        Assert.Equal(expected, WebServer.GetContentType(file));
    }
    
    private static WebServer CreateServer()
    {
        var dbName = Guid.NewGuid().ToString();

        var services = new ServiceCollection();

        services.AddLogging();

        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseInMemoryDatabase(dbName));

        var mockConfig = MockConfig();
        services.AddSingleton(mockConfig);
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<WebServer>>();
        var factory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        AppServices.Provider = provider;
        return new WebServer(factory, mockConfig, logger, port: 14045);
    }
    
        private static IConfig MockConfig(Dictionary<(string, string), string>? values = null)
    {
        var mock = new Mock<IConfig>();
        
        mock.Setup(c => c.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string s, string k, string d) =>
                values != null && values.TryGetValue((s, k), out var v) ? v : d);
            
        var kd0 = new KeyData("Port");
        kd0.Value = "14045";
        mock.Setup(c => c.GetSection("Server")).Returns(() =>
        {
            var kdc = new KeyDataCollection();
            kdc.AddKey(kd0);
            return kdc;
        });
        return mock.Object;
    }

    
    [Fact]
    public void MatchRoute_Matches_By_Method_And_Prefix()
    {
        var server = CreateServer();

        var handler = server.MatchRoute("GET", "/api/data/status");

        Assert.NotNull(handler);
    }

    [Fact]
    public void MatchRoute_Returns_Null_For_Wrong_Method()
    {
        var server = CreateServer();

        var handler = server.MatchRoute("POST", "/api/data/status");

        Assert.Null(handler);
    }
    
    [Fact]
    public void MatchRoute_Does_Not_Match_Unrelated_Prefix()
    {
        var server = CreateServer();

        var handler = server.MatchRoute("GET", "/api/data/status123");

        Assert.NotNull(handler);
    }
    
    [Fact]
    public async Task HandleRequest_Executes_Route_Handler()
    {
        var ctx = new MockHttpContext
        {
            Method = "GET",
            Path = "/api/data/values"
        };

        var server = CreateServer();

        await server.InvokeHandleRequest(ctx);

        Assert.NotEqual(404, ctx.StatusCode);
    }
    
    [Fact]
    public async Task HandleRequest_Returns_404_For_Unknown_Route()
    {
        var ctx = new MockHttpContext
        {
            Method = "GET",
            Path = "/nope"
        };

        var server = CreateServer();

        await server.InvokeHandleRequest(ctx);

        Assert.Equal(404, ctx.StatusCode);
    }

    [Fact]
    public async Task Api_Unknown_Route_Returns_400()
    {
        var ctx = new MockHttpContext
        {
            Method = "GET",
            Path = "/api/nope"
        };

        var server = CreateServer();

        await server.InvokeHandleRequest(ctx);

        Assert.Equal(400, ctx.StatusCode);
    }
    
    [Fact]
    public async Task DataControl_Invalid_Body_Returns_400()
    {
        var ctx = new MockHttpContext
        {
            Method = "POST",
            Path = "/api/data/control",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(""))
        };

        var server = CreateServer();

        await server.InvokeHandleRequest(ctx);

        Assert.Equal(400, ctx.StatusCode);
    }
    
    [Fact]
    public async Task DataControl_Invalid_Type_Returns_400()
    {
        var json = "{\"type\":\"NotARealType\"}";
        var ctx = new MockHttpContext
        {
            Method = "POST",
            Path = "/api/data/control",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(json))
        };

        var server = CreateServer();

        await server.InvokeHandleRequest(ctx);

        Assert.Equal(400, ctx.StatusCode);
    }

    [Fact]
    public void BuildDataSummary_Groups_Currency_Donations()
    {
        var events = new List<SubathonEvent>
        {
            new() { EventType = SubathonEventType.KoFiDonation, Currency = "USD", Value = "5.55" },
            new() { EventType = SubathonEventType.StreamElementsDonation, Currency = "USD", Value = "3" }
        };

        var server = CreateServer();

        var result = server.InvokeBuildDataSummary(events);

        Assert.NotNull(result);
    }
    
    /////////////// Widget and Route tests
    
    [Fact]
    public async Task HandleSelectAsync_Returns_200_With_Route_Info()
    {
        var server = CreateServer();
        var mockConfig = MockConfig();
        var route = new Route(mockConfig){ Name = "TestRoute" };
        var widget = new Widget("Widget1", "test.html") { Route = route, RouteId = route.Id };
        route.Widgets.Add(widget);

        await using (var db = await server._factory.CreateDbContextAsync())
        {
            db.Routes.Add(route);
            db.Widgets.Add(widget);
            await db.SaveChangesAsync();
        }

        var ctx = new MockHttpContext
        {
            Method = "GET",
            Path = $"/api/select/{route.Id}"
        };

        await server.HandleSelectAsync(ctx);

        Assert.Equal(200, ctx.StatusCode);
        var body = ctx.GetResponseText();
        Assert.Equal("OK", body);
    }
    
    [Fact]
    public async Task HandleWidgetUpdateAsync_Updates_Widget_Position()
    {
        var server = CreateServer();
        var mockConfig = MockConfig();
        var route = new Route(mockConfig){ Name = "Route1" };
        var widget = new Widget("Widget1", "test.html") { Route = route, RouteId = route.Id };
        route.Widgets.Add(widget);

        await using (var db = await server._factory.CreateDbContextAsync())
        {
            db.Routes.Add(route);
            db.Widgets.Add(widget);
            await db.SaveChangesAsync();
        }

        var updateJson = $"{{\"x\":162,\"y\":200}}";
        var ctx = new MockHttpContext
        {
            Method = "POST",
            Path = $"/api/update-position/{widget.Id}",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(updateJson))
        };

        await server.HandleWidgetUpdateAsync(ctx);

        Assert.Equal(200, ctx.StatusCode);
        await using (var db = await server._factory.CreateDbContextAsync())
        {
            var updatedWidget = await db.Widgets.FindAsync(widget.Id);
            Assert.Equal(162, updatedWidget!.X);
            Assert.Equal(200, updatedWidget.Y);
        }
    }
    
        
    [Fact]
    public async Task HandleWidgetUpdateAsync_Updates_Widget_Scale()
    {
        var server = CreateServer();
        var mockConfig = MockConfig();
        var route = new Route(mockConfig){ Name = "Route1" };
        var widget = new Widget("Widget1", "test.html") { Route = route, RouteId = route.Id };
        route.Widgets.Add(widget);

        await using (var db = await server._factory.CreateDbContextAsync())
        {
            db.Routes.Add(route);
            db.Widgets.Add(widget);
            await db.SaveChangesAsync();
        }

        var updateJson = $"{{\"scaleX\":2,\"scaleY\":2.5}}";
        var ctx = new MockHttpContext
        {
            Method = "POST",
            Path = $"/api/update-size/{widget.Id}",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(updateJson))
        };

        await server.HandleWidgetUpdateAsync(ctx);

        Assert.Equal(200, ctx.StatusCode);
        await using (var db = await server._factory.CreateDbContextAsync())
        {
            var updatedWidget = await db.Widgets.FindAsync(widget.Id);
            Assert.Equal(2, updatedWidget!.ScaleX);
            Assert.Equal(2.5, updatedWidget.ScaleY);
        }
    }

    
    [Fact]
    public async Task HandleStatusRequestAsync_Returns_400_No_Subathon()
    {
        var server = CreateServer();
        var ctx = new MockHttpContext
        {
            Method = "GET",
            Path = "/api/data/status"
        };

        await server.HandleStatusRequestAsync(ctx);

        Assert.Equal(400, ctx.StatusCode);
    }
    
    [Fact]
    public async Task HandleStatusRequestAsync_Returns_200_With_Expected_Content()
    {
        var server = CreateServer();
        var ctx = new MockHttpContext
        {
            Method = "GET",
            Path = "/api/data/status"
        };
        
        SubathonData subathon = new  SubathonData();
        await using (var db = await server._factory.CreateDbContextAsync())
        {
            db.SubathonDatas.Add(subathon);
            await db.SaveChangesAsync();
        }

        await server.HandleStatusRequestAsync(ctx);

        Assert.Equal(200, ctx.StatusCode);
        var text = ctx.GetResponseText();
        Assert.Contains("millis_remaining", text, StringComparison.OrdinalIgnoreCase);
    }
    
    [Fact]
    public async Task HandleAmountsRequestAsync_Returns_200()
    {
        var server = CreateServer();
        SubathonData subathon = new  SubathonData();
        await using (var db = await server._factory.CreateDbContextAsync())
        {
            db.SubathonDatas.Add(subathon);
            await db.SaveChangesAsync();
        }

        var ctx = new MockHttpContext
        {
            Method = "GET",
            Path = "/api/data/amounts"
        };

        await server.HandleAmountsRequestAsync(ctx);

        Assert.Equal(200, ctx.StatusCode);
        var text = ctx.GetResponseText();
        Assert.NotNull(text);
        Assert.Contains("real", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("simulated", text, StringComparison.OrdinalIgnoreCase);
    }

    
    [Fact]
    public async Task HandleValuesPatchRequestAsync_Updates_SubValue()
    {
        var server = CreateServer();

        SubathonValue subathonValue = new  SubathonValue
        {
            Meta = "1000",
            EventType = SubathonEventType.TwitchSub,
            Points = 1,
            Seconds = 60
        };
        
        await using (var db = await server._factory.CreateDbContextAsync())
        {
            db.SubathonValues.Add(subathonValue);
            await db.SaveChangesAsync();
        }

        var patchJson = "[{\"EventType\":\"TwitchSub\", \"Source\":\"Twitch\", \"Seconds\": 20, \"Meta\": \"1000\"}]";
        var ctx = new MockHttpContext
        {
            Method = "PATCH",
            Path = "/api/data/values",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(patchJson))
        };

        await server.HandleValuesPatchRequestAsync(ctx);

        Assert.Equal(200, ctx.StatusCode);

        await using (var db = await server._factory.CreateDbContextAsync())
        {
            var updatedVal = await db.SubathonValues.Where(
                x => x.EventType == SubathonEventType.TwitchSub && x.Meta == "1000").FirstOrDefaultAsync();
            Assert.Equal(20, updatedVal!.Seconds);
        }
    }
    
    [Fact]
    public async Task HandleWidgetRequestAsync_Returns_Widget_Html_With_Overrides()
    {
        var server = CreateServer();
        var route = new Route(MockConfig()) { Name = "TestRoute" };
    
        string tempHtml = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".html");
        await File.WriteAllTextAsync(tempHtml, "<html><head></head><body></body></html>");
    
        var widget = new Widget("Widget1", tempHtml) 
        { 
            Route = route, 
            RouteId = route.Id,
            CssVariables = new List<CssVariable>
            {
                new() { Name = "color-primary", Value = "red", WidgetId = Guid.NewGuid() }
            },
            JsVariables = new List<JsVariable>
            {
                new() { Name = "testVar", Value = "42", Type = WidgetVariableType.Int, WidgetId = Guid.NewGuid() }
            }
        };
        route.Widgets.Add(widget);

        await using var db = await server._factory.CreateDbContextAsync();
        db.Routes.Add(route);
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();

        var ctx = new MockHttpContext
        {
            Method = "GET",
            Path = $"/widget/{widget.Id}"
        };

        await server.HandleWidgetRequest(ctx);

        Assert.Equal(200, ctx.StatusCode);
        var body = ctx.GetResponseText();
        Assert.Contains("<style type=\"text/css\">", body);
        Assert.Contains("color-primary: red", body);
        Assert.Contains("const testVar = 42", body);

        File.Delete(tempHtml);
    }
    
    [Fact]
    public async Task HandleWidgetRequest_Returns_404_For_Invalid_Widget()
    {
        var server = CreateServer();

        var ctx = new MockHttpContext
        {
            Method = "GET",
            Path = $"/widget/{Guid.NewGuid()}"
        };

        await server.HandleRouteRequest(ctx);

        Assert.Equal(404, ctx.StatusCode);
    }
    
    [Fact]
    public async Task HandleRouteRequest_Returns_200_With_Merged_Html()
    {
        var server = CreateServer();
        var mockConfig = MockConfig();

        string tempHtml = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".html");
        await File.WriteAllTextAsync(tempHtml, "<html><head></head><body></body></html>");

        var route = new Route(mockConfig) { Name = "TestRoute" };
        var widget = new Widget("Widget1", tempHtml)
        {
            Route = route,
            RouteId = route.Id,
            CssVariables = new List<CssVariable>
            {
                new() { Name = "bg-color", Value = "blue", WidgetId = Guid.NewGuid() }
            },
            JsVariables = new List<JsVariable>
            {
                new() { Name = "widgetVar", Value = "true", Type = WidgetVariableType.Boolean, WidgetId = Guid.NewGuid() }
            }
        };
        route.Widgets.Add(widget);

        await using var db = await server._factory.CreateDbContextAsync();
        db.Routes.Add(route);
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();

        var ctx = new MockHttpContext
        {
            Method = "GET",
            Path = $"/route/{route.Id}?edit=false"
        };

        await server.HandleRouteRequest(ctx);

        Assert.Equal(200, ctx.StatusCode);
        var html = ctx.GetResponseText();
        Assert.Contains("iframe", html);
        Assert.Contains($"<iframe src=\"/widget/{widget.Id}/\"", html);
        Assert.Contains($"<title>overlay-{route.Id}", html);
        Assert.Contains("<html>", html, StringComparison.OrdinalIgnoreCase);

        File.Delete(tempHtml);
    }
    
    [Fact]
    public async Task HandleRouteRequest_Returns_404_For_Invalid_Route()
    {
        var server = CreateServer();

        var ctx = new MockHttpContext
        {
            Method = "GET",
            Path = $"/route/{Guid.NewGuid()}"
        };

        await server.HandleRouteRequest(ctx);

        Assert.Equal(404, ctx.StatusCode);
    }
}