using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Services;
using SubathonManager.Tests.Utility;

namespace SubathonManager.Tests.ServicesUnitTests;

[Collection("GlobalState")]
public class SubathonTimerServiceTests {
    private static async Task<SubathonData> TickOnceAsync(long cumulativeMs, bool paused) {
        (DbContextOptions<AppDbContext> options, IDbContextFactory<AppDbContext> factory, SqliteConnection connection) =
            TestDb.CreateSqlite();
        await using SqliteConnection __ = connection;

        var id = Guid.NewGuid();
        await using (var db = new AppDbContext(options)) {
            db.SubathonDatas.Add(new SubathonData
                { Id = id, IsActive = true, IsPaused = paused, IsLocked = false, MillisecondsCumulative = cumulativeMs });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var service = new SubathonTimerService(factory);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.TickAsync(TimeSpan.FromSeconds(1));

        await using var check = new AppDbContext(options);
        return await check.SubathonDatas.AsNoTracking().SingleAsync(x => x.Id == id,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Tick_CountsDown_WhenRunning() {
        SubathonData data = await TickOnceAsync(10_000, paused: false);
        Assert.Equal(1000, data.MillisecondsElapsed);
        Assert.False(data.IsLocked);
    }

    [Fact]
    public async Task Tick_DoesNothing_WhenPaused() {
        SubathonData data = await TickOnceAsync(10_000, paused: true);
        Assert.Equal(0, data.MillisecondsElapsed);
    }

    [Fact]
    public async Task Tick_LocksSubathon_WhenTimeRunsOut() {
        SubathonData data = await TickOnceAsync(500, paused: false);
        Assert.True(data.IsLocked);
    }
}
