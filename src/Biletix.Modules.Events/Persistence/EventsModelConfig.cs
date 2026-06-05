using Biletix.Modules.Events.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Biletix.Modules.Events.Persistence;

internal class VenueConfig : IEntityTypeConfiguration<Venue>
{
    public void Configure(EntityTypeBuilder<Venue> b) => b.ToTable("venues");
}

internal class PerformerConfig : IEntityTypeConfiguration<Performer>
{
    public void Configure(EntityTypeBuilder<Performer> b) => b.ToTable("performers");
}

internal class EventConfig : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> b)
    {
        b.ToTable("events");
        b.HasOne(x => x.Venue).WithMany().HasForeignKey(x => x.VenueId);
        b.HasOne(x => x.Performer).WithMany().HasForeignKey(x => x.PerformerId);
        b.HasMany(x => x.Tickets).WithOne().HasForeignKey(t => t.EventId);
        b.HasIndex(x => x.StartsAt);
    }
}

internal class TicketConfig : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> b)
    {
        b.ToTable("tickets");
        b.HasIndex(x => new { x.EventId, x.Status });
        b.HasIndex(x => new { x.Status, x.ReservedUntil });
        b.Property(x => x.Status).HasConversion<string>();
    }
}
