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
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Booking>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await db.Bookings
            .Include(b => b.AccountPostings)
            .Include(b => b.PoolPostings)
            .Include(b => b.Splits)
            .ThenInclude(s => s.Tax)
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
}
