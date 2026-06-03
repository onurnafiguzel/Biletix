using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Biletix.Shared.Persistence;

/// <summary>
/// Single DbContext for the entire modular monolith. Each module ships its
/// entity types and IEntityTypeConfiguration<T> implementations; they are
/// discovered at startup via ApplyConfigurationsFromAssembly.
/// </summary>
public class AppDbContext : DbContext
{
    private readonly IEnumerable<Assembly> _moduleAssemblies;

    public AppDbContext(DbContextOptions<AppDbContext> options, IEnumerable<Assembly> moduleAssemblies)
        : base(options)
    {
        _moduleAssemblies = moduleAssemblies;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        foreach (var asm in _moduleAssemblies)
            modelBuilder.ApplyConfigurationsFromAssembly(asm);
    }
}
