using System.Globalization;
using Lario.Application;
using Lario.Application.Journal;
using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Lario.Host.Api.Journal;

/// <summary>
/// Journal-Schreibendpunkte (Schritt 7, T4; Design §2.4). Alle
/// Endpunkte liegen unter <c>/api/v1</c>, sind durch die
/// ApiAuthMiddleware (T1) geschützt und folgen dem Schritt-7-
/// Fehlerformat über das Exception-Mapping (400 validation_error /
/// invalid_reference, 409 domain_conflict, 500 internal_error).
///
/// <b>Keine Fachlogik hier</b>: die Endpunkte mappen Command-DTOs
/// (primitive Typen, Cent als <c>long</c>) auf die bestehenden
/// Domain-Specs und rufen die Application-Services auf — Idempotenz,
/// Validierung, Revision und Fachregeln bleiben dort. Die Antwort ist
/// <see cref="BookingCreatedDto"/> (Buchungsdetail + Idempotenz-Flag),
/// gemappt über den gemeinsamen <see cref="JournalDtoMapper"/>.
///
/// Datums- und Monatsfelder kommen als String und werden hier als
/// Transport-Format geprüft (<c>yyyy-MM-dd</c> / <c>yyyy-MM</c>);
/// ungültige Formate werden 400 validation_error.
/// </summary>
public static class JournalWriteEndpoints
{
    public static void MapJournalWrite(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/expenses", CreateExpense)
            .WithName("createExpense")
            .Produces<BookingCreatedDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<Api.ErrorResponse>(StatusCodes.Status409Conflict);

        endpoints.MapPost("/api/v1/income", CreateIncome)
            .WithName("createIncome")
            .Produces<BookingCreatedDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<Api.ErrorResponse>(StatusCodes.Status409Conflict);

        endpoints.MapPost("/api/v1/opening-balances", CreateOpeningBalance)
            .WithName("createOpeningBalance")
            .Produces<BookingCreatedDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<Api.ErrorResponse>(StatusCodes.Status409Conflict);

        endpoints.MapPost("/api/v1/transfers", CreateTransfer)
            .WithName("createTransfer")
            .Produces<BookingCreatedDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<Api.ErrorResponse>(StatusCodes.Status409Conflict);

        endpoints.MapPost("/api/v1/reassignments", CreateReassignment)
            .WithName("createReassignment")
            .Produces<BookingCreatedDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<Api.ErrorResponse>(StatusCodes.Status409Conflict);

        endpoints.MapPost("/api/v1/transfer-reassignments", CreateTransferReassignment)
            .WithName("createTransferReassignment")
            .Produces<BookingCreatedDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<Api.ErrorResponse>(StatusCodes.Status409Conflict);

        endpoints.MapPost("/api/v1/bookings/{id}/corrections", CorrectBooking)
            .WithName("correctBooking")
            .Produces<BookingCreatedDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<Api.ErrorResponse>(StatusCodes.Status409Conflict);

        endpoints.MapPost("/api/v1/bookings/{id}/reversals", CreateReversal)
            .WithName("createReversal")
            .Produces<BookingCreatedDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<Api.ErrorResponse>(StatusCodes.Status409Conflict);

        endpoints.MapPost("/api/v1/bookings/{id}/refunds", CreateRefund)
            .WithName("createRefund")
            .Produces<BookingCreatedDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<Api.ErrorResponse>(StatusCodes.Status409Conflict);

        endpoints.MapPost("/api/v1/balance-corrections", CreateBalanceCorrection)
            .WithName("createBalanceCorrection")
            .Produces<BookingCreatedDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<Api.ErrorResponse>(StatusCodes.Status409Conflict);
    }

    // ---------- Ausgabe / Einnahme / Anfangsbestand ----------

    private static async Task<IResult> CreateExpense(
        ExpenseCommand command,
        ExpenseService service,
        IBookingRepository bookings,
        IMasterDataRepository masterData,
        CancellationToken cancellationToken)
    {
        var spec = new ExpenseSpec
        {
            Id = command.Id,
            Date = ParseDate(command.Date, "date"),
            TotalAmount = new Money(command.TotalAmount),
            AccountId = command.AccountId,
            Note = command.Note,
            Splits = command.Splits.Select(MapSplitSpec).ToList(),
            ExpectedBaseRevision = command.ExpectedBaseRevision
        };

        var result = await service.CreateAsync(spec, cancellationToken);
        return await CreatedAsync(result.Created, result.Booking, bookings, masterData, cancellationToken);
    }

