using SubathonManager.Core.Models;
using SubathonManager.Core.Enums;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
namespace SubathonManager.Data.Extensions;

[ExcludeFromCodeCoverage]
public static class SubathonQueryExtensions
{
    // unused but may be useful in future. Also sets up possible future extensions
    
    public static async Task<long> TwitchCheerTotalAsync(
        this SubathonData subathon,
        AppDbContext db)
    {
        var events = await db.SubathonEvents.AsNoTracking()
            .Where(e =>
                e.SubathonId == subathon.Id &&
                e.EventType == SubathonEventType.TwitchCheer)
            .ToListAsync();
        return events.Sum(e =>
            long.TryParse(e.Value, out var v) ? v : 0);
    }
    
    public static long TwitchCheerTotal(
        this SubathonData subathon,
        AppDbContext db)
    {
        return db.Set<SubathonEvent>()
            .Where(e =>
                e.SubathonId == subathon.Id &&
                e.EventType == SubathonEventType.TwitchCheer).AsNoTracking().AsEnumerable()
            .Sum(e => (long?)Convert.ToInt64(e.Value))?? 0;
    }
}