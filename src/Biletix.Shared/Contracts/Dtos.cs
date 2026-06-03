namespace Biletix.Shared.Contracts;

public record VenueDto(Guid Id, string Name, string City);
public record PerformerDto(Guid Id, string Name);

public enum TicketStatus { Available, Reserved, Booked }

public record TicketDto(Guid Id, string SeatLabel, decimal Price, TicketStatus Status);

public record EventDto(
    Guid Id,
    string Title,
    DateTime StartsAt,
    int TotalTickets,
    VenueDto Venue,
    PerformerDto Performer);

public record EventDetailsDto(
    Guid Id,
    string Title,
    DateTime StartsAt,
    int TotalTickets,
    VenueDto Venue,
    PerformerDto Performer,
    IReadOnlyList<TicketDto> Tickets);

public record CreateBookingRequest(
    IReadOnlyList<Guid> TicketIds,
    Guid UserId,
    PaymentDetails PaymentDetails);

public record PaymentDetails(string CardNumberMasked, string Holder);

public record BookingResponse(Guid BookingId, string Status, decimal TotalPrice);

public record SearchHit(
    Guid Id,
    string Title,
    string PerformerName,
    string VenueName,
    string City,
    DateTime StartsAt,
    int TotalTickets);
