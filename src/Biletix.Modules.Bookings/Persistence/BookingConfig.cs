using Biletix.Modules.Bookings.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Biletix.Modules.Bookings.Persistence;

internal class BookingConfig : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> b)
    {
        b.ToTable("bookings");
        b.Property(x => x.Status).HasConversion<string>();
        b.Property(x => x.TicketIds).HasColumnType("uuid[]");
        b.HasIndex(x => x.EventId);
        b.HasIndex(x => x.UserId);
    }
}
