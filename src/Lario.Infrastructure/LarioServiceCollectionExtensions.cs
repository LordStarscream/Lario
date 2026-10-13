using Lario.Application;
using Lario.Application.Journal;
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
        services.AddScoped<ISettlementPositionRepository, SettlementPositionRepository>();

        services.AddScoped<MasterDataAdmin>();
        services.AddScoped<OpeningBalanceService>();
        services.AddScoped<IncomeService>();
        services.AddScoped<ExpenseService>();
        services.AddScoped<TransferService>();
        services.AddScoped<ReassignmentService>();
        services.AddScoped<TransferReassignmentService>();
        services.AddScoped<BalanceQueryService>();
        services.AddScoped<SettlementQueryService>();
        // Upgrade-Backfill (Schritt-5-Review P2d): führt beim Host-Start
        // fehlende Zeilen für offene Ausgleichspositionen älterer
        // Datenbanken nach — atomar, idempotent.
        services.AddScoped<SettlementPositionBackfillService>();

        return services;
    }
}
