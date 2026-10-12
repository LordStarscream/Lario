using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Lario.Infrastructure;

/// <summary>
/// Design-time-Factory für die EF-Migrationsbefehle (dotnet ef migrations
/// add ...). Nutzt eine feste, lokale Connection String, die keine echte
/// Datenquelle anspricht und keine Konfiguration braucht.
/// </summary>
public sealed class LarioDbContextFactory : IDesignTimeDbContextFactory<LarioDbContext>
{
    public LarioDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LarioDbContext>()
            .UseSqlite("Data Source=lario-design.sqlite")
            .Options;

        return new LarioDbContext(options);
    }
}
