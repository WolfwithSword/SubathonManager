using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;

namespace SubathonManager.Core;

[ExcludeFromCodeCoverage]
public static class GoAffProConfigMigration
{
    public static bool Run(IConfig config)
    {
        bool migrated = false;
        var discord = config.GetSection("Discord");
        if (discord == null) return false;

        foreach (var legacyType in Enum.GetValues<SubathonEventType>()
                     .Where(t => t.GetSource() == SubathonEventSource.GoAffPro
                                 && t.GetLegacyGoAffProSiteId() > 0))
        {
            var oldKey = $"Events.Log.{legacyType}";
            if (!discord.ContainsKey(oldKey)) continue;

            var newKey = $"Events.Log.GoAffProOrder.{legacyType.GetLegacyGoAffProSiteId()}";
            if (string.IsNullOrWhiteSpace(config.Get("Discord", newKey, string.Empty)))
                config.Set("Discord", newKey, discord[oldKey]);
            discord.RemoveKey(oldKey);
            migrated = true;
        }

        if (migrated) config.Save();
        return migrated;
    }
}
