using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json.Serialization;
using SubathonManager.Data;
using SubathonManager.Core.Models;
using SubathonManager.Core.Enums;

namespace SubathonManager.Tests.DataUnitTests;
public class SubathonValueConfigHelperTests
{
    private JsonSerializerOptions _jsonOptions = new()
    {
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        },
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    
    private IDbContextFactory<AppDbContext> CreateInMemoryFactory(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var db = new AppDbContext(options);

        db.SubathonValues.AddRange(
            new SubathonValue
            {
                EventType = SubathonEventType.TwitchSub,
                Meta = "1000",
                Seconds = 60,
                Points = 1
            },
            new SubathonValue
            {
                EventType = SubathonEventType.TwitchGiftSub,
                Meta = "2000",
                Seconds = 120,
                Points = 2
            },
            new SubathonValue
            {
                EventType = SubathonEventType.KoFiDonation,
                Meta = "DEFAULT",
                Seconds = 20,
                Points = 0
            }
        );
        db.SaveChanges();
        //db.ChangeTracker.Clear();

        var mockFactory = new Mock<IDbContextFactory<AppDbContext>>();
        mockFactory.Setup(f => f.CreateDbContext())
                   .Returns(db);
        mockFactory.Setup(f => f.CreateDbContextAsync(default))
                   .ReturnsAsync(db);

        return mockFactory.Object;
    }

    [Fact]
    public void GetAllAsJson_Returns_CorrectJson()
    {
        var factory = CreateInMemoryFactory("GetAllAsJsonDb");
        var logger = Mock.Of<ILogger<SubathonValueConfigHelper>>();
        var helper = new SubathonValueConfigHelper(factory, logger);

        var json = helper.GetAllAsJson();

        Assert.NotNull(json);

        
        var deserialized = JsonSerializer.Deserialize<List<SubathonValueDto>>(json, _jsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(3, deserialized.Count);
        Assert.Contains(deserialized, x => x.Meta == "1000" && x.Points == 1 
                                                            && x.EventType == SubathonEventType.TwitchSub);
        Assert.Contains(deserialized, x => x.Meta == "2000" && x.Points == 2
                                                            && x.EventType == SubathonEventType.TwitchGiftSub);
        Assert.Contains(deserialized, x => x.Meta == "DEFAULT" && x.Points == 0 
                                                               && x.EventType == SubathonEventType.KoFiDonation);
    }

    [Fact]
    public async Task GetAllAsJsonAsync_Returns_FilteredJson()
    {
        var factory = CreateInMemoryFactory("GetAllAsJsonAsyncDb");
        var logger = Mock.Of<ILogger<SubathonValueConfigHelper>>();
        var helper = new SubathonValueConfigHelper(factory, logger);

        var filter = new List<SubathonEventSource> { SubathonEventSource.Twitch };

        var json = await helper.GetAllAsJsonAsync(filter);

        var deserialized = JsonSerializer.Deserialize<List<SubathonValueDto>>(json, _jsonOptions);
        Assert.Equal(2, deserialized!.Count);
        Assert.Equal("1000", deserialized![0].Meta);
        Assert.Equal("2000", deserialized![1].Meta);
        Assert.Contains(deserialized, x => x.Meta == "1000" && x.Points == 1 
                                                            && x.EventType == SubathonEventType.TwitchSub);
        Assert.Contains(deserialized, x => x.Meta == "2000" && x.Points == 2
                                                            && x.EventType == SubathonEventType.TwitchGiftSub);
    }
}
