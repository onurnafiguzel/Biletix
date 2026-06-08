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
        services.AddScoped<EventCatalogCache>();   // BILETIX-3: cache-aside read-model for event detail
        services.AddHostedService<ReservationSweeper>();
        return services;
    }

    public static IEndpointRouteBuilder MapEventsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/events");

        // Detail: static catalog from the Redis cache-aside read-model; the volatile ticket list
        // still from the write-model (BILETIX-4 moves live availability to its own read-model).
        g.MapGet("/{id:guid}", async (Guid id, EventCatalogCache catalog, IEventsModule mod, CancellationToken ct) =>
        {
            var ev = await catalog.GetAsync(id, ct);
            if (ev is null) return Results.NotFound();
            var tickets = await mod.GetEventTicketsAsync(id, ct);
            var dto = new EventDetailsDto(
                ev.Id, ev.Title, ev.StartsAt, ev.TotalTickets, ev.Venue, ev.Performer,
                tickets.Select(t => new TicketDto(t.Id, t.SeatLabel, t.Price, t.Status)).ToList());
            return Results.Ok(dto);
        });

        // List: served from the CDC-fed ES read-model — sort/filter/paginate is a query-engine job,
        // so the OLTP write-model no longer carries browse load (BILETIX-3).
        g.MapGet("/", async (IEventCatalogReadModel catalog, CancellationToken ct, int pageNumber = 1, int pageSize = 20, bool upcoming = true) =>
        {
            var items = await catalog.ListAsync(pageNumber, pageSize, upcoming, ct);
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
