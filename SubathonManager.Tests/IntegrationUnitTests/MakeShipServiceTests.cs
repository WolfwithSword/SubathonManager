using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Moq;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Integration;
using SubathonManager.Tests.Utility;

namespace SubathonManager.Tests.IntegrationUnitTests;

[Collection("SharedEventBusTests")]
public class MakeShipServiceTests
{
    private const string PetitionUrl = "https://www.makeship.com/petitions/cool-plush";
    private const string CampaignUrl = "https://www.makeship.com/products/cool-jumbo-plush";
    private const string PetitionProductId = "1234567890123";
    private const string CampaignProductId = "98765432111220";

    private const string PetitionHtml =
        $"""<html><body><div id="preproduct-pledge" data-id="{PetitionProductId}" class="x"></div></body></html>""";

    private const string CampaignHtml =
        $$"""<html><script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"handle":"cool-jumbo-plush","product":{"id":"gid://shopify/Product/{{CampaignProductId}}"} } } }</script></html>""";

    public MakeShipServiceTests()
    {
        typeof(IntegrationEvents)
            .GetField("ConnectionUpdated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(MakeShipTrackingRegistry)
            .GetField("TrackingUpdated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        MakeShipTrackingRegistry.Initialize([]);
    }

    private static MakeShipService MakeService(RoutedHttpHandler handler)
    {
        var logger = new Mock<ILogger<MakeShipService>>();
        var mock = new Mock<IHttpClientFactory>();
        mock.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler));
        return new MakeShipService(logger.Object, mock.Object, timerService: null);
    }

    private static RoutedHttpHandler PetitionHandler(int sales, int pledges, string name = "Cool Plush!") => new()
    {
        [PetitionUrl] = (HttpStatusCode.OK, PetitionHtml),
        [$"https://api.preproduct.io/api/preproducts/{PetitionProductId}.json"] =
            (HttpStatusCode.OK, $$"""{"preproduct":{"name":"{{name}}","sales_actual":{{sales}} } }"""),
        [$"https://storefront.makeship.com/orders/petitions/{PetitionProductId}/pledges/count"] =
            (HttpStatusCode.OK, $"{pledges}")
    };

    private static RoutedHttpHandler CampaignHandler(int salesQuantity, int pledges) => new()
    {
        [CampaignUrl] = (HttpStatusCode.OK, CampaignHtml),
        [$"https://storefront.makeship.com/products/{CampaignProductId}/sales-quantity"] =
            (HttpStatusCode.OK, $$"""{"quantity":{{salesQuantity}}}"""),
        [$"https://storefront.makeship.com/orders/petitions/{CampaignProductId}/pledges/count"] =
            (HttpStatusCode.OK, $"{pledges}")
    };

