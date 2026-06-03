using Biletix.Modules.Bookings.Domain;
using Biletix.Modules.Bookings.Services;
using Biletix.Modules.Events;
using Biletix.Shared.Contracts;
using Biletix.Shared.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Biletix.Modules.Bookings;

public static class BookingsEndpoints
{
    public static IServiceCollection AddBookingsModule(this IServiceCollection services)
    {
        services.AddScoped<TicketLockService>();
        services.AddScoped<PaymentService>();
        return services;
    }

    public static IEndpointRouteBuilder MapBookingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/bookings/{eventId:guid}", async (
            Guid eventId,
            CreateBookingRequest req,
            AppDbContext db,
            IEventsModule events,
            TicketLockService locks,
            PaymentService payments) =>
        {
            if (req.TicketIds is null || req.TicketIds.Count == 0)
                return Results.BadRequest(new { error = "ticketIds required" });

            var tickets = await events.GetTicketsAsync(req.TicketIds);
            if (tickets.Count != req.TicketIds.Count)
                return Results.BadRequest(new { error = "some tickets do not belong to system" });
            if (tickets.Any(t => t.EventId != eventId))
                return Results.BadRequest(new { error = "ticket(s) do not belong to event" });
            if (tickets.Any(t => t.Status != TicketStatus.Available))
                return Results.Conflict(new { error = "ticket(s) not available" });

            var bookingId = Guid.NewGuid();
            var got = await locks.TryAcquireAllAsync(req.TicketIds, bookingId);
            if (!got) return Results.Conflict(new { error = "ticket(s) locked by another buyer" });

            var total = tickets.Sum(t => t.Price);
            var paid = await payments.ChargeAsync(req.PaymentDetails, total);
            if (!paid)
            {
                await locks.ReleaseAsync(req.TicketIds, bookingId);
                return Results.Problem("payment failed", statusCode: 402);
            }

            await using var tx = await db.Database.BeginTransactionAsync();

            db.Add(new Booking
            {
                Id = bookingId,
                EventId = eventId,
                UserId = req.UserId,
                TicketIds = req.TicketIds.ToList(),
                TotalPrice = total,
                Status = BookingStatus.Confirmed,
            });

            // Cross-module call — runs against the same AppDbContext, same transaction.
            // Both bookings INSERT and tickets UPDATE land atomically; Postgres WAL
            // emits a single logical change set that Debezium streams to Elasticsearch.
            await events.MarkTicketsBookedAsync(req.TicketIds);

            await db.SaveChangesAsync();
            await tx.CommitAsync();

            return Results.Ok(new BookingResponse(bookingId, "Confirmed", total));
        });

        return app;
    }
}
