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
        ComputeTotal(booking),
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
        ComputeTotal(booking));

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
    /// Storno = Summe aller (negierten) Posten, signiert;
    /// Kontokorrektur = signiert.
    /// </summary>
    public static long ComputeTotal(Booking b) => b.Type switch
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
        BookingType.Reversal =>
            b.AccountPostings.Sum(p => p.Amount.Cents)
                + b.PoolPostings.Sum(p => p.Amount.Cents),
        BookingType.Refund => b.AccountPostings[0].Amount.Cents,
        BookingType.BalanceCorrection => b.AccountPostings[0].Amount.Cents,
        _ => throw new LarioDomainException($"Unbekannter Buchungstyp '{b.Type}'.")
    };

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
