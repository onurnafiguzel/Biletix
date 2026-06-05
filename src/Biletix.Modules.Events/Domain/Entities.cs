using Biletix.Shared.Contracts;

namespace Biletix.Modules.Events.Domain;

public class Venue
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public string City { get; set; } = default!;
}

public class Performer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
}

public class Event
{
    public Guid Id { get; set; }
    public string Title { get; set; } = default!;
    public DateTime StartsAt { get; set; }
    public int TotalTickets { get; set; }
    public Guid VenueId { get; set; }
    public Venue Venue { get; set; } = default!;
    public Guid PerformerId { get; set; }
    public Performer Performer { get; set; } = default!;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<Ticket> Tickets { get; set; } = new();
}

public class Ticket
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public string SeatLabel { get; set; } = default!;
    public decimal Price { get; set; }
    public TicketStatus Status { get; set; } = TicketStatus.Available;
    public Guid? ReservedBy { get; set; }
    public DateTime? ReservedUntil { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
