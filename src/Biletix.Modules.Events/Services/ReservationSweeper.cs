using Biletix.Modules.Events.Domain;
using Biletix.Shared.Contracts;
using Biletix.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Biletix.Modules.Events.Services;


internal sealed class ReservationSweeper(IServiceScopeFactory scopeFactory, ILogger<ReservationSweeper> log)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var released = await db.Set<Ticket>()
                    .Where(t => t.Status == TicketStatus.Reserved && t.ReservedUntil < now)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.Status, TicketStatus.Available)
                        .SetProperty(t => t.ReservedBy, (Guid?)null)
                        .SetProperty(t => t.ReservedUntil, (DateTime?)null)
                        .SetProperty(t => t.UpdatedAt, now), stoppingToken);
                if (released > 0)
                    log.LogInformation("ReservationSweeper released {Count} expired hold(s)", released);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // shutting down
            }
            catch (Exception ex)
            {
                log.LogError(ex, "ReservationSweeper sweep failed; will retry next tick");
            }
        }
    }
}
