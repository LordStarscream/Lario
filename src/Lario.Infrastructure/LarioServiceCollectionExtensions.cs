using Lario.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lario.Infrastructure;

/// <summary>
/// Verdrahtung der Datenschicht (Schritt 3): DbContext auf SQLite plus
/// die gezielten Repository- und Anwendungsfall-Implementierungen.
/// </summary>
public static class LarioServiceCollectionExtensions
{
    /// <summary>
    /// Registriert DbContext, Repositories und Anwendungsfälle.
    /// <paramref name="connectionString"/> zeigt auf die lokale
    /// SQLite-Datei (z. B. über <see cref="LarioDataPaths"/>).
    /// </summary>
    public static IServiceCollection AddLarioData(
        this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<LarioDbContext>(options =>
            options.UseSqlite(connectionString));

        services.AddScoped<IMasterDataRepository, MasterDataRepository>();
        services.AddScoped<IBookingRepository, BookingRepository>();

        services.AddScoped<MasterDataAdmin>();
        services.AddScoped<OpeningBalanceService>();
        services.AddScoped<BalanceQueryService>();

        return services;
    }
}
