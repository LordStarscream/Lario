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
        ComputeTotal(booking, journal));

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
    /// Storno = Negation des Original-Anzeigetotals (s. <see cref="ReversalTotal"/>);
    /// Kontokorrektur = signiert.
    /// </summary>
    public static long ComputeTotal(Booking b, BookingJournal journal) => b.Type switch
    {
        BookingType.Income => b.AccountPostings[0].Amount.Cents,
        BookingType.Expense => b.AccountPostings[0].Amount.Negate().Cents,
        BookingType.OpeningBalance => b.AccountPostings[0].Amount.Cents,
        BookingType.AccountTransfer =>
            b.AccountPostings.Single(p => p.Amount < Money.Zero).Amount.Negate().Cents,
        BookingType.TransferReassignment =>
            b.AccountPostings.Single(p => p.Amount < Money.Zero).Amount.Negate().Cents,
        BookingType.Reassignment =>
            b.PoolPostings.Single(p => p.Amount < Money.Zero).Amount.Negate().Cents,
        BookingType.Reversal => ReversalTotal(b, journal),
        BookingType.Refund => b.AccountPostings[0].Amount.Cents,
        BookingType.BalanceCorrection => b.AccountPostings[0].Amount.Cents,
        _ => throw new LarioDomainException($"Unbekannter Buchungstyp '{b.Type}'.")
    };

    /// <summary>
    /// Storno-Total (Review R1 P2): das Storno ist die exakte Negation
    /// seines Originals — die Anzeigesumme ist die Negation des
    /// Original-Anzeigetotals, aufgelöst über
    /// <see cref="Booking.ReversesBookingId"/>. Damit ist die Konvention
    /// eindeutig und typabhängig, und das Storno des Stornos (Widerruf)
    /// ist wieder positiv. Konto- und Poolachse werden nicht mehr
    /// separat aufaddiert (doppelte Zählung).
    /// </summary>
    private static long ReversalTotal(Booking reversal, BookingJournal journal)
    {
        if (reversal.ReversesBookingId is not { } originalId)
        {
            throw new LarioDomainException(
                $"Storno '{reversal.Id}' verweist auf kein Original.");
        }

        var original = journal.Bookings.FirstOrDefault(b => b.Id == originalId);
        if (original is null)
        {
            throw new LarioDomainException(
                $"Storno '{reversal.Id}': Original '{originalId}' fehlt im Journal — " +
                "Journalintegrität verletzt.");
        }

        return -ComputeTotal(original, journal);
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