    private static async Task<List<SubathonEvent>> CaptureEventsFromPoll(MakeShipService service, MakeShipTracking tracking)
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        var captured = new List<SubathonEvent>();
        void Handler(SubathonEvent e) => captured.Add(e);
        SubathonEvents.SubathonEventCreated += Handler;
        try
        {
            await service.PollTrackingAsync(tracking, CancellationToken.None);
            return captured;
        }
        finally
        {
            SubathonEvents.SubathonEventCreated -= Handler;
        }
    }

    [Theory]
    [InlineData(PetitionUrl, MakeShipProductType.Petition)]
    [InlineData(CampaignUrl, MakeShipProductType.Campaign)]
    [InlineData("https://www.makeship.com/shop/some-plush", MakeShipProductType.Invalid)]
    [InlineData("https://example.com/petitions/whatever", MakeShipProductType.Invalid)]
    [InlineData("https://www.makeship.com/petitions/", MakeShipProductType.Invalid)]
    [InlineData("", MakeShipProductType.Invalid)]
    [InlineData(null, MakeShipProductType.Invalid)]
    public void ClassifyUrl_ReturnsExpectedType(string? url, MakeShipProductType expected)
    {
        Assert.Equal(expected, MakeShipTrackingRegistry.ClassifyUrl(url));
    }

    [Theory]
    [InlineData(PetitionUrl, "cool-plush")]
    [InlineData(PetitionUrl + "/", "cool-plush")]
    [InlineData(PetitionUrl + "?utm_source=x", "cool-plush")]
    [InlineData("", "")]
    public void GetSlug_ExtractsLastSegment(string url, string expected)
    {
        Assert.Equal(expected, MakeShipTrackingRegistry.GetSlug(url));
    }

    [Fact]
    public void GetDisplayNameFromSlug_TitleCasesSlug()
    {
        Assert.Equal("Cool Jumbo Plush", MakeShipTrackingRegistry.GetDisplayNameFromSlug(CampaignUrl));
        Assert.Equal("Cool Plush", MakeShipTrackingRegistry.GetDisplayNameFromSlug(PetitionUrl));
    }

    [Fact]
    public async Task Petition_FirstSync_ResolvesAndBaselinesWithoutEvents()
    {
        var tracking = new MakeShipTracking { Url = PetitionUrl };
        MakeShipTrackingRegistry.Upsert(tracking);
        var service = MakeService(PetitionHandler(sales: 190, pledges: 190));

        var events = await CaptureEventsFromPoll(service, tracking);

        Assert.Empty(events);
        Assert.Equal(MakeShipProductType.Petition, tracking.ProductType);
        Assert.Equal(PetitionProductId, tracking.ShopifyProductId);
        Assert.Equal("Cool Plush!", tracking.Name);
        Assert.Equal(190, tracking.Sales);
        Assert.Equal(190, tracking.Orders);
    }

    [Fact]
    public async Task Petition_ObservedPledgeIncrease_RaisesSingleEventWithDelta()
    {
        var tracking = new MakeShipTracking { Url = PetitionUrl };
        MakeShipTrackingRegistry.Upsert(tracking);
        var service = MakeService(PetitionHandler(sales: 190, pledges: 190));

        await service.PollTrackingAsync(tracking, CancellationToken.None);

        var events = await CaptureEventsFromPoll(
            MakeServiceReusingSync(service, PetitionHandler(sales: 191, pledges: 197)), tracking);

        var ev = Assert.Single(events);
        Assert.Equal(SubathonEventType.MakeShipPledge, ev.EventType);
        Assert.Equal(SubathonEventSource.MakeShip, ev.Source);
        Assert.Equal("7", ev.Value);
        Assert.Equal(7, ev.Amount);
        Assert.Equal("pledges", ev.Currency);
        Assert.Equal("Cool Plush!", ev.EventTypeMeta);
        Assert.Equal("Cool Plush!", ev.TertiaryValue);
        Assert.Equal(197, tracking.Orders);
    }

    [Fact]
    public async Task Petition_NoChange_RaisesNothing()
    {
        var tracking = new MakeShipTracking { Url = PetitionUrl };
        MakeShipTrackingRegistry.Upsert(tracking);
        var service = MakeService(PetitionHandler(sales: 190, pledges: 190));

        await service.PollTrackingAsync(tracking, CancellationToken.None);
        var events = await CaptureEventsFromPoll(service, tracking);

        Assert.Empty(events);
    }

    [Fact]
    public async Task Petition_AlreadyResolvedFromDb_FirstSyncIsStillBaseline()
    {
        var tracking = new MakeShipTracking
        {
            Url = PetitionUrl,
            Name = "Cool Plush!",
            ShopifyProductId = PetitionProductId,
            ProductType = MakeShipProductType.Petition,
            Sales = 150,
            Orders = 150
        };
        MakeShipTrackingRegistry.Upsert(tracking);
        var service = MakeService(PetitionHandler(sales: 190, pledges: 197));

        var events = await CaptureEventsFromPoll(service, tracking);

        Assert.Empty(events);
        Assert.Equal(197, tracking.Orders);

        var second = await CaptureEventsFromPoll(
            MakeServiceReusingSync(service, PetitionHandler(sales: 190, pledges: 199)), tracking);
        var ev = Assert.Single(second);
        Assert.Equal(2, ev.Amount);
    }

    [Fact]
    public async Task Campaign_FirstSync_ResolvesAndBaselines()
    {
        var tracking = new MakeShipTracking { Url = CampaignUrl };
        MakeShipTrackingRegistry.Upsert(tracking);
        var service = MakeService(CampaignHandler(salesQuantity: 800, pledges: 42));

        var events = await CaptureEventsFromPoll(service, tracking);

        Assert.Empty(events);
        Assert.Equal(MakeShipProductType.Campaign, tracking.ProductType);
        Assert.Equal(CampaignProductId, tracking.ShopifyProductId);
        Assert.Equal("Cool Jumbo Plush", tracking.Name); 
        Assert.Equal(800, tracking.Sales);
        Assert.Equal(42, tracking.Orders);
    }

    [Fact]
    public async Task Campaign_SalesIncrease_RaisesOrderEventWithDelta()
    {
        var tracking = new MakeShipTracking { Url = CampaignUrl };
        MakeShipTrackingRegistry.Upsert(tracking);
        var service = MakeService(CampaignHandler(salesQuantity: 800, pledges: 42));
        await service.PollTrackingAsync(tracking, CancellationToken.None);

        var events = await CaptureEventsFromPoll(
            MakeServiceReusingSync(service, CampaignHandler(salesQuantity: 805, pledges: 42)), tracking);

        var ev = Assert.Single(events);
        Assert.Equal(SubathonEventType.MakeShipOrder, ev.EventType);
        Assert.Equal("5", ev.Value);
        Assert.Equal(5, ev.Amount);
        Assert.Equal("items", ev.Currency);
        Assert.Equal(805, tracking.Sales);
    }

    [Fact]
    public async Task Campaign_PledgeCountChangeAlone_RaisesNothing()
    {
        var tracking = new MakeShipTracking { Url = CampaignUrl };
        MakeShipTrackingRegistry.Upsert(tracking);
        var service = MakeService(CampaignHandler(salesQuantity: 800, pledges: 42));
        await service.PollTrackingAsync(tracking, CancellationToken.None);

        var events = await CaptureEventsFromPoll(
            MakeServiceReusingSync(service, CampaignHandler(salesQuantity: 800, pledges: 99)), tracking);

        Assert.Empty(events);
        Assert.Equal(99, tracking.Orders);
    }

    [Fact]
    public async Task Campaign_NoSalesQuantity_MarkedInvalid()
    {
        var tracking = new MakeShipTracking { Url = CampaignUrl };
        MakeShipTrackingRegistry.Upsert(tracking);
        var handler = new RoutedHttpHandler
        {
            [CampaignUrl] = (HttpStatusCode.OK, CampaignHtml),
            [$"https://storefront.makeship.com/products/{CampaignProductId}/sales-quantity"] =
                (HttpStatusCode.NotFound, null)
        };
        var service = MakeService(handler);

        var events = await CaptureEventsFromPoll(service, tracking);

        Assert.Empty(events);
        Assert.Equal(MakeShipProductType.Invalid, tracking.ProductType);
    }

    [Fact]
    public async Task InvalidUrl_MarkedInvalidWithoutAnyHttpRequests()
    {
        var tracking = new MakeShipTracking { Url = "https://www.makeship.com/about" };
        MakeShipTrackingRegistry.Upsert(tracking);
        var handler = new RoutedHttpHandler();
        var service = MakeService(handler);

        var events = await CaptureEventsFromPoll(service, tracking);

        Assert.Empty(events);
        Assert.Equal(MakeShipProductType.Invalid, tracking.ProductType);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PetitionPage_WithoutPledgeDiv_MarkedInvalid()
    {
        var tracking = new MakeShipTracking { Url = PetitionUrl };
        MakeShipTrackingRegistry.Upsert(tracking);
        var handler = new RoutedHttpHandler
        {
            [PetitionUrl] = (HttpStatusCode.OK, "<html><body>nope</body></html>")
        };
        var service = MakeService(handler);

        await service.PollTrackingAsync(tracking, CancellationToken.None);

        Assert.Equal(MakeShipProductType.Invalid, tracking.ProductType);
    }

    [Fact]
    public void Simulate_Pledge_RaisesSimulatedEventWithQuantity()
    {
        var ev = EventUtil.SubathonEventCapture.CaptureRequired(
            () => MakeShipService.Simulate("Test 123", isPetition: true, count: 7));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventSource.Simulated, ev!.Source);
        Assert.Equal(SubathonEventType.MakeShipPledge, ev.EventType);
        Assert.Equal("7", ev.Value);
        Assert.Equal(7, ev.Amount);
        Assert.Equal("pledges", ev.Currency);
        Assert.Equal("Test 123", ev.EventTypeMeta);
        Assert.Equal("Test 123", ev.TertiaryValue);
    }

    [Fact]
    public void Simulate_Campaign_DefaultsNameAndClampsCount()
    {
        var ev = EventUtil.SubathonEventCapture.CaptureRequired(
            () => MakeShipService.Simulate("  ", isPetition: false, count: 0));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.MakeShipOrder, ev!.EventType);
        Assert.Equal("1", ev.Value);
        Assert.Equal(1, ev.Amount);
        Assert.Equal("items", ev.Currency);
        Assert.Equal("Test Plush", ev.EventTypeMeta);
    }

    private static MakeShipService MakeServiceReusingSync(MakeShipService service, RoutedHttpHandler handler)
    {
        var factoryField = typeof(MakeShipService)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .First(f => f.FieldType == typeof(IHttpClientFactory));
        var mock = new Mock<IHttpClientFactory>();
        mock.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler));
        factoryField.SetValue(service, mock.Object);
        return service;
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