    private static async Task<IResult> CreateIncome(
        IncomeCommand command,
        IncomeService service,
        IBookingRepository bookings,
        IMasterDataRepository masterData,
        CancellationToken cancellationToken)
    {
        var spec = new IncomeSpec
        {
            Id = command.Id,
            Date = ParseDate(command.Date, "date"),
            Amount = new Money(command.Amount),
            AccountId = command.AccountId,
            Note = command.Note,
            FundingMonth = ParseYearMonth(command.FundingMonth, "fundingMonth"),
            Allocations = command.Allocations
                .Select(a => new IncomeAllocationSpec(a.PoolId, new Money(a.Amount)))
                .ToList(),
            ExpectedBaseRevision = command.ExpectedBaseRevision
        };

        var result = await service.CreateAsync(spec, cancellationToken);
        return await CreatedAsync(result.Created, result.Booking, bookings, masterData, cancellationToken);
    }

    private static async Task<IResult> CreateOpeningBalance(
        OpeningBalanceCommand command,
        OpeningBalanceService service,
        IBookingRepository bookings,
        IMasterDataRepository masterData,
        CancellationToken cancellationToken)
    {
        var spec = new OpeningBalanceSpec(
            command.Id,
            ParseDate(command.Date, "date"),
            command.AccountId,
            new Money(command.Total),
            command.Distribution
                .Select(d => new PoolAllocation(d.PoolId, new Money(d.Amount)))
                .ToList(),
            command.Note);

        var result = await service.CreateAsync(spec, cancellationToken);
        return await CreatedAsync(result.Created, result.Booking, bookings, masterData, cancellationToken);
    }

    // ---------- Transfers / Umverteilungen ----------

    private static async Task<IResult> CreateTransfer(
        TransferCommand command,
        TransferService service,
        IBookingRepository bookings,
        IMasterDataRepository masterData,
        CancellationToken cancellationToken)
    {
        var spec = new TransferSpec
        {
            Id = command.Id,
            Date = ParseDate(command.Date, "date"),
            FromAccountId = command.FromAccountId,
            ToAccountId = command.ToAccountId,
            Amount = new Money(command.Amount),
            Purposes = command.Purposes is null
                ? null
                : command.Purposes
                    .Select(p => new PoolAllocation(p.PoolId, new Money(p.Amount)))
                    .ToList(),
            RelatedSettlementId = command.RelatedSettlementId,
            Note = command.Note
        };

        var result = await service.CreateAsync(spec, cancellationToken);
        return await CreatedAsync(result.Created, result.Booking, bookings, masterData, cancellationToken);
    }

    private static async Task<IResult> CreateReassignment(
        ReassignmentCommand command,
        ReassignmentService service,
        IBookingRepository bookings,
        IMasterDataRepository masterData,
        CancellationToken cancellationToken)
    {
        var spec = new ReassignmentSpec
        {
            Id = command.Id,
            Date = ParseDate(command.Date, "date"),
            FromPoolId = command.FromPoolId,
            ToPoolId = command.ToPoolId,
            Amount = new Money(command.Amount),
            Accounts = command.Accounts is null
                ? null
                : command.Accounts
                    .Select(a => new AccountAllocation(a.AccountId, new Money(a.Amount)))
                    .ToList(),
            Note = command.Note
        };

        var result = await service.CreateAsync(spec, cancellationToken);
        return await CreatedAsync(result.Created, result.Booking, bookings, masterData, cancellationToken);
    }

    private static async Task<IResult> CreateTransferReassignment(
        TransferReassignmentCommand command,
        TransferReassignmentService service,
        IBookingRepository bookings,
        IMasterDataRepository masterData,
        CancellationToken cancellationToken)
    {
        var spec = new TransferReassignmentSpec
        {
            Id = command.Id,
            Date = ParseDate(command.Date, "date"),
            FromAccountId = command.FromAccountId,
            ToAccountId = command.ToAccountId,
            FromPoolId = command.FromPoolId,
            ToPoolId = command.ToPoolId,
            Amount = new Money(command.Amount),
            Note = command.Note
        };

        var result = await service.CreateAsync(spec, cancellationToken);
        return await CreatedAsync(result.Created, result.Booking, bookings, masterData, cancellationToken);
    }

    // ---------- Korrektur / Storno / Erstattung / Kontostand ----------

    private static async Task<IResult> CorrectBooking(
        string id,
        CorrectionCommand command,
        CorrectionService service,
        IBookingRepository bookings,
        IMasterDataRepository masterData,
        CancellationToken cancellationToken)
    {
        var spec = new CorrectionSpec(
            id,
            command.OperationId,
            command.ExpectedBaseRevision,
            command.Date is null ? null : ParseDate(command.Date, "date"),
            command.Note,
            command.AccountId,
            command.TotalAmount is null ? null : new Money(command.TotalAmount.Value),
            command.Splits is null ? null : command.Splits.Select(MapSplitSpec).ToList(),
            command.Allocations is null
                ? null
                : command.Allocations
                    .Select(a => new IncomeAllocationSpec(a.PoolId, new Money(a.Amount)))
                    .ToList());

        var result = await service.CorrectAsync(spec, cancellationToken);
        return await CreatedAsync(result.Created, result.Booking, bookings, masterData, cancellationToken);
    }

