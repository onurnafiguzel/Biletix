using Biletix.Modules.Bookings.Domain;
using Biletix.Modules.Bookings.Services;
using Biletix.Modules.Events;
using Biletix.Shared.Contracts;
using Biletix.Shared.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Biletix.Modules.Bookings;

public static class BookingsEndpoints
{
    public static IServiceCollection AddBookingsModule(this IServiceCollection services)
    {
        services.AddScoped<TicketLockService>();
        services.AddScoped<IPaymentGateway, StubPaymentGateway>();
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
            IPaymentGateway payments,
            IConfiguration cfg,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("Biletix.Bookings");

            if (req.TicketIds is null || req.TicketIds.Count == 0)
                return Results.BadRequest(new { error = "ticketIds required" });

            var ticketIds = req.TicketIds.Distinct().ToList();

            // 1) Cheap pre-filter (NOT authoritative): tickets exist and belong to the event; price.
            var tickets = await events.GetTicketsAsync(ticketIds, ct);
            if (tickets.Count != ticketIds.Count)
                return Results.BadRequest(new { error = "some tickets do not belong to system" });
            if (tickets.Any(t => t.EventId != eventId))
                return Results.BadRequest(new { error = "ticket(s) do not belong to event" });

            var bookingId = Guid.NewGuid();
            var total = tickets.Sum(t => t.Price);
            var reservedUntil = DateTime.UtcNow.AddSeconds(cfg.GetValue("Bookings:HoldSeconds", 120));

            // 2) Redis fast-path (advisory). A Redis outage must NOT stop bookings — correctness
            //    lives in the DB — so any Redis error is swallowed and we fall through to the DB gate.
            var lockedInRedis = false;
            try
            {
                lockedInRedis = await locks.TryAcquireAllAsync(eventId, ticketIds, bookingId);
                if (!lockedInRedis)
                    return Results.Conflict(new { error = "ticket(s) locked by another buyer" });
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Redis lock unavailable; proceeding to DB reservation (advisory only)");
            }

            // 3) RESERVE (tx1) — authoritative, all-or-nothing. No charge yet.
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                var reserved = await events.TryReserveTicketsAsync(ticketIds, bookingId, reservedUntil, ct);
                if (reserved != ticketIds.Count)
                {
                    await tx.RollbackAsync(ct);                       // undo any partial reservation
                    await ReleaseRedisAsync(locks, eventId, ticketIds, bookingId, lockedInRedis);
                    return Results.Conflict(new { error = "ticket(s) not available" });
                }
                await tx.CommitAsync(ct);                             // durable hold; no DB locks held during payment
            }

            // 4) PAY. On failure, release the hold (and the advisory Redis lock).
            var paid = await payments.ChargeAsync(req.PaymentDetails, total, ct);
            if (!paid)
            {
                await events.ReleaseReservationAsync(ticketIds, bookingId, ct);
                await ReleaseRedisAsync(locks, eventId, ticketIds, bookingId, lockedInRedis);
                return Results.Problem("payment failed", statusCode: 402);
            }

            // 5) CONFIRM (tx2) — Reserved→Booked + booking row, atomically.
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                var confirmed = await events.TryConfirmTicketsAsync(ticketIds, bookingId, ct);
                if (confirmed != ticketIds.Count)
                {
                    await tx.RollbackAsync(ct);
                    // The hold expired / was reclaimed during payment → compensate (refund + release).
                    await payments.RefundAsync(req.PaymentDetails, total, ct);
                    await events.ReleaseReservationAsync(ticketIds, bookingId, ct);
                    await ReleaseRedisAsync(locks, eventId, ticketIds, bookingId, lockedInRedis);
                    return Results.Conflict(new { error = "reservation expired during payment; refunded" });
                }

                db.Add(new Booking
                {
                    Id = bookingId,
                    EventId = eventId,
                    UserId = req.UserId,
                    TicketIds = ticketIds,
                    TotalPrice = total,
                    Status = BookingStatus.Confirmed,
                });
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }

            await ReleaseRedisAsync(locks, eventId, ticketIds, bookingId, lockedInRedis);
            return Results.Ok(new BookingResponse(bookingId, "Confirmed", total));
        });

        return app;
    }

    // Best-effort release of the advisory Redis lock; never throws into the request path.
    private static async Task ReleaseRedisAsync(TicketLockService locks, Guid eventId, IReadOnlyCollection<Guid> ticketIds, Guid bookingId, bool acquired)
    {
        if (!acquired) return;
        try { await locks.ReleaseAsync(eventId, ticketIds, bookingId); }
        catch { /* advisory lock — ignore release failures */ }
    }
}
