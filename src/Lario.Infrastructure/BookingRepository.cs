using Lario.Domain.Journal;
using Lario.Application;
using Microsoft.EntityFrameworkCore;

namespace Lario.Infrastructure;

/// <summary>
/// EF-Core/SQLite-Implementierung des Buchungsjournals (Schritt 3).
/// <see cref="AddAsync"/> speichert Buchung und alle zusammengehörigen
/// Postings in einer Transaktion — eine Teilübernahme ist unmöglich
/// (Rechenvertrag §3.2).
/// </summary>
public sealed class BookingRepository(LarioDbContext db) : IBookingRepository
{
    public async Task<Booking?> FindByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return await db.Bookings
            .Include(b => b.AccountPostings)
            .Include(b => b.PoolPostings)
            .Include(b => b.Splits)
            .ThenInclude(s => s.Tax)
            .Include(b => b.TransferPurposes)
            .Include(b => b.ReassignmentAccounts)
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Booking>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await db.Bookings
            .Include(b => b.AccountPostings)
            .Include(b => b.PoolPostings)
            .Include(b => b.Splits)
            .ThenInclude(s => s.Tax)
            .Include(b => b.TransferPurposes)
            .Include(b => b.ReassignmentAccounts)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Booking booking, CancellationToken cancellationToken = default)
    {
        var exists = await db.Bookings.AnyAsync(b => b.Id == booking.Id, cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException(
                $"Buchung mit der Id '{booking.Id}' existiert bereits. " +
                "Die Idempotenzprüfung obliegt dem Anwendungsfall (OpeningBalanceService).");
        }

        db.Bookings.Add(booking);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddAsync(
        Booking booking,
        IReadOnlyCollection<SettlementPosition> settlements,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settlements);

        var exists = await db.Bookings.AnyAsync(b => b.Id == booking.Id, cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException(
                $"Buchung mit der Id '{booking.Id}' existiert bereits. " +
                "Die Idempotenzprüfung obliegt dem Anwendungsfall.");
        }

        db.Bookings.Add(booking);

        foreach (var position in settlements)
        {
            var tracked = await db.SettlementPositions
                .FirstOrDefaultAsync(p => p.Id == position.Id, cancellationToken);

            if (tracked is null)
            {
                // Neue Position (Episode wurde durch diese Buchung begründet
                // oder eine vermisste Position wird nachgeführt).
                db.SettlementPositions.Add(position);
                continue;
            }

            // Bestehende Position: Restzustand, Status und
            // Begleichungsreferenz aktualisieren.
            tracked.Amount = position.Amount;
            tracked.RestAmount = position.RestAmount;
            tracked.Status = position.Status;
            tracked.SettledByBookingId = position.SettledByBookingId;
            tracked.SettledDate = position.SettledDate;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
