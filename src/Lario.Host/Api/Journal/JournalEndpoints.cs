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
/// (gemeinsamer <see cref="JournalDtoMapper"/>) und leiten reine
/// Anzeige-Werte ab.
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

        endpoints.MapGet("/api/v1/opening-balances", ListOpeningBalances)
            .WithName("listOpeningBalances")
            .Produces<IReadOnlyList<OpeningBalanceListItemDto>>(StatusCodes.Status200OK)
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
            .Select(b => JournalDtoMapper.ToListItem(b, catalog, journal))
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
        return Results.Ok(JournalDtoMapper.ToDetail(booking, catalog, journal));
    }

    // ---------- Kontostand ----------

    private static async Task<IResult> GetBalances(
        BalanceQueryService balances,
        IMasterDataRepository masterData,
        CancellationToken cancellationToken)
    {
        var result = await balances.GetBalancesAsync(cancellationToken);
        var catalog = await masterData.LoadCatalogAsync(cancellationToken);
        return Results.Ok(JournalDtoMapper.ToBalancesDto(result, catalog));
    }

    // ---------- Anfangsbestände (Zwischenstep 7.5, A4) ----------

    private static async Task<IResult> ListOpeningBalances(
        IBookingRepository bookings,
        IMasterDataRepository masterData,
        CancellationToken cancellationToken)
    {
        var all = await bookings.GetAllAsync(cancellationToken);
        var catalog = await masterData.LoadCatalogAsync(cancellationToken);

        // Wirksam (nicht wirkungsgestornt) sind bei Anfangsbeständen alle
        // gespeicherten Einträge — sie sind designgemäß nicht stornierbar
        // (Rechenvertrag §3.7); Änderungen laufen als In-place-Korrektur
        // auf derselben Id. Pro Konto ist genau ein Eintrag vorhanden
        // (Einmaligkeitsregel, Eindeutigkeitsindex). Sortierung aufsteigend
        // (Stichtag, Id) — die Liste ist ein Ist-Überblick, keine
        // Aktivitätschronik.
        var list = all
            .Where(b => b.Type == BookingType.OpeningBalance)
            .OrderBy(b => b.Date)
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .Select(b => JournalDtoMapper.ToOpeningBalanceListItem(b, catalog))
            .ToList();

        return Results.Ok(list);
    }

    // ---------- Ausgleichspositionen ----------

    private static async Task<IResult> GetSettlements(
        SettlementQueryService settlements,
        CancellationToken cancellationToken)
    {
        var views = await settlements.GetAllAsync(cancellationToken);
        return Results.Ok(views.Select(JournalDtoMapper.ToSettlementDto).ToList());
    }

    private static async Task<IResult> GetOpenSettlements(
        SettlementQueryService settlements,
        CancellationToken cancellationToken)
    {
        var views = await settlements.GetOpenAsync(cancellationToken);
        return Results.Ok(views.Select(JournalDtoMapper.ToSettlementDto).ToList());
    }

    // ---------- Filter-Prüfung (Transport-Validierung, keine Fachlogik) ----------

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
}
