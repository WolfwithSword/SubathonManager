using System.Text;
using IniParser.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Server;
using SubathonManager.Server.Interfaces;
using SubathonManager.Services;

// ReSharper disable NullableWarningSuppressionIsUsed
namespace SubathonManager.Tests.ServerUnitTests;

[Collection("GlobalState")]
public class WebServerTests {
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
    public void ContentType_IsCorrect(string file, string expected) {
        Assert.Equal(expected, WebServer.GetContentType(file));
    }


    private static void SetupServices() {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        IConfig mockConfig = MockConfig(new Dictionary<(string, string), string> {
            { ("Server", "Port"), "14045" }
        });
        services.AddSingleton(mockConfig);
        AppServices.Provider = services.BuildServiceProvider();
    }

    private static WebServer CreateServer() {
        SetupServices();
        var logger = AppServices.Provider.GetRequiredService<ILogger<WebServer>>();
        var factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        IConfig mockConfig = MockConfig(new Dictionary<(string, string), string> {
            { ("Server", "Port"), "14045" }
        });
        var webserver = new WebServer(logger, mockConfig, factory);
        webserver.Initialize();
        return webserver;
    }

    private static IConfig MockConfig(Dictionary<(string, string), string>? values = null) {
        var mock = new Mock<IConfig>();

        mock.Setup(c => c.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string s, string k, string d) =>
                values != null && values.TryGetValue((s, k), out string? v) ? v : d);

