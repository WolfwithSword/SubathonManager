using Updatum;
using System.Reflection;
using Microsoft.Extensions.Logging;
namespace SubathonManager.Core;

public static class AppServices
{
    public static IServiceProvider Provider { get; set; } = default!;
    
    public static readonly string AppVersion = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion ?? "dev";

    private static readonly UpdatumManager AppUpdater = new("WolfwithSword", "SubathonManager")
    {
        AssetExtensionFilter = "zip",
        CurrentVersion = GetVersion(),
        FetchOnlyLatestRelease = true
    };

    private static Version GetVersion()
    {
        if (string.IsNullOrWhiteSpace(AppVersion))
            throw new ArgumentException("Version string cannot be null or empty.", nameof(AppVersion));

        string input = AppVersion.Trim();
        if (input.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            input = input.Substring(1);

        var parts = input.Split('.', StringSplitOptions.RemoveEmptyEntries);

        int major = parts.Length > 0 && int.TryParse(parts[0], out var m) ? m : 0;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
        int build = parts.Length > 2 && int.TryParse(parts[2], out var b) ? b : 0;
        int revision = parts.Length > 3 && int.TryParse(parts[3], out var r) ? r : 0;

        return new Version(major, minor, build, revision);
    }

    public static async Task<(bool, string?, string?)> CheckForUpdate(ILogger? logger)
    {
        
        try
        {
            bool updateFound = await AppUpdater.CheckForUpdatesAsync();
            if (updateFound)
                logger?.LogInformation($"SubathonManager found an update ({AppUpdater.LatestReleaseTagVersionStr})." +
                                       $" Changelog: {AppUpdater.GetChangelog()}");
            return (updateFound, AppUpdater.LatestReleaseTagVersionStr, $"{AppUpdater.RepositoryUrl}/releases/{AppUpdater.LatestReleaseTagVersionStr}");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to check for updates");
        }
        return (false, null, null);
    }

    public static async Task<UpdatumDownloadedAsset?> DownloadUpdate(ILogger? logger)
    {
        try
        {
            var downloads = await AppUpdater.DownloadUpdateAsync();
            if (downloads == null)
            {
                logger?.LogError($"SubathonManager found an update but failed to download.");
                return null;
            }

            return downloads;

        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to download update");
        }
        return null;
    }
    
    public static async Task<bool> InstallUpdate(UpdatumDownloadedAsset? asset, ILogger? logger)
    {
        if (asset == null)
            return false;
        
        try
        {
            await AppUpdater.InstallUpdateAsync(asset);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to install update");
        }
        return false;
    }

    public static async Task<bool> DownloadAndInstall(ILogger? logger)
    {
        var asset = await DownloadUpdate(logger);
        return await InstallUpdate(asset, logger);
    }
    
}