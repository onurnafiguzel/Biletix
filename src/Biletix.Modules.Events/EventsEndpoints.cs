using Biletix.Modules.Events.Domain;
using Biletix.Modules.Events.Services;
using Biletix.Shared.Contracts;
using Biletix.Shared.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Biletix.Modules.Events;

public static class EventsEndpoints
{
    public static IServiceCollection AddEventsModule(this IServiceCollection services)
    {
        services.AddScoped<IEventsModule, EventsModule>();
        services.AddHostedService<ReservationSweeper>();
        return services;
    }

    public static IEndpointRouteBuilder MapEventsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/events");

        g.MapGet("/{id:guid}", async (Guid id, IEventsModule mod) =>
        {
            var ev = await mod.GetWithTicketsAsync(id);
            if (ev is null) return Results.NotFound();
            var dto = new EventDetailsDto(
                ev.Id, ev.Title, ev.StartsAt, ev.TotalTickets,
                new VenueDto(ev.Venue.Id, ev.Venue.Name, ev.Venue.City),
                new PerformerDto(ev.Performer.Id, ev.Performer.Name),
                ev.Tickets.Select(t => new TicketDto(t.Id, t.SeatLabel, t.Price, t.Status)).ToList());
            return Results.Ok(dto);
        });

        g.MapGet("/", async (AppDbContext db, int pageNumber = 1, int pageSize = 20, bool upcoming = true) =>
        {
            var q = db.Set<Event>().AsNoTracking().Include(e => e.Venue).Include(e => e.Performer).AsQueryable();
            if (upcoming) q = q.Where(e => e.StartsAt >= DateTime.UtcNow);
            var items = await q
                .OrderBy(e => e.StartsAt)
                .Skip((pageNumber - 1) * pageSize).Take(pageSize)
                .Select(e => new EventDto(e.Id, e.Title, e.StartsAt, e.TotalTickets,
                    new VenueDto(e.Venue.Id, e.Venue.Name, e.Venue.City),
                    new PerformerDto(e.Performer.Id, e.Performer.Name)))
                .ToListAsync();
            return Results.Ok(items);
        });

        g.MapPost("/seed", async (SeedRequest req, AppDbContext db) =>
        {
            var venue = await db.Set<Venue>().FirstOrDefaultAsync(v => v.Name == req.VenueName)
                        ?? new Venue { Id = Guid.NewGuid(), Name = req.VenueName, City = req.City };
            if (db.Entry(venue).State == EntityState.Detached) db.Add(venue);
            var perf = await db.Set<Performer>().FirstOrDefaultAsync(p => p.Name == req.PerformerName)
                       ?? new Performer { Id = Guid.NewGuid(), Name = req.PerformerName };
            if (db.Entry(perf).State == EntityState.Detached) db.Add(perf);

            var ev = new Event
            {
                Id = Guid.NewGuid(),
                Title = req.Title,
                StartsAt = req.StartsAt,
                TotalTickets = req.TicketCount,
                Venue = venue,
                Performer = perf,
            };
            for (int i = 1; i <= req.TicketCount; i++)
            {
                ev.Tickets.Add(new Ticket
                {
                    Id = Guid.NewGuid(),
                    EventId = ev.Id,
                    SeatLabel = $"A{i:D3}",
                    Price = req.Price,
                    Status = TicketStatus.Available,
                });
            }
            db.Add(ev);
            await db.SaveChangesAsync();
            return Results.Created($"/events/{ev.Id}", new { ev.Id, TicketCount = ev.Tickets });
        });

        return app;
    }
}

public record SeedRequest(string Title, DateTime StartsAt, string VenueName, string City, string PerformerName, int TicketCount, decimal Price);
