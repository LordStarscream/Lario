using Lario.Application;
using Lario.Application.Journal;
using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;

namespace Lario.Host.Api.Journal;

/// <summary>
/// DTO-Mappings für die Journal-Endpunkte (Schritt 7, T3/T4): reine
/// Transport-Mappinge ohne Fachlogik (Domain → DTO). Von den
/// Lese- (T3) und Schreibeendpunkten (T4) gemeinsam verwendet — die
/// Schreibantwort enthält dasselbe Detail wie <c>GET /bookings/{id}</c>.
/// </summary>
public static class JournalDtoMapper
{
    public static BookingListItemDto ToListItem(
        Booking booking,
        MasterDataCatalog catalog,
        BookingJournal journal) => new(
        booking.Id,
        booking.Type.ToString(),
        booking.Date.ToString("yyyy-MM-dd"),
        booking.Note,
        ComputeTotal(booking, journal),
        PrimaryAccount(booking, catalog).Id,
        PrimaryAccount(booking, catalog).Name,
        booking.Splits
            .Select(s => new BookingSplitSummaryDto(
                s.Amount.Cents,
                catalog.FindPool(s.PoolId)?.Name,
                catalog.FindCategory(s.CategoryId)?.Name,
                s.Tax?.Relevant,
                s.Tax?.TaxCategoryId is { } taxId
                    ? catalog.FindTaxCategory(taxId)?.Name
                    : null))
            .ToList(),
        booking.PoolPostings
            .Select(p => catalog.FindPool(p.PoolId)?.Name)
            .Where(n => n is not null)
            .Cast<string>()
            .ToList(),
        booking.BaseRevision,
        journal.IsEffectivelyReversed(booking.Id));

    public static BookingDetailDto ToDetail(
        Booking booking,
        MasterDataCatalog catalog,
        BookingJournal journal) => new(
        booking.Id,
        booking.Type.ToString(),
        booking.Date.ToString("yyyy-MM-dd"),
        booking.Note,
        booking.BaseRevision,
        journal.IsEffectivelyReversed(booking.Id),
        booking.FundingMonth?.ToString(),
        booking.ReversesBookingId,
        journal.ReversalsOf(booking.Id).Select(r => r.Id).ToList(),
        booking.RefundsExpenseId,
        booking.RelatedSettlementId,
        booking.AccountPostings
            .Select(p => new AccountPostingDto(
                p.AccountId, catalog.FindAccount(p.AccountId)?.Name, p.Amount.Cents))
            .ToList(),
        booking.PoolPostings
            .Select(p => new PoolPostingDto(
                p.PoolId, catalog.FindPool(p.PoolId)?.Name, p.Amount.Cents))
            .ToList(),
        booking.Splits
            .Select(s => new ExpenseSplitDto(
                s.Id,
                s.Amount.Cents,
                s.PoolId,
                catalog.FindPool(s.PoolId)?.Name,
                s.CategoryId,
                catalog.FindCategory(s.CategoryId)?.Name,
                s.Tax is { } tax
                    ? new TaxMarkingDto(
                        tax.Relevant,
                        tax.TaxCategoryId,
                        tax.TaxCategoryId is { } taxId
                            ? catalog.FindTaxCategory(taxId)?.Name
                            : null,
                        tax.MarkedAmount?.Cents,
                        tax.Note)
                    : null,
                s.Note))
            .ToList(),
        booking.TransferPurposes
            .Select(p => new PoolAllocationDto(
                p.PoolId, catalog.FindPool(p.PoolId)?.Name, p.Amount.Cents))
            .ToList(),
        booking.ReassignmentAccounts
            .Select(p => new AccountAllocationDto(
                p.AccountId, catalog.FindAccount(p.AccountId)?.Name, p.Amount.Cents))
            .ToList(),
        ComputeTotal(booking, journal),
        RefundedByPool(booking, journal));

