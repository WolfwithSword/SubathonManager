using System.Text.Json;
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

// ReSharper disable NullableWarningSuppressionIsUsed
namespace SubathonManager.Tests.ServerUnitTests;

[Collection("GlobalState")]
public class WebServerLeaderboardTests {
    private static IConfig MockConfig() {
        var mock = new Mock<IConfig>();
        var values = new Dictionary<(string, string), string> {
            { ("Server", "Port"), "14046" },
            { ("Currency", "Primary"), "USD" }
        };

        mock.Setup(c => c.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string s, string k, string d) => values.TryGetValue((s, k), out string? v) ? v : d);

        var portKey = new KeyData("Port") { Value = "14046" };
        mock.Setup(c => c.GetSection("Server")).Returns(() => {
            var kdc = new KeyDataCollection();
            kdc.AddKey(portKey);
            return kdc;
        });
        return mock.Object;
    }

    private static WebServer CreateServer() {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        IConfig config = MockConfig();
        services.AddSingleton(config);
        AppServices.Provider = services.BuildServiceProvider();

        var logger = AppServices.Provider.GetRequiredService<ILogger<WebServer>>();
        var factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var server = new WebServer(logger, config, factory);
        return server;
    }

    private static async Task<Guid> SeedAsync(WebServer server, params SubathonEvent[] events) {
        var subathon = new SubathonData();
        await using AppDbContext db = await server._factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        db.SubathonDatas.Add(subathon);
        foreach (SubathonEvent ev in events) {
            ev.SubathonId = subathon.Id;
            ev.ProcessedToSubathon = true;
            db.SubathonEvents.Add(ev);
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return subathon.Id;
    }

    private static async Task<JsonElement> QueryAsync(WebServer server, string queryString) {
        var ctx = new MockHttpContext {
            Method = "GET",
            Path = "/api/data/leaderboard",
            QueryString = queryString
        };

        await server.HandleLeaderboardRequestAsync(ctx);
        Assert.Equal(200, ctx.StatusCode);
        return JsonDocument.Parse(ctx.ResponseBody).RootElement;
    }

    private static async Task<(int code, string body)> RawQueryAsync(WebServer server, string queryString) {
        var ctx = new MockHttpContext {
            Method = "GET",
            Path = "/api/data/leaderboard",
            QueryString = queryString
        };

        await server.HandleLeaderboardRequestAsync(ctx);
        return (ctx.StatusCode, ctx.ResponseBody);
    }

    private static SubathonEvent GiftSub(string user, string tier, int amount, int points) {
        return new SubathonEvent {
            Source = SubathonEventSource.Twitch,
            EventType = SubathonEventType.TwitchGiftSub,
            Currency = "sub",
            User = user,
            Value = tier,
            Amount = amount,
            PointsValue = points
        };
    }

    [Fact]
    public void Route_Is_Registered() {
        WebServer server = CreateServer();
        server.Initialize();
        Assert.NotNull(server.MatchRoute("GET", "/api/data/leaderboard"));
        server.Stop();
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Missing_Type_Returns_400() {
        WebServer server = CreateServer();
        await SeedAsync(server);

        (int code, string body) = await RawQueryAsync(server, "top=5");

        Assert.Equal(400, code);
        Assert.Contains("type", body, StringComparison.OrdinalIgnoreCase);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Numeric_Type_Is_Rejected() {
        WebServer server = CreateServer();
        await SeedAsync(server);

        (int code, _) = await RawQueryAsync(server, "type=2");

        Assert.Equal(400, code);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task No_Active_Subathon_Returns_400() {
        WebServer server = CreateServer();

        (int code, string body) = await RawQueryAsync(server, "type=TwitchGiftSub");

        Assert.Equal(400, code);
        Assert.Contains("subathon", body, StringComparison.OrdinalIgnoreCase);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Subs_Default_To_ByPoints_And_Rank_Descending() {
        WebServer server = CreateServer();
        await SeedAsync(server,
            GiftSub("alice", "1000", 5, 10),
            GiftSub("bob", "1000", 1, 10),
            GiftSub("bob", "2000", 2, 20));

        JsonElement root = await QueryAsync(server, "type=TwitchGiftSub");

        Assert.Equal("ByPoints", root.GetProperty("method").GetString());
        Assert.Equal("points", root.GetProperty("unit").GetString());
        Assert.Equal(2, root.GetProperty("user_count").GetInt32());
        Assert.Equal(100, root.GetProperty("total").GetDouble());

        JsonElement results = root.GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal("alice", results[0].GetProperty("user").GetString());
        Assert.Equal(50, results[0].GetProperty("value").GetDouble());
        Assert.Equal(1, results[0].GetProperty("events").GetInt32());
        Assert.Equal(5, results[0].GetProperty("count").GetInt32());
        Assert.Equal("bob", results[1].GetProperty("user").GetString());
        Assert.Equal(2, results[1].GetProperty("events").GetInt32());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Subs_ByCount_Sums_Gifted_Amounts() {
        WebServer server = CreateServer();
        await SeedAsync(server,
            GiftSub("alice", "1000", 5, 10),
            GiftSub("bob", "1000", 1, 10),
            GiftSub("bob", "2000", 2, 20));

        JsonElement root = await QueryAsync(server, "type=TwitchGiftSub&method=byCount");

        Assert.Equal("ByCount", root.GetProperty("method").GetString());
        Assert.Equal("count", root.GetProperty("unit").GetString());
        JsonElement results = root.GetProperty("results");
        Assert.Equal("alice", results[0].GetProperty("user").GetString());
        Assert.Equal(5, results[0].GetProperty("value").GetDouble());
        Assert.Equal(3, results[1].GetProperty("value").GetDouble());
        AppServices.Provider = null!;
    }

    [Theory]
    [InlineData("meta=1000")]
    [InlineData("meta=T1")]
    [InlineData("tier=t1")]
    public async Task Meta_Filter_Matches_Tier_By_Value(string metaQuery) {
        WebServer server = CreateServer();
        await SeedAsync(server,
            GiftSub("alice", "1000", 5, 10),
            GiftSub("bob", "2000", 2, 20));

        JsonElement root = await QueryAsync(server, $"type=TwitchGiftSub&{metaQuery}");

        Assert.Equal(1, root.GetProperty("user_count").GetInt32());
        Assert.Equal("alice", root.GetProperty("results")[0].GetProperty("user").GetString());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Meta_Filter_Matches_EventTypeMeta() {
        WebServer server = CreateServer();
        SubathonEvent tagged = GiftSub("alice", "3000", 1, 10);
        tagged.EventTypeMeta = "3000";
        await SeedAsync(server, tagged, GiftSub("bob", "1000", 1, 10));

        JsonElement root = await QueryAsync(server, "type=TwitchGiftSub&meta=3000");

        Assert.Equal(1, root.GetProperty("user_count").GetInt32());
        Assert.Equal("alice", root.GetProperty("results")[0].GetProperty("user").GetString());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Blacklist_Excludes_Exact_And_Wildcard_Users() {
        WebServer server = CreateServer();
        await SeedAsync(server,
            GiftSub("alice", "1000", 1, 10),
            GiftSub("Anonymous", "1000", 1, 10),
            GiftSub("SYSTEM Twitch", "1000", 1, 10));

        JsonElement root = await QueryAsync(server,
            "type=TwitchGiftSub&blacklist=anonymous,SYSTEM*");

        Assert.Equal(1, root.GetProperty("user_count").GetInt32());
        Assert.Equal("alice", root.GetProperty("results")[0].GetProperty("user").GetString());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Top_Limits_Results_But_Not_Totals() {
        WebServer server = CreateServer();
        await SeedAsync(server,
            GiftSub("alice", "1000", 3, 10),
            GiftSub("bob", "1000", 2, 10),
            GiftSub("carol", "1000", 1, 10));

        JsonElement root = await QueryAsync(server, "type=TwitchGiftSub&top=2");

        Assert.Equal(2, root.GetProperty("results").GetArrayLength());
        Assert.Equal(3, root.GetProperty("user_count").GetInt32());
        Assert.Equal(60, root.GetProperty("total").GetDouble());

        JsonElement all = await QueryAsync(server, "type=TwitchGiftSub&top=all");
        Assert.Equal(3, all.GetProperty("results").GetArrayLength());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Tokens_Default_To_Summed_Values() {
        WebServer server = CreateServer();
        await SeedAsync(server,
            new SubathonEvent {
                Source = SubathonEventSource.Twitch, EventType = SubathonEventType.TwitchCheer,
                Currency = "bits", User = "alice", Value = "500", PointsValue = 5
            },
            new SubathonEvent {
                Source = SubathonEventSource.Twitch, EventType = SubathonEventType.TwitchCheer,
                Currency = "bits", User = "alice", Value = "250", PointsValue = 2
            });

        JsonElement root = await QueryAsync(server, "type=TwitchCheer");

        Assert.Equal("ByValue", root.GetProperty("method").GetString());
        Assert.Equal("tokens", root.GetProperty("unit").GetString());
        Assert.Equal(750, root.GetProperty("results")[0].GetProperty("value").GetDouble());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Donations_Default_To_ByAmount_In_Primary_Currency() {
        WebServer server = CreateServer();
        await SeedAsync(server,
            new SubathonEvent {
                Source = SubathonEventSource.StreamElements, EventType = SubathonEventType.StreamElementsDonation,
                Currency = "USD", User = "alice", Value = "10.50", PointsValue = 10
            },
            new SubathonEvent {
                Source = SubathonEventSource.StreamElements, EventType = SubathonEventType.StreamElementsDonation,
                Currency = "USD", User = "alice", Value = "4.50", PointsValue = 4
            });

        JsonElement root = await QueryAsync(server, "type=StreamElementsDonation");

        Assert.Equal("ByAmount", root.GetProperty("method").GetString());
        Assert.Equal("USD", root.GetProperty("unit").GetString());
        Assert.Equal("USD", root.GetProperty("currency").GetString());
        Assert.Equal(15, root.GetProperty("results")[0].GetProperty("value").GetDouble());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Donations_Report_Currencies_That_Could_Not_Convert() {
        WebServer server = CreateServer();
        await SeedAsync(server,
            new SubathonEvent {
                Source = SubathonEventSource.StreamElements, EventType = SubathonEventType.StreamElementsDonation,
                Currency = "EUR", User = "alice", Value = "10.00", PointsValue = 10
            });

        JsonElement root = await QueryAsync(server, "type=StreamElementsDonation");
        Assert.Equal("EUR", root.GetProperty("unconverted_currencies")[0].GetString());
        Assert.Equal(0, root.GetProperty("results")[0].GetProperty("value").GetDouble());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Orders_Support_ByOrder_ByItems_And_ByValue() {
        WebServer server = CreateServer();
        await SeedAsync(server,
            new SubathonEvent {
                Source = SubathonEventSource.KoFi, EventType = SubathonEventType.KoFiShopOrder,
                Currency = "items", User = "alice", Value = "3", Amount = 3,
                SecondaryValue = "30.00|USD", PointsValue = 5
            },
            new SubathonEvent {
                Source = SubathonEventSource.KoFi, EventType = SubathonEventType.KoFiShopOrder,
                Currency = "items", User = "alice", Value = "1", Amount = 1,
                SecondaryValue = "12.50|USD", PointsValue = 5
            });

        JsonElement byValue = await QueryAsync(server, "type=KoFiShopOrder");
        Assert.Equal("ByValue", byValue.GetProperty("method").GetString());
        Assert.Equal("USD", byValue.GetProperty("unit").GetString());
        Assert.Equal(42.5, byValue.GetProperty("results")[0].GetProperty("value").GetDouble());

        JsonElement byOrder = await QueryAsync(server, "type=KoFiShopOrder&method=byOrder");
        Assert.Equal("orders", byOrder.GetProperty("unit").GetString());
        Assert.Equal(2, byOrder.GetProperty("results")[0].GetProperty("value").GetDouble());

        JsonElement byItems = await QueryAsync(server, "type=KoFiShopOrder&method=byItems");
        Assert.Equal("items", byItems.GetProperty("unit").GetString());
        Assert.Equal(4, byItems.GetProperty("results")[0].GetProperty("value").GetDouble());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Orders_Do_Not_Multiply_Points_By_Quantity() {
        WebServer server = CreateServer();
        await SeedAsync(server,
            new SubathonEvent {
                Source = SubathonEventSource.KoFi, EventType = SubathonEventType.KoFiShopOrder,
                Currency = "items", User = "alice", Value = "4", Amount = 4,
                SecondaryValue = "40.00|USD", PointsValue = 7
            });

        JsonElement root = await QueryAsync(server, "type=KoFiShopOrder&method=byPoints");

        Assert.Equal(7, root.GetProperty("results")[0].GetProperty("value").GetDouble());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Method_Not_Valid_For_Category_Returns_400() {
        WebServer server = CreateServer();
        await SeedAsync(server, GiftSub("alice", "1000", 1, 10));

        (int code, string body) = await RawQueryAsync(server, "type=TwitchGiftSub&method=byItems");

        Assert.Equal(400, code);
        Assert.Contains("ByItems", body);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Unknown_Method_Returns_400() {
        WebServer server = CreateServer();
        await SeedAsync(server, GiftSub("alice", "1000", 1, 10));

        (int code, string body) = await RawQueryAsync(server, "type=TwitchGiftSub&method=byVibes");

        Assert.Equal(400, code);
        Assert.Contains("byVibes", body);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Unprocessed_Events_Are_Excluded_Unless_Requested() {
        WebServer server = CreateServer();
        var subathon = new SubathonData();
        await using (AppDbContext db =
                     await server._factory.CreateDbContextAsync(TestContext.Current.CancellationToken)) {
            db.SubathonDatas.Add(subathon);
            SubathonEvent processed = GiftSub("alice", "1000", 1, 10);
            processed.SubathonId = subathon.Id;
            processed.ProcessedToSubathon = true;
            SubathonEvent skipped = GiftSub("bob", "1000", 1, 10);
            skipped.SubathonId = subathon.Id;
            skipped.ProcessedToSubathon = false;
            db.SubathonEvents.AddRange(processed, skipped);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        JsonElement processedOnly = await QueryAsync(server, "type=TwitchGiftSub");
        Assert.Equal(1, processedOnly.GetProperty("user_count").GetInt32());

        JsonElement all = await QueryAsync(server, "type=TwitchGiftSub&includeUnprocessed=true");
        Assert.Equal(2, all.GetProperty("user_count").GetInt32());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Invalid_Currency_Returns_400() {
        WebServer server = CreateServer();
        await SeedAsync(server, GiftSub("alice", "1000", 1, 10));

        (int code, _) = await RawQueryAsync(server, "type=StreamElementsDonation&currency=DOLLARS");

        Assert.Equal(400, code);
        AppServices.Provider = null!;
    }

    private static SubathonEvent Donation(string user, string value, int points) {
        return new SubathonEvent {
            Source = SubathonEventSource.StreamElements,
            EventType = SubathonEventType.StreamElementsDonation,
            Currency = "USD",
            User = user,
            Value = value,
            PointsValue = points
        };
    }

    [Fact]
    public async Task Combined_Types_Sum_Points_Across_Types_Per_User() {
        WebServer server = CreateServer();
        await SeedAsync(server,
            GiftSub("Alice", "1000", 2, 10), // 20 points
            Donation("alice", "5.00", 7), // 7 points, same user
            GiftSub("bob", "1000", 1, 5)); // 5 points

        JsonElement root = await QueryAsync(server, "type=TwitchGiftSub,StreamElementsDonation");

        Assert.True(root.GetProperty("combined").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("event_type").ValueKind);
        Assert.Equal(2, root.GetProperty("event_types").GetArrayLength());
        Assert.Equal("ByPoints", root.GetProperty("method").GetString());
        Assert.Equal("points", root.GetProperty("unit").GetString());
        Assert.Equal(2, root.GetProperty("user_count").GetInt32());
        Assert.Equal(32, root.GetProperty("total").GetDouble());

        JsonElement results = root.GetProperty("results");
        Assert.Equal("alice", results[0].GetProperty("user").GetString(), true);
        Assert.Equal(27, results[0].GetProperty("value").GetDouble());
        Assert.Equal(2, results[0].GetProperty("events").GetInt32());
        Assert.Equal("bob", results[1].GetProperty("user").GetString(), true);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Combined_Types_Normalize_Casing_And_At_Prefix() {
        WebServer server = CreateServer();
        await SeedAsync(server,
            GiftSub("Alice", "1000", 1, 10),
            Donation("@alice", "5.00", 5),
            Donation(" ALICE ", "5.00", 5));

        JsonElement root = await QueryAsync(server, "type=TwitchGiftSub,StreamElementsDonation");

        Assert.Equal(1, root.GetProperty("user_count").GetInt32());
        Assert.Equal(20, root.GetProperty("results")[0].GetProperty("value").GetDouble());
        Assert.Equal(3, root.GetProperty("results")[0].GetProperty("events").GetInt32());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Combined_Types_Ignore_Meta() {
        WebServer server = CreateServer();
        await SeedAsync(server,
            GiftSub("alice", "2000", 1, 10),
            Donation("bob", "5.00", 5));

        JsonElement root = await QueryAsync(server,
            "type=TwitchGiftSub,StreamElementsDonation&meta=1000");

        Assert.True(root.GetProperty("meta_ignored").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("meta").ValueKind);
        Assert.Equal(2, root.GetProperty("user_count").GetInt32());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Combined_Types_Still_Apply_Blacklist_And_Top() {
        WebServer server = CreateServer();
        await SeedAsync(server,
            GiftSub("alice", "1000", 3, 10),
            Donation("bob", "5.00", 20),
            Donation("SYSTEM Twitch", "5.00", 100),
            GiftSub("carol", "1000", 1, 5));

        JsonElement root = await QueryAsync(server,
            "type=TwitchGiftSub,StreamElementsDonation&blacklist=SYSTEM*&top=2");

        Assert.Equal(3, root.GetProperty("user_count").GetInt32());
        Assert.Equal(55, root.GetProperty("total").GetDouble());

        JsonElement results = root.GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal("alice", results[0].GetProperty("user").GetString());
        Assert.Equal(30, results[0].GetProperty("value").GetDouble());
        Assert.Equal("bob", results[1].GetProperty("user").GetString());
        AppServices.Provider = null!;
    }

    [Theory]
    [InlineData("byCount")]
    [InlineData("byAmount")]
    [InlineData("byItems")]
    public async Task Combined_Types_Reject_Non_Points_Methods(string method) {
        WebServer server = CreateServer();
        await SeedAsync(server, GiftSub("alice", "1000", 1, 10));

        (int code, string body) = await RawQueryAsync(server,
            $"type=TwitchGiftSub,StreamElementsDonation&method={method}");

        Assert.Equal(400, code);
        Assert.Contains("ByPoints", body);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Combined_Types_Accept_Explicit_ByPoints() {
        WebServer server = CreateServer();
        await SeedAsync(server, GiftSub("alice", "1000", 1, 10), Donation("alice", "5.00", 5));

        JsonElement root = await QueryAsync(server,
            "type=TwitchGiftSub,StreamElementsDonation&method=byPoints");

        Assert.Equal(15, root.GetProperty("results")[0].GetProperty("value").GetDouble());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Combined_Types_Reject_An_Invalid_Entry() {
        WebServer server = CreateServer();
        await SeedAsync(server, GiftSub("alice", "1000", 1, 10));

        (int code, string body) = await RawQueryAsync(server, "type=TwitchGiftSub,NotAType");

        Assert.Equal(400, code);
        Assert.Contains("NotAType", body);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Repeated_Type_Param_Is_Deduplicated_To_Single_Mode() {
        WebServer server = CreateServer();
        await SeedAsync(server, GiftSub("alice", "1000", 1, 10));

        JsonElement root = await QueryAsync(server, "type=TwitchGiftSub&type=TwitchGiftSub");

        Assert.False(root.GetProperty("combined").GetBoolean());
        Assert.Equal("TwitchGiftSub", root.GetProperty("event_type").GetString());
        Assert.Equal(10, root.GetProperty("results")[0].GetProperty("value").GetDouble());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Alias_Attributes_Alternate_Name_To_Canonical_User() {
        WebServer server = CreateServer();
        await SeedAsync(server,
            GiftSub("doodleGuy", "1000", 1, 10),
            Donation("MayhemMan", "5.00", 25),
            GiftSub("bob", "1000", 1, 5));

        JsonElement root = await QueryAsync(server,
            "type=TwitchGiftSub,StreamElementsDonation&alias=doodleGuy:MayhemMan");

        Assert.Equal(2, root.GetProperty("user_count").GetInt32());

        JsonElement results = root.GetProperty("results");
        Assert.Equal("doodleGuy", results[0].GetProperty("user").GetString());
        Assert.Equal(35, results[0].GetProperty("value").GetDouble());
        Assert.Equal(2, results[0].GetProperty("events").GetInt32());

        JsonElement echoed = root.GetProperty("aliases").GetProperty("doodleGuy");
        Assert.Equal("MayhemMan", echoed[0].GetString());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Alias_Accepts_Multiple_Alternates_And_Repeated_Params() {
        WebServer server = CreateServer();
        await SeedAsync(server,
            GiftSub("doodleGuy", "1000", 1, 10),
            Donation("MayhemMan", "5.00", 5),
            Donation("dg_yt", "5.00", 5),
            Donation("@DoodleG", "5.00", 5));

        JsonElement root = await QueryAsync(server,
            "type=TwitchGiftSub,StreamElementsDonation&alias=doodleGuy:MayhemMan|dg_yt&alias=doodleGuy:DoodleG");

        Assert.Equal(1, root.GetProperty("user_count").GetInt32());
        Assert.Equal(25, root.GetProperty("results")[0].GetProperty("value").GetDouble());
        Assert.Equal(3, root.GetProperty("aliases").GetProperty("doodleGuy").GetArrayLength());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Alias_Matching_Is_Case_Insensitive() {
        WebServer server = CreateServer();
        await SeedAsync(server,
            GiftSub("doodleGuy", "1000", 1, 10),
            Donation("mayhemman", "5.00", 5));

        JsonElement root = await QueryAsync(server,
            "type=TwitchGiftSub,StreamElementsDonation&alias=DOODLEGUY:MayhemMan");

        Assert.Equal(1, root.GetProperty("user_count").GetInt32());
        Assert.Equal(15, root.GetProperty("results")[0].GetProperty("value").GetDouble());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Alias_Chains_Collapse_To_The_Final_Canonical() {
        WebServer server = CreateServer();
        await SeedAsync(server,
            GiftSub("doodleGuy", "1000", 1, 10),
            Donation("MayhemMan", "5.00", 5),
            Donation("dg_alt", "5.00", 5));

        // dg_alt -> MayhemMan -> doodleGuy
        JsonElement root = await QueryAsync(server,
            "type=TwitchGiftSub,StreamElementsDonation&alias=doodleGuy:MayhemMan,MayhemMan:dg_alt");

        Assert.Equal(1, root.GetProperty("user_count").GetInt32());
        Assert.Equal("doodleGuy", root.GetProperty("results")[0].GetProperty("user").GetString());
        Assert.Equal(20, root.GetProperty("results")[0].GetProperty("value").GetDouble());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Alias_Applies_In_Single_Type_Mode_Too() {
        WebServer server = CreateServer();
        await SeedAsync(server,
            GiftSub("doodleGuy", "1000", 2, 10),
            GiftSub("MayhemMan", "1000", 3, 10));

        JsonElement root = await QueryAsync(server, "type=TwitchGiftSub&alias=doodleGuy:MayhemMan");

        Assert.Equal(1, root.GetProperty("user_count").GetInt32());
        Assert.Equal(50, root.GetProperty("results")[0].GetProperty("value").GetDouble());
        Assert.Equal(5, root.GetProperty("results")[0].GetProperty("count").GetInt32());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Alias_Blacklist_Works_On_Either_Name() {
        WebServer server = CreateServer();
        await SeedAsync(server,
            GiftSub("doodleGuy", "1000", 1, 10),
            Donation("MayhemMan", "5.00", 5),
            GiftSub("bob", "1000", 1, 5));

        JsonElement byAlt = await QueryAsync(server,
            "type=TwitchGiftSub,StreamElementsDonation&alias=doodleGuy:MayhemMan&blacklist=MayhemMan");
        Assert.Equal(10, byAlt.GetProperty("results")[0].GetProperty("value").GetDouble());

        JsonElement byCanonical = await QueryAsync(server,
            "type=TwitchGiftSub,StreamElementsDonation&alias=doodleGuy:MayhemMan&blacklist=doodleGuy");
        Assert.Equal(1, byCanonical.GetProperty("user_count").GetInt32());
        Assert.Equal("bob", byCanonical.GetProperty("results")[0].GetProperty("user").GetString());
        AppServices.Provider = null!;
    }

    [Theory]
    [InlineData("alias=doodleGuy")]
    [InlineData("alias=:MayhemMan")]
    [InlineData("alias=doodleGuy:")]
    public async Task Alias_Malformed_Entry_Returns_400(string aliasQuery) {
        WebServer server = CreateServer();
        await SeedAsync(server, GiftSub("alice", "1000", 1, 10));

        (int code, string body) = await RawQueryAsync(server, $"type=TwitchGiftSub&{aliasQuery}");

        Assert.Equal(400, code);
        Assert.Contains("alias", body, StringComparison.OrdinalIgnoreCase);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Alias_Conflicting_Canonicals_Return_400() {
        WebServer server = CreateServer();
        await SeedAsync(server, GiftSub("alice", "1000", 1, 10));

        (int code, string body) = await RawQueryAsync(server,
            "type=TwitchGiftSub&alias=doodleGuy:MayhemMan,otherGuy:MayhemMan");

        Assert.Equal(400, code);
        Assert.Contains("MayhemMan", body);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Alias_Loop_Returns_400() {
        WebServer server = CreateServer();
        await SeedAsync(server, GiftSub("alice", "1000", 1, 10));

        (int code, string body) = await RawQueryAsync(server,
            "type=TwitchGiftSub&alias=a:b,b:c,c:a");

        Assert.Equal(400, code);
        Assert.Contains("loop", body, StringComparison.OrdinalIgnoreCase);
        AppServices.Provider = null!;
    }

    private static async Task<Guid> SeedInactiveAsync(WebServer server, string name, params SubathonEvent[] events) {
        var subathon = new SubathonData { IsActive = false, Name = name };
        await using AppDbContext db = await server._factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        db.SubathonDatas.Add(subathon);
        foreach (SubathonEvent ev in events) {
            ev.SubathonId = subathon.Id;
            ev.ProcessedToSubathon = true;
            db.SubathonEvents.Add(ev);
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return subathon.Id;
    }

    [Fact]
    public async Task Subathon_Param_Defaults_To_Active() {
        WebServer server = CreateServer();
        Guid active = await SeedAsync(server, GiftSub("alice", "1000", 1, 10));
        await SeedInactiveAsync(server, "Old One", GiftSub("bob", "1000", 1, 99));

        JsonElement root = await QueryAsync(server, "type=TwitchGiftSub");

        Assert.Equal(active.ToString(), root.GetProperty("subathon_id").GetString());
        Assert.True(root.GetProperty("subathon_active").GetBoolean());
        Assert.Equal(1, root.GetProperty("user_count").GetInt32());
        Assert.Equal("alice", root.GetProperty("results")[0].GetProperty("user").GetString());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Subathon_Param_Selects_A_Past_Subathon() {
        WebServer server = CreateServer();
        await SeedAsync(server, GiftSub("alice", "1000", 1, 10));
        Guid old = await SeedInactiveAsync(server, "Old One", GiftSub("bob", "1000", 1, 99));

        JsonElement root = await QueryAsync(server, $"type=TwitchGiftSub&subathon={old}");

        Assert.Equal(old.ToString(), root.GetProperty("subathon_id").GetString());
        Assert.Equal("Old One", root.GetProperty("subathon_name").GetString());
        Assert.False(root.GetProperty("subathon_active").GetBoolean());
        Assert.Equal("bob", root.GetProperty("results")[0].GetProperty("user").GetString());
        Assert.Equal(99, root.GetProperty("results")[0].GetProperty("value").GetDouble());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Subathon_Param_Accepts_Literal_Active() {
        WebServer server = CreateServer();
        Guid active = await SeedAsync(server, GiftSub("alice", "1000", 1, 10));
        await SeedInactiveAsync(server, "Old One", GiftSub("bob", "1000", 1, 99));

        JsonElement root = await QueryAsync(server, "type=TwitchGiftSub&subathon=active");

        Assert.Equal(active.ToString(), root.GetProperty("subathon_id").GetString());
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Subathon_Param_Invalid_Guid_Returns_400() {
        WebServer server = CreateServer();
        await SeedAsync(server, GiftSub("alice", "1000", 1, 10));

        (int code, string body) = await RawQueryAsync(server, "type=TwitchGiftSub&subathon=not-a-guid");

        Assert.Equal(400, code);
        Assert.Contains("subathon", body, StringComparison.OrdinalIgnoreCase);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Subathon_Param_Unknown_Id_Returns_400() {
        WebServer server = CreateServer();
        await SeedAsync(server, GiftSub("alice", "1000", 1, 10));
        var missing = Guid.NewGuid();

        (int code, string body) = await RawQueryAsync(server, $"type=TwitchGiftSub&subathon={missing}");

        Assert.Equal(400, code);
        Assert.Contains(missing.ToString(), body);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Amounts_Endpoint_Honours_Subathon_Param() {
        WebServer server = CreateServer();
        await SeedAsync(server, GiftSub("alice", "1000", 1, 10));
        Guid old = await SeedInactiveAsync(server, "Old One", GiftSub("bob", "1000", 1, 99));

        var ctx = new MockHttpContext {
            Method = "GET",
            Path = "/api/data/amounts",
            QueryString = $"subathon={old}"
        };
        await server.HandleAmountsRequestAsync(ctx);

        Assert.Equal(200, ctx.StatusCode);
        JsonElement root = JsonDocument.Parse(ctx.ResponseBody).RootElement;
        Assert.Equal(old.ToString(), root.GetProperty("subathon_id").GetString());
        Assert.Equal("Old One", root.GetProperty("subathon_name").GetString());
        Assert.False(root.GetProperty("subathon_active").GetBoolean());
        AppServices.Provider = null!;
    }
}
