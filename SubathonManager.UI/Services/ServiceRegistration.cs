using System.IO;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Interfaces;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.Server;
using SubathonManager.Services;

namespace SubathonManager.UI.Services;

public static class ServiceRegistration
{
    public static void SetupInfrastructure(this IServiceCollection services)
    {
        services.AddLogging(ConfigureLogging);
        services.AddSingleton<IConfig, Config>();
        services.AddDbContextFactory<AppDbContext>(ConfigureDatabase);
        services.AddSingleton<ServiceManager>();
    }

    public static void SetupCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<TimerService>();
        services.AddHttpClient(nameof(CurrencyService)).SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        services.AddSingleton<CurrencyService>(BuildCurrencyService);
        services.AddSingleton<EventService>();
        services.AddSingleton<WebServer>();

    }
    
    public static void AddIntegrations(this IServiceCollection services)
    {
        // Platforms ///
        services.AddSingleton<TwitchService>();
        services.AddSingleton<YouTubeService>();
        services.AddSingleton<PicartoService>();
        
        // Auxiliary //
        services.AddSingleton<StreamElementsService>();
        services.AddSingleton<StreamLabsService>();
        
        // Order Sales //
        services.AddSingleton<GoAffProService>();
        
        // Other //
        services.AddSingleton<DiscordWebhookService>();
    }

    private static void ConfigureLogging(ILoggingBuilder builder)
    {
        builder.ClearProviders();
        builder.AddProvider(new RotatingFileLoggerProvider("data/logs"));
        builder.AddSimpleConsole(options =>
        {
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
            options.SingleLine = true;
            options.IncludeScopes = false;
        });
        builder.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Error);
        builder.AddFilter("StreamLabs.SocketClient", LogLevel.Warning);
        builder.AddFilter("Microsoft.Extensions.Http", LogLevel.Warning);
        builder.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        builder.AddFilter("YTLiveChat", LogLevel.Warning);
        builder.AddFilter("YTLiveChat.Services.YTLiveChat", LogLevel.Error);
        builder.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(
            "YTLiveChat",
            LogLevel.Critical);
        builder.SetMinimumLevel(ServiceManager.AppVersion.Contains("dev") ? LogLevel.Debug : LogLevel.Information); 
    }
    
    private static void ConfigureDatabase(IServiceProvider sp, DbContextOptionsBuilder options) 
    {
        var dbPath = Config.DatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        options.UseSqlite($"Data Source={dbPath}");
    }
    
    private static CurrencyService BuildCurrencyService(IServiceProvider sp) =>
        new(sp.GetRequiredService<ILogger<CurrencyService>>(),
            sp.GetRequiredService<IConfig>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(CurrencyService)));
}