    /// <summary>
    /// Bereits gewährte Erstattungen je Ursprungs-Pool (Review R3 P2):
    /// Summe der Pool-Postings aller Erstattungs-Buchungen mit
    /// <c>RefundsExpenseId</c> = diese Buchung. Dieselbe Quelle wie die
    /// Deckelprüfung in <c>RefundService</c> — Erstattungen sind nicht
    /// stornierbar (B2), eine einfache Summe ist damit korrekt. Für
    /// Nicht-Ausgaben (und Ausgaben ohne Erstattungen) leer.
    /// </summary>
    private static IReadOnlyList<RefundedPoolDto> RefundedByPool(
        Booking booking, BookingJournal journal) =>
        journal.Bookings
            .Where(b => b.Type == BookingType.Refund
                        && b.RefundsExpenseId == booking.Id)
            .SelectMany(b => b.PoolPostings)
            .GroupBy(p => p.PoolId)
            .Select(g => new RefundedPoolDto(g.Key, g.Sum(p => p.Amount.Cents)))
            .OrderBy(r => r.PoolId, StringComparer.Ordinal)
            .ToList();

    public static BalancesDto ToBalancesDto(
        BalanceQueryService.Balances balances,
        MasterDataCatalog catalog) => new(
        balances.Accounts
            .Select(a => new AccountBalanceDto(
                a.AccountId, a.Name, StatusToString(a.Status), a.Balance.Cents))
            .ToList(),
        balances.Pools
            .Select(p => new PoolBalanceDto(
                p.PoolId, p.Name, StatusToString(p.Status), p.Balance.Cents))
            .ToList(),
        balances.TotalAccountBalance.Cents,
        balances.TotalPoolBalance.Cents,
        balances.TotalIncome.Cents,
        balances.AccountPoolGap.Cents,
        balances.UnallocatedOpeningBalances
            .Select(u => new UnallocatedOpeningBalanceDto(
                u.OpeningBalanceId,
                u.Date.ToString("yyyy-MM-dd"),
                u.AccountId,
                catalog.FindAccount(u.AccountId)?.Name,
                u.Unallocated.Cents))
            .ToList());

    public static SettlementDto ToSettlementDto(
        SettlementQueryService.SettlementView view) => new(
        view.Id,
        view.SourceAccountId,
        view.SourceAccountName,
        view.TargetPoolId,
        view.TargetPoolName,
        view.Amount.Cents,
        view.RestAmount.Cents,
        view.Status == SettlementStatus.Beglichen ? "settled" : "open",
        view.CreatedByBookingId,
        view.CreatedDate.ToString("yyyy-MM-dd"),
        view.SettledByBookingId,
        view.SettledDate?.ToString("yyyy-MM-dd"));

    /// <summary>
    /// Gesamtbetrag einer Buchung in Cent (Anzeigewert): Income,
    /// OpeningBalance, Refund = positiv; Expense = positiv (Ausgabe);
    /// Transfers/Umverteilungen = Betrag der negativen Ausgangsposition;
    /// Storno = eigene historische Postings (s. <see cref="ReversalTotal"/>);
    /// Kontokorrektur = signiert. Nicht-Storno-Typen hängen nur von den
    /// eigenen (unveränderlichen) Postings ab — kein Journal-Kontext.
    /// </summary>
    public static long ComputeTotal(Booking b, BookingJournal journal) =>
        b.Type == BookingType.Reversal
            ? ReversalTotal(b, journal)
            : OriginalDisplayTotal(b.Type, b.AccountPostings, b.PoolPostings);

    /// <summary>
    /// Anzeigetotal eines Nicht-Storno-Typs aus den eigenen Postings
    /// (reine Transport-Konvention, keine Fachlogik): Income,
    /// OpeningBalance, Refund = positiv; Expense = positiv (Ausgabe);
    /// Transfers/Transfer-Umverteilungen = Betrag der negativen
    /// Ausgangsposition; Umwidmung = Betrag der negativen Poolposition;
    /// Kontokorrektur = signiert.
    /// </summary>
    private static long OriginalDisplayTotal(
        BookingType type,
        IReadOnlyList<AccountPosting> accounts,
        IReadOnlyList<PoolPosting> pools) => type switch
    {
        BookingType.Income => accounts[0].Amount.Cents,
        BookingType.Expense => accounts[0].Amount.Negate().Cents,
        BookingType.OpeningBalance => accounts[0].Amount.Cents,
        BookingType.AccountTransfer =>
            accounts.Single(p => p.Amount < Money.Zero).Amount.Negate().Cents,
        BookingType.TransferReassignment =>
            accounts.Single(p => p.Amount < Money.Zero).Amount.Negate().Cents,
        BookingType.Reassignment =>
            pools.Single(p => p.Amount < Money.Zero).Amount.Negate().Cents,
        BookingType.Refund => accounts[0].Amount.Cents,
        BookingType.BalanceCorrection => accounts[0].Amount.Cents,
        _ => throw new LarioDomainException($"Unbekannter Buchungstyp '{type}'.")
    };

