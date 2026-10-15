using Lario.Application;
using Lario.Application.Journal;
using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Lario.Host.Api.Journal;

/// <summary>
/// Journal-Leseendpunkte (Schritt 7, T3; Design §2.2/§2.3). Alle
/// Endpunkte liegen unter <c>/api/v1</c> und sind durch die
/// ApiAuthMiddleware (T1) geschützt; Fehler folgen dem Schritt-7-
/// Fehlerformat über <c>ExceptionToHttpResult</c>.
///
/// <b>Keine Fachlogik hier</b>: Abgleich (Balances) und
/// Ausgleichspositionen (Settlements) kommen aus den Application-
/// Services; <c>effectivelyReversed</c> und die Kette-Informationen
/// aus <c>BookingJournal</c>. Die Endpunkte mappen nur DTO ↔ Domain
/// und leiten reine Anzeige-Werte (Gesamtbetrag pro Buchungsart) ab.
///
/// Konventionen:
/// * <c>GET /bookings</c>: Filter <c>type</c> (Enum-Name,
///   case-insensitiv), <c>from</c>/<c>to</c> (Datum, inklusive),
///   <c>limit</c> (Default 200, max 1000); Sortierung (Datum, Id)
///   absteigend (neueste zuerst).
/// * Unbekannte Id → <c>InvalidReferenceException</c> → 400 invalid_reference.
/// * Geldbeträge als long in Cent (D6).
/// </summary>
public static class JournalEndpoints
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 1000;

    public static void MapJournalRead(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/bookings", GetBookings)
            .WithName("listBookings")
            .Produces<IReadOnlyList<BookingListItemDto>>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapGet("/api/v1/bookings/{id}", GetBooking)
            .WithName("getBooking")
            .Produces<BookingDetailDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapGet("/api/v1/balances", GetBalances)
            .WithName("getBalances")
            .Produces<BalancesDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapGet("/api/v1/settlements", GetSettlements)
            .WithName("listSettlements")
            .Produces<IReadOnlyList<SettlementDto>>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapGet("/api/v1/settlements/open", GetOpenSettlements)
            .WithName("listOpenSettlements")
            .Produces<IReadOnlyList<SettlementDto>>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);
    }

    // ---------- Buchungen ----------

    private static async Task<IResult> GetBookings(
        string? type,
        DateOnly? from,
        DateOnly? to,
        int? limit,
        IBookingRepository bookings,
        IMasterDataRepository masterData,
        CancellationToken cancellationToken)
    {
        var bookingType = ParseBookingType(type);
        var effectiveLimit = ResolveLimit(limit);

        var all = await bookings.GetAllAsync(cancellationToken);
        var catalog = await masterData.LoadCatalogAsync(cancellationToken);
        var journal = new BookingJournal(all);

        var list = all
            .Where(b => bookingType is null || b.Type == bookingType)
            .Where(b => from is null || b.Date >= from.Value)
            .Where(b => to is null || b.Date <= to.Value)
            .OrderByDescending(b => b.Date)
            .ThenByDescending(b => b.Id, StringComparer.Ordinal)
            .Take(effectiveLimit)
            .Select(b => ToListItem(b, catalog, journal))
            .ToList();

        return Results.Ok(list);
    }

    private static async Task<IResult> GetBooking(
        string id,
        IBookingRepository bookings,
        IMasterDataRepository masterData,
        CancellationToken cancellationToken)
    {
        var all = await bookings.GetAllAsync(cancellationToken);
        var booking = all.FirstOrDefault(b => b.Id == id)
            ?? throw new InvalidReferenceException($"Unbekannte Buchung '{id}'.");

        var catalog = await masterData.LoadCatalogAsync(cancellationToken);
        var journal = new BookingJournal(all);
        return Results.Ok(ToDetail(booking, catalog, journal));
    }

    // ---------- Kontostand ----------

    private static async Task<IResult> GetBalances(
        BalanceQueryService balances,
        IMasterDataRepository masterData,
        CancellationToken cancellationToken)
    {
        var result = await balances.GetBalancesAsync(cancellationToken);
        var catalog = await masterData.LoadCatalogAsync(cancellationToken);
        return Results.Ok(ToDto(result, catalog));
    }

    // ---------- Ausgleichspositionen ----------

    private static async Task<IResult> GetSettlements(
        SettlementQueryService settlements,
        CancellationToken cancellationToken)
    {
        var views = await settlements.GetAllAsync(cancellationToken);
        return Results.Ok(views.Select(ToDto).ToList());
    }

    private static async Task<IResult> GetOpenSettlements(
        SettlementQueryService settlements,
        CancellationToken cancellationToken)
    {
        var views = await settlements.GetOpenAsync(cancellationToken);
        return Results.Ok(views.Select(ToDto).ToList());
    }

    // ---------- Mappings (DTO ↔ Domain) ----------

    private static BookingListItemDto ToListItem(
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

    private static BookingDetailDto ToDetail(
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

    private static BalancesDto ToDto(BalanceQueryService.Balances balances, MasterDataCatalog catalog) => new(
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

    private static SettlementDto ToDto(SettlementQueryService.SettlementView view) => new(
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

    // ---------- Anzeige-/Protokoll-Ableitungen (keine Fachlogik) ----------

    /// <summary>
    /// Gesamtbetrag einer Buchung in Cent (Anzeigewert): Income,
    /// OpeningBalance, Refund = positiv; Expense = positiv (Ausgabe);
    /// Transfers/Umverteilungen = Betrag der negativen Ausgangsposition;
    /// Storno = Summe aller (negierten) Posten, signiert;
    /// Kontokorrektur = signiert.
    /// </summary>
    private static long ComputeTotal(Booking b) => b.Type switch
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
    private static (string? Id, string? Name) PrimaryAccount(
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

    private static BookingType? ParseBookingType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<BookingType>(value, ignoreCase: true, out var type)
            ? type
            : throw new LarioDomainException(
                $"Buchungstyp '{value}' ist unbekannt. Erlaubt: " +
                $"{string.Join(", ", Enum.GetNames<BookingType>()).ToLowerInvariant()}.");
    }

    private static int ResolveLimit(int? limit)
    {
        if (limit is null)
        {
            return DefaultLimit;
        }

        if (limit.Value < 1)
        {
            throw new LarioDomainException("Der Filter 'limit' muss mindestens 1 sein.");
        }

        if (limit.Value > MaxLimit)
        {
            throw new LarioDomainException(
                $"Der Filter 'limit' darf maximal {MaxLimit} sein.");
        }

        return limit.Value;
    }

    private static string StatusToString(EntityStatus status) => status switch
    {
        EntityStatus.Active => "active",
        EntityStatus.Archived => "archived",
        _ => throw new LarioDomainException($"Unbekannter Status '{status}'.")
    };
}
