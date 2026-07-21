using System.Net;
using System.Numerics;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Moq;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Integration;
using SubathonManager.Tests.Utility;

namespace SubathonManager.Tests.IntegrationUnitTests;

[Collection("SharedEventBusTests")]
public class JuniperServiceTests
{
    private const string StoreName = "shop.example.com";
    private const string ProductIdA = "8123456765432";
    private const string ProductIdB = "9000187654321";

    public JuniperServiceTests()
    {
        typeof(IntegrationEvents)
            .GetField("ConnectionUpdated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        foreach (var eventName in new[] { "StoreDiscovered", "StoreUpdated", "ProductUpdated" })
        {
            typeof(JuniperStoreRegistry)
                .GetField(eventName, BindingFlags.Static | BindingFlags.NonPublic)
                ?.SetValue(null, null);
        }
        JuniperStoreRegistry.Initialize([]);
    }

    private static JuniperStore MakeStore(string name = StoreName)
        => new() { RowId = Guid.NewGuid(), StoreName = name };

    private static JuniperProduct MakeProduct(JuniperStore store, string productId, string name = "Cool Tee",
        bool valid = true)
    {
        var product = new JuniperProduct
        {
            ProductId = BigInteger.Parse(productId),
            StoreId = store.RowId,
            Store = store,
            ProductName = name,
            Valid = valid
        };
        store.Products.Add(product);
        return product;
    }

    private static JuniperService MakeService(RoutedHttpHandler handler)
    {
        var logger = new Mock<ILogger<JuniperService>>();
        var mock = new Mock<IHttpClientFactory>();
        mock.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler));
        return new JuniperService(logger.Object, mock.Object, timerService: null);
    }

    private static string OrdersSumBody(params (string ProductId, int UnitsSold)[] sums)
    {
        var details = string.Join(",", sums.Select(s =>
            $$""""
            "{{s.ProductId}}":{"units_sold":{{s.UnitsSold}},"total_amount":12.34}
            """".Trim()));
        return $$"""{"products_details": { {{details}} } }""";
    }

    private static async Task<List<SubathonEvent>> CaptureEventsAsync(Func<Task> action)
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        var captured = new List<SubathonEvent>();
        void Handler(SubathonEvent e) => captured.Add(e);
        SubathonEvents.SubathonEventCreated += Handler;
        try
        {
            await action();
            return captured;
        }
        finally
        {
            SubathonEvents.SubathonEventCreated -= Handler;
        }
    }
    
    [Theory]
    [InlineData("8123456765432", true)]
    [InlineData("123456789012345678901234567890", true)]
    [InlineData(" 8123456765432 ", true)]
    [InlineData("0", false)]
    [InlineData("-5", false)]
    [InlineData("+5", false)]
    [InlineData("12a", false)]
    [InlineData("1.5", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryParseProductId_RequiresPositiveBigInteger(string? value, bool expected)
    {
        Assert.Equal(expected, JuniperStoreRegistry.TryParseProductId(value, out _));
    }

    [Theory]
    [InlineData("https://shop.example.com/p/8123456765432", StoreName, ProductIdA)]
    [InlineData("https://SHOP.Example.com/p/8123456765432/some-variant", StoreName, ProductIdA)]
    [InlineData("https://shop.example.com/p/8123456765432?dfgjhfdgh=fdfdjg", StoreName, ProductIdA)]
    [InlineData("http://shop.example.com/store/p/8123456765432#frag", StoreName, ProductIdA)]
    public void TryParseProductUrl_ExtractsStoreAndId(string url, string expectedStore, string expectedId)
    {
        Assert.True(JuniperStoreRegistry.TryParseProductUrl(url, out var storeName, out var productId));
        Assert.Equal(expectedStore, storeName);
        Assert.Equal(BigInteger.Parse(expectedId), productId);
    }

    [Theory]
    [InlineData("https://shop.example.com/products/8123456765432")]
    [InlineData("https://shop.example.com/p/")]
    [InlineData("https://shop.example.com/p/not-a-number")]
    [InlineData("ftp://shop.example.com/p/8123456765432")]
    [InlineData("/p/8123456765432")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseProductUrl_RejectsInvalidUrls(string? url)
    {
        Assert.False(JuniperStoreRegistry.TryParseProductUrl(url, out _, out _));
    }

    [Fact]
    public void AllValidProductIds_SkipsInvalidProducts()
    {
        var store = MakeStore();
        MakeProduct(store, ProductIdA);
        MakeProduct(store, ProductIdB, name: "Broken Tee", valid: false);
        JuniperStoreRegistry.Initialize([store]);

        var ids = JuniperStoreRegistry.AllValidProductIds();

        Assert.Single(ids);
        Assert.Equal(BigInteger.Parse(ProductIdA), ids[0]);
    }

    [Fact]
    public void GetOrProvisionStore_ReusesExistingByNameCaseInsensitive()
    {
        var store = MakeStore();
        JuniperStoreRegistry.Initialize([store]);
        var discovered = new List<JuniperStore>();
        JuniperStoreRegistry.StoreDiscovered += discovered.Add;

        var same = JuniperStoreRegistry.GetOrProvisionStore("SHOP.EXAMPLE.COM");
        var fresh = JuniperStoreRegistry.GetOrProvisionStore("other.store.com");

        Assert.Same(store, same);
        Assert.Single(discovered);
        Assert.Equal("other.store.com", fresh.StoreName);
        Assert.NotEqual(Guid.Empty, fresh.RowId);
        Assert.True(JuniperStoreRegistry.TryGetStore(fresh.RowId, out _));
    }

    [Fact]
    public void RemoveStore_DropsItsProductsFromTheIndex()
    {
        var store = MakeStore();
        MakeProduct(store, ProductIdA);
        JuniperStoreRegistry.Initialize([store]);

        JuniperStoreRegistry.RemoveStore(store.RowId);

        Assert.False(JuniperStoreRegistry.TryGetStore(store.RowId, out _));
        Assert.False(JuniperStoreRegistry.TryGetProduct(ProductIdA, out _));
        Assert.Empty(JuniperStoreRegistry.AllValidProductIds());
    }

    [Fact]
    public void MakeQueryUrls_BatchesTenIdsPerUrlWithFixedEndTime()
    {
        var ids = Enumerable.Range(1, 25).Select(i => new BigInteger(1000000000000 + i)).ToList();
        var start = new DateTime(2026, 7, 15, 10, 30, 0, DateTimeKind.Utc);

        var urls = JuniperService.MakeQueryUrls(ids, start);

        Assert.Equal(3, urls.Count);
        Assert.All(urls, u => Assert.StartsWith(JuniperService.OrdersSumBase, u));
        Assert.All(urls, u => Assert.Contains("startTime=2026-07-15T10:30:00.000Z", u));
        Assert.All(urls, u => Assert.EndsWith($"endTime={JuniperService.FixedEndTime}", u));

        Assert.Contains($"productIds={string.Join(",", ids.Take(10))}&", urls[0]);
        Assert.Contains($"productIds={string.Join(",", ids.Skip(10).Take(10))}&", urls[1]);
        Assert.Contains($"productIds={string.Join(",", ids.Skip(20))}&", urls[2]);
    }

    [Fact]
    public void MakeQueryUrls_NoIds_YieldsNoUrls()
    {
        Assert.Empty(JuniperService.MakeQueryUrls([], DateTime.UtcNow));
    }

    [Fact]
    public async Task Poll_UnitsSoldInWindow_RaisesEventPerProduct()
    {
        var store = MakeStore();
        MakeProduct(store, ProductIdA, "Cool Tee");
        JuniperStoreRegistry.Initialize([store]);

        var handler = new RoutedHttpHandler
        {
            [JuniperService.OrdersSumBase] = (HttpStatusCode.OK, OrdersSumBody((ProductIdA, 3)))
        };
        var service = MakeService(handler);
        await service.StartAsync(TestContext.Current.CancellationToken);

        var events = await CaptureEventsAsync(() => service.PollAsync(CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        Assert.Contains($"productIds={ProductIdA}", request);
        Assert.Contains($"endTime={JuniperService.FixedEndTime}", request);

        var ev = Assert.Single(events);
        Assert.Equal(SubathonEventSource.JuniperCreates, ev.Source);
        Assert.Equal(SubathonEventType.JuniperMerchSale, ev.EventType);
        Assert.Equal(ProductIdA, ev.EventTypeMeta);
        Assert.Equal("sales", ev.Currency);
        Assert.Equal("3", ev.Value);
        Assert.Equal(3, ev.Amount);
        Assert.Equal("Cool Tee", ev.TertiaryValue);
        Assert.Equal(StoreName, ev.User);
        Assert.True(service.Connected);
    }

    [Fact]
    public async Task Poll_ZeroUnitsSold_RaisesNothing()
    {
        var store = MakeStore();
        MakeProduct(store, ProductIdA);
        JuniperStoreRegistry.Initialize([store]);

        var handler = new RoutedHttpHandler
        {
            [JuniperService.OrdersSumBase] = (HttpStatusCode.OK, OrdersSumBody((ProductIdA, 0)))
        };
        var service = MakeService(handler);
        await service.StartAsync(TestContext.Current.CancellationToken);

        var events = await CaptureEventsAsync(() => service.PollAsync(CancellationToken.None));

        Assert.Empty(events);
        Assert.True(service.Connected);
    }

    [Fact]
    public async Task Poll_UnknownProductInResponse_IsIgnored()
    {
        var store = MakeStore();
        MakeProduct(store, ProductIdA, "Cool Tee");
        JuniperStoreRegistry.Initialize([store]);

        var handler = new RoutedHttpHandler
        {
            [JuniperService.OrdersSumBase] =
                (HttpStatusCode.OK, OrdersSumBody((ProductIdB, 5), (ProductIdA, 2)))
        };
        var service = MakeService(handler);
        await service.StartAsync(TestContext.Current.CancellationToken);

        var events = await CaptureEventsAsync(() => service.PollAsync(CancellationToken.None));

        var ev = Assert.Single(events);
        Assert.Equal(ProductIdA, ev.EventTypeMeta);
        Assert.Equal(2, ev.Amount);
    }

    [Fact]
    public async Task Poll_ManyProducts_SplitsIntoBatchedRequests()
    {
        var store = MakeStore();
        for (int i = 0; i < 12; i++)
            MakeProduct(store, $"{1000000000000 + i}", $"Tee {i}");
        JuniperStoreRegistry.Initialize([store]);

        var handler = new RoutedHttpHandler
        {
            [JuniperService.OrdersSumBase] = (HttpStatusCode.OK, """{"products_details":{}}""")
        };
        var service = MakeService(handler);
        await service.StartAsync(TestContext.Current.CancellationToken);

        await service.PollAsync(CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.True(service.Connected);
    }

    [Fact]
    public async Task Poll_ErrorResponse_MarksDisconnectedUntilNextSuccess()
    {
        var store = MakeStore();
        MakeProduct(store, ProductIdA);
        JuniperStoreRegistry.Initialize([store]);

        var handler = new RoutedHttpHandler
        {
            [JuniperService.OrdersSumBase] = (HttpStatusCode.InternalServerError, null)
        };
        var service = MakeService(handler);
        await service.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(service.Connected);

        await service.PollAsync(CancellationToken.None);
        Assert.False(service.Connected);

        handler[JuniperService.OrdersSumBase] = (HttpStatusCode.OK, """{"products_details":{}}""");
        await service.PollAsync(CancellationToken.None);
        Assert.True(service.Connected);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"unexpected":"shape"}""")]
    [InlineData("""{"products_details":[1,2,3]}""")]
    public void HandleOrdersResponse_BadOrUnexpectedBody_ReturnsFalse(string body)
    {
        var service = MakeService(new RoutedHttpHandler());
        Assert.False(service.HandleOrdersResponse(body, DateTime.UtcNow));
    }

    [Fact]
    public void HandleOrdersResponse_MissingUnitsSold_SkipsProductWithoutThrowing()
    {
        var store = MakeStore();
        MakeProduct(store, ProductIdA);
        JuniperStoreRegistry.Initialize([store]);
        var service = MakeService(new RoutedHttpHandler());

        bool ok = service.HandleOrdersResponse(
            $$"""{"products_details": { "{{ProductIdA}}": {"total_amount":9.99} } }""", DateTime.UtcNow);

        Assert.True(ok);
    }

    [Fact]
    public void Simulate_KnownProduct_UsesProductNameAndStore()
    {
        var store = MakeStore();
        MakeProduct(store, ProductIdA, "Cool Tee");
        JuniperStoreRegistry.Initialize([store]);

        var ev = EventUtil.SubathonEventCapture.CaptureRequired(
            () => JuniperService.Simulate(ProductIdA, 4));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventSource.Simulated, ev!.Source);
        Assert.Equal(SubathonEventType.JuniperMerchSale, ev.EventType);
        Assert.Equal(ProductIdA, ev.EventTypeMeta);
        Assert.Equal("sales", ev.Currency);
        Assert.Equal("4", ev.Value);
        Assert.Equal(4, ev.Amount);
        Assert.Equal("Cool Tee", ev.TertiaryValue);
        Assert.Equal(StoreName, ev.User);
    }

    [Fact]
    public void Simulate_UnknownMeta_FallsBackToTestProductAndClampsCount()
    {
        var ev = EventUtil.SubathonEventCapture.CaptureRequired(
            () => JuniperService.Simulate("DEFAULT", 0));

        Assert.NotNull(ev);
        Assert.Equal("DEFAULT", ev!.EventTypeMeta);
        Assert.Equal("1", ev.Value);
        Assert.Equal(1, ev.Amount);
        Assert.Equal("Test Product", ev.TertiaryValue);
        Assert.Equal("JuniperCreates Store", ev.User);
    }
    
    [Fact]
    public async Task Start_NoProducts_BroadcastsUnconfigured()
    {
        var captured = new List<IntegrationConnection>();
        IntegrationEvents.ConnectionUpdated += captured.Add;

        var service = MakeService(new RoutedHttpHandler());
        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(service.Connected);
        var main = Assert.Single(captured,
            c => c.Service == nameof(SubathonEventSource.JuniperCreates));
        Assert.False(main.Status);
        Assert.False(main.Configured);
    }

    [Fact]
    public async Task Start_WithProducts_BroadcastsMainAndPerStoreRows()
    {
        var store = MakeStore();
        MakeProduct(store, ProductIdA);
        JuniperStoreRegistry.Initialize([store]);

        var captured = new List<IntegrationConnection>();
        IntegrationEvents.ConnectionUpdated += captured.Add;

        var service = MakeService(new RoutedHttpHandler());
        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(service.Connected);
        var main = Assert.Single(captured,
            c => c.Service == nameof(SubathonEventSource.JuniperCreates));
        Assert.True(main.Status);
        Assert.True(main.Configured);

        var storeRow = Assert.Single(captured, c => c.Service == StoreName);
        Assert.True(storeRow.Status);
        Assert.True(storeRow.Configured);
        Assert.Equal("1 tracked", storeRow.Name);
    }

    private sealed class RoutedHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string? Body)> _routes = new();
        public List<string> Requests { get; } = new();

        public (HttpStatusCode, string?) this[string urlPrefix]
        {
            set => _routes[urlPrefix] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);

            var match = _routes.FirstOrDefault(r => url.StartsWith(r.Key, StringComparison.OrdinalIgnoreCase));
            if (match.Key == null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var response = new HttpResponseMessage(match.Value.Status);
            if (match.Value.Body != null)
                response.Content = new StringContent(match.Value.Body, Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }
}