    /// <summary>
    /// Storno-Total (Review R1 P2 / R2 P2): die <b>Höhe</b> kommt aus den
    /// eigenen, unveränderlichen Storno-Postings — nicht aus dem Ist-Zustand
    /// des Originals, das nach dem Storno noch per In-place-Korrektur
    /// geändert werden darf. Eine solche Korrektur darf die historischen
    /// Anzeigewerte des Stornos nicht rückwirkend umschreiben (der
    /// Storno spiegelt die Bewegung zum Stornozeitpunkt). Die
    /// <b>Konvention</b> (Vorzeichen) bestimmt der Typ der Kettenspitze:
    /// <list type="number">
    /// <item>Die Kette wird über <see cref="Booking.ReversesBookingId"/>
    ///   bis zum ersten Nicht-Storno hochgelaufen (die Typen der
    ///   Kettenglieder sind unveränderlich); Abbrüche sind
    ///   Journalintegritätsfehler.</item>
    /// <item>Jedes Storno negiert exakt die Postings des Vorgängerglieds
    ///   (<c>ReversalSpec.ToBooking</c>) — die eigenen Postings des
    ///   Stornos sind also (−1)<sup>k</sup> · Postings der Kettenspitze
    ///   <i>zum Stornozeitpunkt</i> (k = Kettentiefe).</item>
    /// <item>Virtuelle Kettenspitze = diese rekonstruierten Postings; auf
    ///   sie wird die Anzeigekonvention des Spitzentyps angewendet
    ///   (<see cref="OriginalDisplayTotal"/>) und das Ergebnis je
    ///   Kettentiefe vorzeichengekehrt. Das Storno des Stornos
    ///   (Widerruf) ist damit wieder positiv.</item>
    /// </list>
    /// Konto- und Poolachse werden dabei nicht addiert — dieselbe Bewegung
    /// erscheint auf beiden Achsen, die Konvention wählt genau eine.
    /// </summary>
    private static long ReversalTotal(Booking reversal, BookingJournal journal)
    {
        var byId = journal.Bookings.ToDictionary(b => b.Id);
        var current = reversal;
        var links = 0;
        while (current.Type == BookingType.Reversal
               && current.ReversesBookingId is { } parentId)
        {
            if (!byId.TryGetValue(parentId, out var parent))
            {
                throw new LarioDomainException(
                    $"Storno '{reversal.Id}': die Storno-Kette ist unterbrochen — " +
                    $"das Original '{parentId}' fehlt im Journal (Journalintegrität).");
            }

            current = parent;
            links++;
        }

        // (−1)^links: gerade Tiefe → eigene Postings sind die der Spitze
        // zum Stornozeitpunkt; ungerade → Negation davon.
        var negate = links % 2 == 1;
        var virtualAccounts = reversal.AccountPostings
            .Select(p => negate
                ? new AccountPosting { AccountId = p.AccountId, Amount = p.Amount.Negate() }
                : p)
            .ToList();
        var virtualPools = reversal.PoolPostings
            .Select(p => negate
                ? new PoolPosting { PoolId = p.PoolId, Amount = p.Amount.Negate() }
                : p)
            .ToList();

        var rootTotal = OriginalDisplayTotal(current.Type, virtualAccounts, virtualPools);
        return negate ? -rootTotal : rootTotal;
    }

    /// <summary>
    /// Haupt-Konto für die Liste: bei Einzel-Posten das Konto, bei
    /// Transfers/Transfer-Umverteilungen das Konto der negativen
    /// Ausgangsposition (Quelle).
    /// </summary>
    public static (string? Id, string? Name) PrimaryAccount(
        Booking b, MasterDataCatalog catalog)
    {
        if (b.AccountPostings.Count == 0)
        {
            return (null, null);
        }

        var posting = b.AccountPostings.Count == 1
            ? b.AccountPostings[0]
            : b.AccountPostings.First(p => p.Amount < Money.Zero);
        return (posting.AccountId, catalog.FindAccount(posting.AccountId)?.Name);
    }

    public static string StatusToString(EntityStatus status) => status switch
    {
        EntityStatus.Active => "active",
        EntityStatus.Archived => "archived",
        _ => throw new LarioDomainException($"Unbekannter Status '{status}'.")
    };
}
