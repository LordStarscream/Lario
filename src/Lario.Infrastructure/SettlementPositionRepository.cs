using Lario.Domain.Journal;
using Lario.Application;
using Microsoft.EntityFrameworkCore;

namespace Lario.Infrastructure;

/// <summary>
/// EF-Core/SQLite-Implementierung der internen Ausgleichspositionen
/// (Rechenvertrag §3.5). Der reguläre Schreibweg (Buchung zusammen mit
/// ihren Positionseffekten) läuft über
/// <see cref="BookingRepository.AddAsync(Lario.Domain.Journal.Booking,
/// System.Collections.Generic.IReadOnlyCollection{Lario.Domain.Journal.SettlementPosition},
/// System.Threading.CancellationToken)"/>, damit Buchung und
/// Positionseffekte in einer Transaktion committet werden; die einzige
/// Ausnahme ist der Upgrade-Backfill (<see cref="AddAsync"/>), der
/// fehlende Zeilen ohne neue Buchung nachführt.
/// </summary>
public sealed class SettlementPositionRepository(LarioDbContext db) : ISettlementPositionRepository
{
    public Task<SettlementPosition?> FindByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return db.SettlementPositions
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<SettlementPosition>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await db.SettlementPositions
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Upgrade-Backfill (Schritt-5-Review P2d): neue Zeilen in einer
    /// einzigen Transaktion — ein Teilkommitt ist ausgeschlossen.
    /// </summary>
    public async Task AddAsync(
        IReadOnlyCollection<SettlementPosition> positions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(positions);

        foreach (var position in positions)
        {
            db.SettlementPositions.Add(position);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
