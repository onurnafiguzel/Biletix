using System.Reflection;
using Biletix.Modules.Bookings;
using Biletix.Modules.Events;
using Biletix.Modules.Search;
using Biletix.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var moduleAssemblies = new[]
{
    typeof(Biletix.Modules.Events.EventsEndpoints).Assembly,
    typeof(Biletix.Modules.Bookings.BookingsEndpoints).Assembly,
};

builder.Services.AddSingleton<IEnumerable<Assembly>>(moduleAssemblies);
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("AppDb"),
        npg => npg.MigrationsAssembly("Biletix.Api")));

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    // Single node today ("redis:6379"); for a Redis Cluster set Redis:ConnectionString to a comma-
    // separated seed list ("redis-1:6379,redis-2:6379,redis-3:6379"). ConfigurationOptions.Parse adds
    // every endpoint and StackExchange.Redis auto-discovers the cluster topology (slot routing + MOVED/
    // ASK). AbortOnConnectFail=false keeps the lock advisory — a Redis outage must not fail DI/startup.
    var options = ConfigurationOptions.Parse(builder.Configuration["Redis:ConnectionString"] ?? "redis:6379");
    options.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(options);
});

builder.Services
    .AddEventsModule()
    .AddBookingsModule()
    .AddSearchModule(builder.Configuration);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

app.MapEventsEndpoints();
app.MapBookingsEndpoints();
app.MapSearchEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
