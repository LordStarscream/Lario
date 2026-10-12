using Lario.Domain.Journal;
using Lario.Application;
using Microsoft.EntityFrameworkCore;

namespace Lario.Infrastructure;

/// <summary>
/// EF-Core/SQLite-Implementierung der internen Ausgleichspositionen
/// (Rechenvertrag §3.5). Nur Lesen: Schreibzugriffe laufen über
/// <see cref="BookingRepository.AddAsync(Lario.Domain.Journal.Booking,
/// System.Collections.Generic.IReadOnlyCollection{Lario.Domain.Journal.SettlementPosition},
/// System.Threading.CancellationToken)"/>, damit Buchung und
/// Positionseffekte in einer Transaktion committet werden.
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
}
