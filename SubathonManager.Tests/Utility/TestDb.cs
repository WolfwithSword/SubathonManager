using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core.Interfaces;
using SubathonManager.Data;

namespace SubathonManager.Tests.Utility;

public static class TestDb {
    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext> {
        public AppDbContext CreateDbContext() {
            return new AppDbContext(options);
        }
    }

    public static (DbContextOptions<AppDbContext> Options, IDbContextFactory<AppDbContext> Factory,
        SqliteConnection Connection) CreateSqlite() {
        var connectionString = $"DataSource=test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
        var connection = new SqliteConnection(connectionString);
        connection.Open();

        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        using var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return (options, new Factory(options), connection);
    }

    public static IDbContextFactory<AppDbContext> CreateInMemory(string? dbName = null) {
        return new Factory(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options);
    }

    public static IDbContextFactory<AppDbContext> SetupAppServices(IConfig config) {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton(config);
        AppServices.Provider = services.BuildServiceProvider();
        return AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    }
}
