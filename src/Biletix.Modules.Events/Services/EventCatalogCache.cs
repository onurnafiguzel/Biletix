using System.Text.Json;
using Biletix.Modules.Events.Domain;
using Biletix.Shared.Contracts;
using Biletix.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Biletix.Modules.Events.Services;

internal sealed class EventCatalogCache(AppDbContext db, IConnectionMultiplexer redis, ILogger<EventCatalogCache> log)
{
    private static string KeyFor(Guid id) => $"event:{id}";

    /// <summary>Static catalog for one event — from Redis if present, else Postgres (then cached). Null if no such event.</summary>
    public async Task<EventDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var key = KeyFor(id);

        if (redis.IsConnected)
        {
            try
            {
                var cached = await redis.GetDatabase().StringGetAsync(key);
                if (cached.HasValue)
                    return JsonSerializer.Deserialize<EventDto>(cached!);
            }
            catch (Exception ex) { log.LogWarning(ex, "Event catalog cache read failed; falling back to DB"); }
        }

        var dto = await db.Set<Event>().AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new EventDto(e.Id, e.Title, e.StartsAt, e.TotalTickets,
                new VenueDto(e.Venue.Id, e.Venue.Name, e.Venue.City),
                new PerformerDto(e.Performer.Id, e.Performer.Name)))
            .FirstOrDefaultAsync(ct);
        if (dto is null) return null;

        if (redis.IsConnected)
        {
            try { await redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(dto)); }
            catch (Exception ex) { log.LogWarning(ex, "Event catalog cache write failed"); }
        }
        return dto;
    }

    /// <summary>Drop the cached catalog. Called on the (rare) event create/edit write path to stay fresh without a TTL.</summary>
    public async Task InvalidateAsync(Guid id)
    {
        if (!redis.IsConnected) return;
        try { await redis.GetDatabase().KeyDeleteAsync(KeyFor(id)); }
        catch (Exception ex) { log.LogWarning(ex, "Event catalog cache invalidate failed"); }
    }
}
