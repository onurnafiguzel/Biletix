namespace Biletix.Modules.Bookings.Domain;

public enum BookingStatus { Pending, Confirmed, Failed }

public class Booking
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Guid UserId { get; set; }
    public List<Guid> TicketIds { get; set; } = new();
    public decimal TotalPrice { get; set; }
    public BookingStatus Status { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
