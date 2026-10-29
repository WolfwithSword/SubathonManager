using SubathonManager.Core.Models;

namespace SubathonManager.Core.Interfaces;

public interface IMissedEventSource {
    Task<List<SubathonEvent>> FetchMissedEventsAsync(DateTime from, DateTime to, CancellationToken ct = default);
}
