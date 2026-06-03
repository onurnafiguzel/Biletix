using StackExchange.Redis;

namespace Biletix.Modules.Bookings.Services;

public class TicketLockService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(120);

    // KEYS = ticket keys to lock
    // ARGV[1] = owner (bookingId)
    // ARGV[2] = ttl in milliseconds
    // Returns 1 if ALL keys were claimed atomically, 0 otherwise (no key mutated).
    private const string AcquireScript = @"
for i=1,#KEYS do
  if redis.call('EXISTS', KEYS[i]) == 1 then
    return 0
  end
end
for i=1,#KEYS do
  redis.call('SET', KEYS[i], ARGV[1], 'PX', ARGV[2])
end
return 1
";

    // KEYS = ticket keys to release
    // ARGV[1] = expected owner
    // Only deletes keys whose value matches the expected owner (idempotent, safe vs TTL expiry).
    private const string ReleaseScript = @"
local n = 0
for i=1,#KEYS do
  if redis.call('GET', KEYS[i]) == ARGV[1] then
    redis.call('DEL', KEYS[i])
    n = n + 1
  end
end
return n
";

    public TicketLockService(IConnectionMultiplexer redis) => _redis = redis;

    /// <summary>
    /// Atomically tries to acquire per-ticket locks via a Redis Lua script.
    /// The check-then-set is executed inside Redis as a single atomic operation,
    /// so no partial state is observable and no rollback path is needed.
    /// This is the overselling boundary — DB-level locks are not used.
    /// </summary>
    public async Task<bool> TryAcquireAllAsync(IEnumerable<Guid> ticketIds, Guid bookingId)
    {
        var db = _redis.GetDatabase();
        var keys = ticketIds.Select(id => (RedisKey)$"ticket:{id}").ToArray();
        if (keys.Length == 0) return true;

        var result = (int)await db.ScriptEvaluateAsync(
            AcquireScript,
            keys,
            new RedisValue[] { bookingId.ToString(), (long)_ttl.TotalMilliseconds });

        return result == 1;
    }

    public async Task ReleaseAsync(IEnumerable<Guid> ticketIds, Guid bookingId)
    {
        var db = _redis.GetDatabase();
        var keys = ticketIds.Select(id => (RedisKey)$"ticket:{id}").ToArray();
        if (keys.Length == 0) return;

        await db.ScriptEvaluateAsync(
            ReleaseScript,
            keys,
            new RedisValue[] { bookingId.ToString() });
    }
}
