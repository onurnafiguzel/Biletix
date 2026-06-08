using Biletix.Modules.Events.Domain;

namespace Biletix.Modules.Events;

public interface IEventsModule
{
    Task<IReadOnlyList<Ticket>> GetEventTicketsAsync(Guid eventId, CancellationToken ct = default);
    Task<IReadOnlyList<Ticket>> GetTicketsAsync(IEnumerable<Guid> ticketIds, CancellationToken ct = default);

    /// <summary>Available (or expired Reserved) → Reserved, held by <paramref name="bookingId"/> until <paramref name="reservedUntil"/>.</summary>
    Task<int> TryReserveTicketsAsync(IReadOnlyCollection<Guid> ticketIds, Guid bookingId, DateTime reservedUntil, CancellationToken ct = default);

    /// <summary>Reserved-by-this-booking → Booked (clears the hold). The authoritative oversell gate.</summary>
    Task<int> TryConfirmTicketsAsync(IReadOnlyCollection<Guid> ticketIds, Guid bookingId, CancellationToken ct = default);

    /// <summary>Reserved-by-this-booking → Available (compensation on payment/confirm failure).</summary>
    Task<int> ReleaseReservationAsync(IReadOnlyCollection<Guid> ticketIds, Guid bookingId, CancellationToken ct = default);
}
