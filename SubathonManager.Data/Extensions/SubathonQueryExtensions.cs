using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;

namespace SubathonManager.Data.Extensions;

[ExcludeFromCodeCoverage]
public static class SubathonQueryExtensions {
    // unused but may be useful in future. Also sets up possible future extensions

    public static async Task<long> TwitchCheerTotalAsync(
        this SubathonData subathon,
        AppDbContext db) {
        List<SubathonEvent> events = await db.SubathonEvents.AsNoTracking()
            .Where(e =>
                e.SubathonId == subathon.Id &&
                e.EventType == SubathonEventType.TwitchCheer)
            .ToListAsync();
        return events.Sum(e =>
            long.TryParse(e.Value, out long v) ? v : 0);
    }

    public static long TwitchCheerTotal(
        this SubathonData subathon,
        AppDbContext db) {
        return db.Set<SubathonEvent>()
            .Where(e =>
                e.SubathonId == subathon.Id &&
                e.EventType == SubathonEventType.TwitchCheer).AsNoTracking().AsEnumerable()
            .Sum(e => (long?)Convert.ToInt64(e.Value)) ?? 0;
    }
}