        var kd0 = new KeyData("Port");
        kd0.Value = "14045";
        mock.Setup(c => c.GetSection("Server")).Returns(() => {
            var kdc = new KeyDataCollection();
            kdc.AddKey(kd0);
            return kdc;
        });
        return mock.Object;
    }

    [Fact]
    public void MatchRoute_Matches_By_Method_And_Prefix() {
        SetupServices();
        WebServer server = CreateServer();
        Func<IHttpContext, Task>? handler = server.MatchRoute("GET", "/api/data/status");

        Assert.NotNull(handler);
        AppServices.Provider = null!;
        server.Stop();
    }

    [Fact]
    public void MatchRoute_Returns_Null_For_Wrong_Method() {
        WebServer server = CreateServer();
        SetupServices();
        Func<IHttpContext, Task>? handler = server.MatchRoute("POST", "/api/data/status");
        Assert.Null(handler);
        AppServices.Provider = null!;
    }

    [Fact]
    public void MatchRoute_Does_Not_Match_Unrelated_Prefix() {
        WebServer server = CreateServer();
        SetupServices();
        Func<IHttpContext, Task>? handler = server.MatchRoute("GET", "/api/data/status123");
        Assert.NotNull(handler);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task HandleRequest_Executes_Route_Handler() {
        var ctx = new MockHttpContext {
            Method = "GET",
            Path = "/api/data/values"
        };

        WebServer server = CreateServer();
        SetupServices();
        await server.InvokeHandleRequest(ctx);
        Assert.NotEqual(404, ctx.StatusCode);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task HandleRequest_Returns_404_For_Unknown_Route() {
        var ctx = new MockHttpContext {
            Method = "GET",
            Path = "/nope"
        };

        WebServer server = CreateServer();
        SetupServices();
        await server.InvokeHandleRequest(ctx);
        Assert.Equal(404, ctx.StatusCode);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Api_Unknown_Route_Returns_400() {
        var ctx = new MockHttpContext {
            Method = "GET",
            Path = "/api/nope"
        };
        WebServer server = CreateServer();
        SetupServices();
        await server.InvokeHandleRequest(ctx);
        Assert.Equal(400, ctx.StatusCode);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Commands_Endpoint_Returns_Catalog() {
        var ctx = new MockHttpContext {
            Method = "GET",
            Path = "/api/data/commands"
        };

        WebServer server = CreateServer();
        SetupServices();
        await server.InvokeHandleRequest(ctx);
        Assert.Equal(200, ctx.StatusCode);
        Assert.Contains("\"AddTime\"", ctx.ResponseBody);
        Assert.Contains("requires_parameter", ctx.ResponseBody);
        Assert.DoesNotContain("\"Unknown\"", ctx.ResponseBody);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Globals_Endpoint_Returns_Typed_Values_Filtered_By_Widget_Or_Name() {
        WebServer server = CreateServer();
        var widgetId = Guid.NewGuid();
        await using (AppDbContext db = await server._factory.CreateDbContextAsync(TestContext.Current.CancellationToken)) {
            db.ActionGlobals.AddRange(
                new ActionGlobal { Kind = ActionStoreKind.Global, Name = "deaths", ValueType = ActionValueType.Number, Value = "3.5" },
                new ActionGlobal { Kind = ActionStoreKind.Global, Name = "hat_on", ValueType = ActionValueType.Boolean, Value = "true" },
                new ActionGlobal { Kind = ActionStoreKind.Global, Name = "greeting", ValueType = ActionValueType.Text, Value = "hi" },
                new ActionGlobal { Kind = ActionStoreKind.Secret, Name = "api_key", StorageKey = "x" });
            db.Widgets.Add(new Widget("counter", "counter.html") { Id = widgetId, GlobalVars = "DEATHS,hat_on,gone" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        async Task<string> Get(string query) {
            var ctx = new MockHttpContext { Method = "GET", Path = "/api/data/globals", QueryString = query };
            await server.InvokeHandleRequest(ctx);
            Assert.Equal(200, ctx.StatusCode);
            return ctx.ResponseBody;
        }

        string all = await Get("");
        Assert.Contains("{\"name\":\"deaths\",\"type\":\"number\",\"value\":3.5}", all);
        Assert.Contains("{\"name\":\"hat_on\",\"type\":\"boolean\",\"value\":true}", all);
        Assert.Contains("{\"name\":\"greeting\",\"type\":\"text\",\"value\":\"hi\"}", all);
        Assert.DoesNotContain("api_key", all);

        string forWidget = await Get($"?widget={widgetId}");
        Assert.Contains("deaths", forWidget);
        Assert.Contains("hat_on", forWidget);
        Assert.DoesNotContain("greeting", forWidget);

        string byName = await Get("?name=GREETING");
        Assert.Contains("greeting", byName);
        Assert.DoesNotContain("deaths", byName);

        var missing = new MockHttpContext {
            Method = "GET", Path = "/api/data/globals", QueryString = $"?widget={Guid.NewGuid()}"
        };
        await server.InvokeHandleRequest(missing);
        Assert.Equal(404, missing.StatusCode);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Globals_Post_Creates_Sets_Adds_Toggles_And_Rejects_Bad_Requests() {
        WebServer server = CreateServer();
        var services = new ServiceCollection();
        services.AddSingleton(new ActionService(server._factory, []));
        AppServices.Provider = services.BuildServiceProvider();

        async Task<(int Code, string Body)> Post(string query, string body = "") {
            var ctx = new MockHttpContext {
                Method = "POST", Path = "/api/data/globals", QueryString = query,
                Body = new MemoryStream(Encoding.UTF8.GetBytes(body))
            };
            await server.InvokeHandleRequest(ctx);
            return (ctx.StatusCode, ctx.ResponseBody);
        }

        Assert.Equal((200, "{\"created\":true,\"global\":{\"name\":\"deaths\",\"type\":\"number\",\"value\":2}}"),
            await Post("?name=deaths&op=add&value=2"));
        Assert.Contains("\"value\":-1.5", (await Post("?name=debt&op=subtract&value=1.5")).Body);
        Assert.Contains("\"value\":true", (await Post("", "{\"name\":\"hat_on\",\"op\":\"toggle\"}")).Body);
        Assert.Equal(400, (await Post("?name=greeting&value=hi")).Code);
        Assert.Contains("\"value\":\"hi\"", (await Post("?name=greeting&type=text&value=hi")).Body);
        Assert.Contains("\"value\":7", (await Post("", "{\"name\":\"lives\",\"value\":7}")).Body);

        Assert.Equal((200, "{\"created\":false,\"global\":{\"name\":\"deaths\",\"type\":\"number\",\"value\":1}}"),
            await Post("?name=DEATHS&op=subtract&value=1"));
        Assert.Contains("\"value\":false", (await Post("?name=hat_on&op=toggle")).Body);
        Assert.Contains("\"value\":true", (await Post("", "{\"name\":\"hat_on\",\"value\":true}")).Body);
        Assert.Contains("\"value\":\"bye\"", (await Post("?name=greeting&value=bye")).Body);

        foreach ((string query, string body) in new[] {
                     ("?name=greeting&op=add&value=1", ""), // text can only be set
                     ("?name=greeting&op=toggle", ""),
                     ("", "{\"name\":\"greeting\",\"value\":5}"), // number sent to text
                     ("?name=deaths&type=boolean&value=true", ""),
                     ("?name=deaths&op=add&value=lots", ""),
                     ("?name=deaths&op=add", ""), // value missing
                     ("?name=deaths", ""),
                     ("?name=hat_on&value=maybe", ""),
                     ("?name=deaths&op=multiply&value=2", ""),
                     ("?name=deaths&type=float&value=2", ""),
                     ("?value=2", ""), // name missing
                     ("?name=bad-name&type=text&value=x", ""),
                     ("", "[1,2]")
                 }) {
            (int code, string error) = await Post(query, body);
            Assert.True(code == 400, $"{query} {body} -> {code} {error}");
            Assert.Contains("\"error\"", error);
        }

        await using AppDbContext db = await server._factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(5, await db.ActionGlobals.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task DataControl_Invalid_Body_Returns_400() {
        var ctx = new MockHttpContext {
            Method = "POST",
            Path = "/api/data/control",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(""))
        };

        WebServer server = CreateServer();
        SetupServices();
        await server.InvokeHandleRequest(ctx);
        Assert.Equal(400, ctx.StatusCode);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task DataControl_Invalid_Type_Returns_400() {
        var json = "{\"type\":\"NotARealType\"}";
        var ctx = new MockHttpContext {
            Method = "POST",
            Path = "/api/data/control",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(json))
        };

        WebServer server = CreateServer();
        SetupServices();
        await server.InvokeHandleRequest(ctx);
        Assert.Equal(400, ctx.StatusCode);
        AppServices.Provider = null!;
    }

    [Fact]
    public void BuildDataSummary_Groups_Currency_Donations() {
        var events = new List<SubathonEvent> {
            new() { EventType = SubathonEventType.KoFiDonation, Currency = "USD", Value = "5.55" },
            new() { EventType = SubathonEventType.StreamElementsDonation, Currency = "USD", Value = "3" }
        };
        WebServer server = CreateServer();
        SetupServices();
        object result = server.InvokeBuildDataSummary(events);
        Assert.NotNull(result);
        AppServices.Provider = null!;
    }

    /////////////// Widget and Route tests

    [Fact]
    public async Task HandleSelectAsync_Returns_200_With_Route_Info() {
        WebServer server = CreateServer();
        SetupServices();
        var route = new Route { Name = "TestRoute" };
        var widget = new Widget("Widget1", "test.html") { Route = route, RouteId = route.Id };
        route.Widgets.Add(widget);

        await using (AppDbContext
                     db = await server._factory.CreateDbContextAsync(TestContext.Current.CancellationToken)) {
            db.Routes.Add(route);
            db.Widgets.Add(widget);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var ctx = new MockHttpContext {
            Method = "GET",
            Path = $"/api/select/{route.Id}"
        };

        await server.HandleSelectAsync(ctx);

        Assert.Equal(200, ctx.StatusCode);
        string body = ctx.GetResponseText();
        Assert.Equal("OK", body);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task HandleWidgetUpdateAsync_Updates_Widget_Position() {
        WebServer server = CreateServer();
        SetupServices();
        var route = new Route { Name = "Route1" };
        var widget = new Widget("Widget1", "test.html") { Route = route, RouteId = route.Id };
        route.Widgets.Add(widget);

        await using (AppDbContext
                     db = await server._factory.CreateDbContextAsync(TestContext.Current.CancellationToken)) {
            db.Routes.Add(route);
            db.Widgets.Add(widget);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var updateJson = "{\"x\":162,\"y\":200}";
        var ctx = new MockHttpContext {
            Method = "POST",
            Path = $"/api/update-position/{widget.Id}",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(updateJson))
        };

        await server.HandleWidgetUpdateAsync(ctx);

        Assert.Equal(200, ctx.StatusCode);
        await using (AppDbContext
                     db = await server._factory.CreateDbContextAsync(TestContext.Current.CancellationToken)) {
            Widget? updatedWidget = await db.Widgets.FindAsync([widget.Id], TestContext.Current.CancellationToken);
            Assert.Equal(162, updatedWidget!.X);
            Assert.Equal(200, updatedWidget.Y);
        }

        AppServices.Provider = null!;
    }


    [Fact]
    public async Task HandleWidgetUpdateAsync_Updates_Widget_Scale() {
        WebServer server = CreateServer();
        SetupServices();
        var route = new Route { Name = "Route1" };
        var widget = new Widget("Widget1", "test.html") { Route = route, RouteId = route.Id };
        route.Widgets.Add(widget);

        await using (AppDbContext
                     db = await server._factory.CreateDbContextAsync(TestContext.Current.CancellationToken)) {
            db.Routes.Add(route);
            db.Widgets.Add(widget);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var updateJson = "{\"scaleX\":2,\"scaleY\":2.5}";
        var ctx = new MockHttpContext {
            Method = "POST",
            Path = $"/api/update-size/{widget.Id}",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(updateJson))
        };

        await server.HandleWidgetUpdateAsync(ctx);

        Assert.Equal(200, ctx.StatusCode);
        await using (AppDbContext
                     db = await server._factory.CreateDbContextAsync(TestContext.Current.CancellationToken)) {
            Widget? updatedWidget = await db.Widgets.FindAsync([widget.Id], TestContext.Current.CancellationToken);
            Assert.Equal(2, updatedWidget!.ScaleX);
            Assert.Equal(2.5, updatedWidget.ScaleY);
        }

        AppServices.Provider = null!;
    }


    [Fact]
    public async Task HandleStatusRequestAsync_Returns_400_No_Subathon() {
        WebServer server = CreateServer();
        SetupServices();
        var ctx = new MockHttpContext {
            Method = "GET",
            Path = "/api/data/status"
        };

        await server.HandleStatusRequestAsync(ctx);

        Assert.Equal(400, ctx.StatusCode);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task HandleStatusRequestAsync_Returns_200_With_Expected_Content() {
        WebServer server = CreateServer();
        SetupServices();
        var ctx = new MockHttpContext {
            Method = "GET",
            Path = "/api/data/status"
        };

        var subathon = new SubathonData();
        await using (AppDbContext
                     db = await server._factory.CreateDbContextAsync(TestContext.Current.CancellationToken)) {
            db.SubathonDatas.Add(subathon);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await server.HandleStatusRequestAsync(ctx);

        Assert.Equal(200, ctx.StatusCode);
        string text = ctx.GetResponseText();
        Assert.Contains("millis_remaining", text, StringComparison.OrdinalIgnoreCase);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task HandleAmountsRequestAsync_Returns_200() {
        WebServer server = CreateServer();
        SetupServices();
        var subathon = new SubathonData();
        await using (AppDbContext
                     db = await server._factory.CreateDbContextAsync(TestContext.Current.CancellationToken)) {
            db.SubathonDatas.Add(subathon);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var ctx = new MockHttpContext {
            Method = "GET",
            Path = "/api/data/amounts"
        };

        await server.HandleAmountsRequestAsync(ctx);

        Assert.Equal(200, ctx.StatusCode);
        string text = ctx.GetResponseText();
        Assert.NotNull(text);
        Assert.Contains("real", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("simulated", text, StringComparison.OrdinalIgnoreCase);
        AppServices.Provider = null!;
    }


    [Fact]
    public async Task HandleValuesPatchRequestAsync_AllPaths() {
        WebServer server = CreateServer();
        SetupServices();
        var subathonValue = new SubathonValue {
            Meta = "1000",
            EventType = SubathonEventType.TwitchSub,
            Points = 1,
            Seconds = 60
        };

        await using (AppDbContext
                     db = await server._factory.CreateDbContextAsync(TestContext.Current.CancellationToken)) {
            db.SubathonValues.Add(subathonValue);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var patchJson = "[{\"EventType\":\"TwitchSub\", \"Source\":\"Twitch\", \"Seconds\": 20, \"Meta\": \"1000\"}]";
        var ctx = new MockHttpContext {
            Method = "PATCH",
            Path = "/api/data/values",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(patchJson))
        };

        await server.HandleValuesPatchRequestAsync(ctx);

        Assert.Equal(200, ctx.StatusCode);

        await using (AppDbContext
                     db = await server._factory.CreateDbContextAsync(TestContext.Current.CancellationToken)) {
            SubathonValue? updatedVal = await db.SubathonValues
                .Where(x => x.EventType == SubathonEventType.TwitchSub && x.Meta == "1000")
                .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
            Assert.Equal(20, updatedVal!.Seconds);
        }

        ctx = new MockHttpContext {
            Method = "PATCH",
            Path = "/api/data/values",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(patchJson))
        };
        await server.HandleValuesPatchRequestAsync(ctx);
        Assert.Equal(201, ctx.StatusCode);

        patchJson = "[{\"EventType\":\"FakeSub\", \"Source\":\"BadData\", \"Seconds\": 20, \"Meta\": \"\"}]";
        ctx = new MockHttpContext {
            Method = "PATCH",
            Path = "/api/data/values",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(patchJson))
        };
        await server.HandleValuesPatchRequestAsync(ctx);
        Assert.Equal(400, ctx.StatusCode);

        AppServices.Provider = null!;
    }

    [Fact]
    public async Task HandleWidgetRequestAsync_Returns_Widget_Html_With_Overrides() {
        WebServer server = CreateServer();
        SetupServices();
        var route = new Route { Name = "TestRoute" };

        string tempHtml = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".html");
        await File.WriteAllTextAsync(tempHtml, "<html><head></head><body></body></html>",
            TestContext.Current.CancellationToken);

        var widget = new Widget("Widget1", tempHtml) {
            Route = route,
            RouteId = route.Id,
            CssVariables = new List<CssVariable> {
                new() { Name = "color-primary", Value = "red", WidgetId = Guid.NewGuid() }
            },
            JsVariables = new List<JsVariable> {
                new() { Name = "testVar", Value = "42", Type = WidgetVariableType.Int, WidgetId = Guid.NewGuid() }
            }
        };
        route.Widgets.Add(widget);

        await using AppDbContext db = await server._factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        db.Routes.Add(route);
        db.Widgets.Add(widget);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ctx = new MockHttpContext {
            Method = "GET",
            Path = $"/widget/{widget.Id}"
        };

        await server.HandleWidgetRequest(ctx);

        Assert.Equal(200, ctx.StatusCode);
        string body = ctx.GetResponseText();
        Assert.Contains("<style type=\"text/css\">", body);
        Assert.Contains("color-primary: red", body);
        Assert.Contains("const testVar = 42", body);

        File.Delete(tempHtml);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task HandleWidgetRequestAsync_Puts_Client_Script_Before_A_Fragment_Widgets_Script() {
        WebServer server = CreateServer();
        SetupServices();
        var route = new Route { Name = "TestRoute" };

        string tempHtml = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".html");
        await File.WriteAllTextAsync(tempHtml,
            "<div id=\"w\"></div>\n<script>\nwindow.__probe = window.Subathon;\n</script>",
            TestContext.Current.CancellationToken);

        var widget = new Widget("Widget1", tempHtml) {
            Route = route,
            RouteId = route.Id,
            JsVariables = new List<JsVariable> {
                new() { Name = "testVar", Value = "42", Type = WidgetVariableType.Int, WidgetId = Guid.NewGuid() }
            }
        };
        route.Widgets.Add(widget);

        await using AppDbContext db = await server._factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        db.Routes.Add(route);
        db.Widgets.Add(widget);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ctx = new MockHttpContext {
            Method = "GET",
            Path = $"/widget/{widget.Id}"
        };

        await server.HandleWidgetRequest(ctx);

        Assert.Equal(200, ctx.StatusCode);
        string body = ctx.GetResponseText();

        int clientAt = body.IndexOf("data-subathon-client", StringComparison.Ordinal);
        int varsAt = body.IndexOf("const testVar = 42", StringComparison.Ordinal);
        int widgetAt = body.IndexOf("window.__probe", StringComparison.Ordinal);

        Assert.True(clientAt >= 0, "client script was not injected");
        Assert.True(varsAt >= 0, "widget variables were not injected");
        Assert.True(widgetAt >= 0, "widget markup was not served");

        Assert.True(clientAt < widgetAt, "client script must come before the widget's own script");
        Assert.True(varsAt < widgetAt, "widget variables must come before the widget's own script");

        File.Delete(tempHtml);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task HandleWidgetRequest_Returns_404_For_Invalid_Widget() {
        WebServer server = CreateServer();
        SetupServices();
        var ctx = new MockHttpContext {
            Method = "GET",
            Path = $"/widget/{Guid.NewGuid()}"
        };

        await server.HandleRouteRequest(ctx);

        Assert.Equal(404, ctx.StatusCode);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task HandleRouteRequest_Returns_200_With_Merged_Html() {
        WebServer server = CreateServer();
        SetupServices();
        string tempHtml = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".html");
        await File.WriteAllTextAsync(tempHtml, "<html><head></head><body></body></html>",
            TestContext.Current.CancellationToken);

        var route = new Route { Name = "TestRoute" };
        var widget = new Widget("Widget1", tempHtml) {
            Route = route,
            RouteId = route.Id,
            CssVariables = new List<CssVariable> {
                new() { Name = "bg-color", Value = "blue", WidgetId = Guid.NewGuid() }
            },
            JsVariables = new List<JsVariable> {
                new() {
                    Name = "widgetVar", Value = "true", Type = WidgetVariableType.Boolean, WidgetId = Guid.NewGuid()
                }
            }
        };
        route.Widgets.Add(widget);

        await using AppDbContext db = await server._factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        db.Routes.Add(route);
        db.Widgets.Add(widget);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ctx = new MockHttpContext {
            Method = "GET",
            Path = $"/route/{route.Id}?edit=false"
        };

        await server.HandleRouteRequest(ctx);

        Assert.Equal(200, ctx.StatusCode);
        string html = ctx.GetResponseText();
        Assert.Contains("iframe", html);
        Assert.Contains($"<iframe src=\"/widget/{widget.Id}/\"", html);
        Assert.Contains($"<title>overlay-{route.Id}", html);
        Assert.Contains("<html>", html, StringComparison.OrdinalIgnoreCase);

        File.Delete(tempHtml);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task HandleRouteRequest_Returns_404_For_Invalid_Route() {
        WebServer server = CreateServer();
        SetupServices();
        var ctx = new MockHttpContext {
            Method = "GET",
            Path = $"/route/{Guid.NewGuid()}"
        };

        await server.HandleRouteRequest(ctx);

        Assert.Equal(404, ctx.StatusCode);

        AppServices.Provider = null!;
    }
}