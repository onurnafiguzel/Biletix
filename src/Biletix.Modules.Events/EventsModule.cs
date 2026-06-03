using Biletix.Modules.Events.Domain;
using Biletix.Shared.Contracts;
using Biletix.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Biletix.Modules.Events;

internal class EventsModule : IEventsModule
{
    private readonly AppDbContext _db;
    public EventsModule(AppDbContext db) => _db = db;

    public Task<Event?> GetWithTicketsAsync(Guid eventId, CancellationToken ct = default) =>
        _db.Set<Event>()
            .Include(e => e.Venue)
            .Include(e => e.Performer)
            .Include(e => e.Tickets)
            .FirstOrDefaultAsync(e => e.Id == eventId, ct);

    public async Task<IReadOnlyList<Ticket>> GetTicketsAsync(IEnumerable<Guid> ticketIds, CancellationToken ct = default)
    {
        var ids = ticketIds.ToHashSet();
        return await _db.Set<Ticket>().Where(t => ids.Contains(t.Id)).ToListAsync(ct);
    }

    public async Task MarkTicketsBookedAsync(IEnumerable<Guid> ticketIds, CancellationToken ct = default)
    {
        var ids = ticketIds.ToHashSet();
        var tickets = await _db.Set<Ticket>().Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        foreach (var t in tickets)
        {
            t.Status = TicketStatus.Booked;
            t.UpdatedAt = DateTime.UtcNow;
        }
    }
}
