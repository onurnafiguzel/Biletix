using Biletix.Modules.Events.Domain;
using Biletix.Shared.Contracts;
using Biletix.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Biletix.Modules.Events;

internal class EventsModule(AppDbContext db) : IEventsModule
{
    public Task<Event?> GetWithTicketsAsync(Guid eventId, CancellationToken ct = default) =>
        db.Set<Event>()
            .Include(e => e.Venue)
            .Include(e => e.Performer)
            .Include(e => e.Tickets)
            .FirstOrDefaultAsync(e => e.Id == eventId, ct);

    public async Task<IReadOnlyList<Ticket>> GetTicketsAsync(IEnumerable<Guid> ticketIds, CancellationToken ct = default)
    {
        var ids = ticketIds.ToHashSet();
        // Read-only validation/pricing — the authoritative state change is the CAS below,
        // so these rows are never saved through the change tracker.
        return await db.Set<Ticket>().AsNoTracking().Where(t => ids.Contains(t.Id)).ToListAsync(ct);
    }

    public Task<int> TryReserveTicketsAsync(IReadOnlyCollection<Guid> ticketIds, Guid bookingId, DateTime reservedUntil, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        // CAS: a ticket is reservable if it is Available, or a Reserved hold that has expired
        // (self-healing — an abandoned hold is reclaimed without a background job). Postgres row
        // locks serialize concurrent reservers, so at most one wins each ticket → no oversell.
        return db.Set<Ticket>()
            .Where(t => ticketIds.Contains(t.Id) &&
                (t.Status == TicketStatus.Available ||
                 (t.Status == TicketStatus.Reserved && t.ReservedUntil < now)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, TicketStatus.Reserved)
                .SetProperty(t => t.ReservedBy, bookingId)
                .SetProperty(t => t.ReservedUntil, reservedUntil)
                .SetProperty(t => t.UpdatedAt, now), ct);
    }

    public Task<int> TryConfirmTicketsAsync(IReadOnlyCollection<Guid> ticketIds, Guid bookingId, CancellationToken ct = default) =>
        db.Set<Ticket>()
            .Where(t => ticketIds.Contains(t.Id) &&
                t.Status == TicketStatus.Reserved &&
                t.ReservedBy == bookingId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, TicketStatus.Booked)
                .SetProperty(t => t.ReservedBy, (Guid?)null)
                .SetProperty(t => t.ReservedUntil, (DateTime?)null)
                .SetProperty(t => t.UpdatedAt, DateTime.UtcNow), ct);

    public Task<int> ReleaseReservationAsync(IReadOnlyCollection<Guid> ticketIds, Guid bookingId, CancellationToken ct = default) =>
        db.Set<Ticket>()
            .Where(t => ticketIds.Contains(t.Id) &&
                t.Status == TicketStatus.Reserved &&
                t.ReservedBy == bookingId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, TicketStatus.Available)
                .SetProperty(t => t.ReservedBy, (Guid?)null)
                .SetProperty(t => t.ReservedUntil, (DateTime?)null)
                .SetProperty(t => t.UpdatedAt, DateTime.UtcNow), ct);
}