    private static async Task<IResult> CreateReversal(
        string id,
        ReversalCommand command,
        ReversalService service,
        IBookingRepository bookings,
        IMasterDataRepository masterData,
        CancellationToken cancellationToken)
    {
        var spec = new ReversalSpec(
            command.Id,
            id,
            command.ExpectedBaseRevision,
            ParseDate(command.Date, "date"),
            command.Note);

        var result = await service.ReverseAsync(spec, cancellationToken);
        return await CreatedAsync(result.Created, result.Booking, bookings, masterData, cancellationToken);
    }

    private static async Task<IResult> CreateRefund(
        string id,
        RefundCommand command,
        RefundService service,
        IBookingRepository bookings,
        IMasterDataRepository masterData,
        CancellationToken cancellationToken)
    {
        var spec = new RefundSpec(
            command.Id,
            id,
            command.AccountId,
            ParseDate(command.Date, "date"),
            command.Lines
                .Select(l => new RefundLineSpec(l.PoolId, new Money(l.Amount)))
                .ToList(),
            command.Note);

        var result = await service.RegisterAsync(spec, cancellationToken);
        return await CreatedAsync(result.Created, result.Booking, bookings, masterData, cancellationToken);
    }

    private static async Task<IResult> CreateBalanceCorrection(
        BalanceCorrectionCommand command,
        BalanceCorrectionService service,
        IBookingRepository bookings,
        IMasterDataRepository masterData,
        CancellationToken cancellationToken)
    {
        var spec = new BalanceCorrectionSpec(
            command.Id,
            command.AccountId,
            new Money(command.Amount),
            ParseDate(command.Date, "date"),
            command.Note);

        var result = await service.CorrectBalanceAsync(spec, cancellationToken);
        return await CreatedAsync(result.Created, result.Booking, bookings, masterData, cancellationToken);
    }

    // ---------- Gemeinsame Antwort ----------

    /// <summary>
    /// Lädt nach dem Service-Aufruf den Journal-Snapshot plus Katalog
    /// (die neue Buchung ist bereits persistent) und antwortet mit dem
    /// Buchungsdetail plus Idempotenz-Flag (true = Wiederholung mit
    /// gleicher Id, keine zweite Buchung).
    /// </summary>
    private static async Task<IResult> CreatedAsync(
        bool created,
        Booking booking,
        IBookingRepository bookings,
        IMasterDataRepository masterData,
        CancellationToken cancellationToken)
    {
        var all = await bookings.GetAllAsync(cancellationToken);
        var catalog = await masterData.LoadCatalogAsync(cancellationToken);
        var journal = new BookingJournal(all);
        var latest = all.FirstOrDefault(b => b.Id == booking.Id) ?? booking;
        return Results.Ok(new BookingCreatedDto(
            JournalDtoMapper.ToDetail(latest, catalog, journal),
            !created));
    }

    // ---------- Transport-Formate ----------

    private static ExpenseSplitSpec MapSplitSpec(ExpenseSplitCommand s) => new()
    {
        Id = s.Id,
        Amount = new Money(s.Amount),
        PoolId = s.PoolId,
        CategoryId = s.CategoryId,
        Note = s.Note,
        Tax = MapTax(s.Tax)
    };

    private static TaxMarking? MapTax(ExpenseSplitTaxDto? tax) => tax is null
        ? null
        : new TaxMarking
        {
            Relevant = tax.Relevant,
            TaxCategoryId = tax.TaxCategoryId,
            MarkedAmount = tax.MarkedAmount is null ? null : new Money(tax.MarkedAmount.Value),
            Note = tax.Note
        };

    /// <summary>
    /// <c>yyyy-MM-dd</c>; ungültig → 400 validation_error.
    /// </summary>
    private static DateOnly ParseDate(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !DateOnly.TryParseExact(
                value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            throw new LarioDomainException(
                $"Das Feld '{field}' ist kein gültiges Datum (erwartet yyyy-MM-dd, " +
                $"ist '{value}').");
        }

        return date;
    }

    /// <summary>
    /// <c>yyyy-MM</c> (optional); ungültig → 400 validation_error.
    /// </summary>
    private static YearMonth? ParseYearMonth(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length != 7
            || trimmed[4] != '-'
            || !int.TryParse(trimmed.AsSpan(0, 4), out var year)
            || !int.TryParse(trimmed.AsSpan(5, 2), out var month)
            || year < 1 || month < 1 || month > 12)
        {
            throw new LarioDomainException(
                $"Das Feld '{field}' ist kein gültiger Monat (erwartet yyyy-MM, " +
                $"ist '{value}').");
        }

        return new YearMonth(year, month);
    }
}
