using Biletix.Modules.Events.Domain;

namespace Biletix.Modules.Events;

/// <summary>
/// Public contract that other modules (Bookings) may call. Within a single
/// AppDbContext transaction; no integration events needed.
/// </summary>
public interface IEventsModule
{
    Task<Event?> GetWithTicketsAsync(Guid eventId, CancellationToken ct = default);
    Task<IReadOnlyList<Ticket>> GetTicketsAsync(IEnumerable<Guid> ticketIds, CancellationToken ct = default);
    Task MarkTicketsBookedAsync(IEnumerable<Guid> ticketIds, CancellationToken ct = default);
}
