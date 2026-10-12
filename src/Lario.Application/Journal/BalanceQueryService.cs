using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;

namespace Lario.Application;

/// <summary>
/// Saldenabfrage über das Buchungsjournal (Schritt 3): alle Salden werden
/// aus den Postings abgeleitet — es gibt keine gesondert gepflegten
/// Saldo-Felder, und jede Antwort ist aus dem Journal rekonstruierbar
/// (Rechenvertrag §3.2).
/// </summary>
public sealed class BalanceQueryService(
    IMasterDataRepository masterDataRepository,
    IBookingRepository bookingRepository)
{
    /// <summary>Einzelner Kontostand inkl. Stammdaten.</summary>
    public sealed record AccountBalanceEntry(
        string AccountId, string Name, EntityStatus Status, Money Balance);

    /// <summary>Einzelner Poolbestand inkl. Stammdaten.</summary>
    public sealed record PoolBalanceEntry(
        string PoolId, string Name, EntityStatus Status, Money Balance);

    /// <summary>Nicht zugeordneter Rest eines Anfangsbestands.</summary>
    public sealed record UnallocatedOpeningBalanceEntry(
        string OpeningBalanceId, DateOnly Date, string AccountId, Money Unallocated);

    /// <summary>
    /// Gesamtbild: Kontostände, Poolbestände, Summen und die sichtbaren
    /// Systempositionen „noch nicht zugeordnet" der Anfangsbestände.
    /// </summary>
    public sealed record Balances(
        IReadOnlyList<AccountBalanceEntry> Accounts,
        IReadOnlyList<PoolBalanceEntry> Pools,
        Money TotalAccountBalance,
        Money TotalPoolBalance,
        Money TotalIncome,
        Money AccountPoolGap,
        IReadOnlyList<UnallocatedOpeningBalanceEntry> UnallocatedOpeningBalances);

    public async Task<Balances> GetBalancesAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await masterDataRepository.LoadCatalogAsync(cancellationToken);
        var bookings = await bookingRepository.GetAllAsync(cancellationToken);

        var journal = new BookingJournal(bookings);

        var accounts = catalog.Accounts
            .Select(a => new AccountBalanceEntry(
                a.Id, a.Name, a.Status, journal.AccountBalance(a.Id)))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

        var pools = catalog.Pools
            .Select(p => new PoolBalanceEntry(
                p.Id, p.Name, p.Status, journal.PoolBalance(p.Id)))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

        var unallocated = journal.OpeningBalances()
            .Select(b => new UnallocatedOpeningBalanceEntry(
                b.Id,
                b.Date,
                b.AccountPostings.Count == 1 ? b.AccountPostings[0].AccountId : string.Empty,
                journal.OpeningBalanceUnallocated(b.Id)))
            .Where(e => e.Unallocated != Money.Zero)
            .ToList();

        return new Balances(
            Accounts: accounts,
            Pools: pools,
            TotalAccountBalance: journal.TotalAccountBalance(),
            TotalPoolBalance: journal.TotalPoolBalance(),
            TotalIncome: journal.TotalIncome(),
            AccountPoolGap: journal.AccountPoolGap(),
            UnallocatedOpeningBalances: unallocated);
    }
}
