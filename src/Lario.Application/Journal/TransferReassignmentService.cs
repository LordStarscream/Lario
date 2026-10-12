using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;

namespace Lario.Application.Journal;

/// <summary>
/// Anwendungsfall „Kombination aus Transfer und Umwidmung erfassen"
/// (Schritt 5, Rechenvertrag §5.2: eigener Vorgangstyp, eine atomare
/// Buchung):
/// - verlangt eine stabile Wiederholungsidentität (Id);
/// - Idempotenz zuerst wie bei den anderen Vorgangstypen;
/// - bei Neuanlage: Katalog-Validierung (aktive Konten/Pools, gleicher
///   Betrag für Konto- und Poolseite) und Journalprüfung, dass der
///   Quellkontostand den Betrag trägt;
/// - die Kombination ist vollständig aus ihren Postings ableitbar —
///   keine Zusatzverteilungen;
/// - Buchung und Ausgleichseffekte werden atomar gespeichert
///   (Rechenvertrag §3.2/§3.5). Die Zelle (Quellkonto, Quellpool) wird
///   um den Betrag reduziert und kann negativ werden (neue oder
///   vertiefte Ausgleichsposition); die Zelle (Zielkonto, Zielpool) wird
///   erhöht und kann eine bestehende Position begleichen.
/// </summary>
public sealed class TransferReassignmentService(
    IMasterDataRepository masterDataRepository,
    IBookingRepository bookingRepository,
    ISettlementPositionRepository settlementPositionRepository)
{
    /// <summary>Ergebnis einer Anlage: <see cref="Created"/> ist false bei Idempotenz-Treffer.</summary>
    public sealed record Result(bool Created, Booking Booking);

    public async Task<Result> CreateAsync(
        TransferReassignmentSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (string.IsNullOrWhiteSpace(spec.Id))
        {
            throw new LarioDomainException(
                "Kombination ohne Id: eine stabile Wiederholungsidentität ist erforderlich.");
        }

        var catalog = await masterDataRepository.LoadCatalogAsync(cancellationToken);

        var existing = await bookingRepository.FindByIdAsync(spec.Id, cancellationToken);
        if (existing is not null)
        {
            if (existing.Type != BookingType.TransferReassignment)
            {
                throw new DomainConflictException(
                    $"Die Id '{spec.Id}' wird bereits von einer Buchung des Typs " +
                    $"{existing.Type} verwendet.");
            }

            if (!SpecMatchesBooking(spec, existing))
            {
                throw new DomainConflictException(
                    $"Für die Kombination '{spec.Id}' existiert bereits eine Buchung " +
                    "mit anderem Inhalt. Gleiches Id, anderer Inhalt ist ein Konflikt — " +
                    "die vorhandene Buchung bleibt unverändert.");
            }

            return new Result(false, existing);
        }

        // Tatsächliche Neuanlage: aktive Stammdaten und Journalprüfung.
        spec.Validate(catalog);

        var bookings = await bookingRepository.GetAllAsync(cancellationToken);
        var persistedPositions = await settlementPositionRepository.GetAllAsync(cancellationToken);
        var journal = new BookingJournal(bookings);

        var sourceBalance = journal.AccountBalance(spec.FromAccountId);
        if (sourceBalance.Cents < spec.Amount.Cents)
        {
            throw new LarioDomainException(
                $"Kombination '{spec.Id}': der Stand des Quellkontos " +
                $"'{spec.FromAccountId}' ({sourceBalance.ToEuro():0.00} €) trägt den Betrag " +
                $"({spec.Amount.ToEuro():0.00} €) nicht.");
        }

        var booking = spec.ToBooking();

        var changes = SettlementPositionSync.Plan(
            bookings, booking, persistedPositions,
            () => $"set-{Guid.NewGuid():N}");

        await bookingRepository.AddAsync(
            booking, changes.Select(c => c.Row).ToList(), cancellationToken);
        return new Result(true, booking);
    }

    /// <summary>
    /// Prüft, ob die Vorgabe exakt den Inhalt der vorhandenen Buchung
    /// hat: Fachdatum, Konten, Pools, Betrag und Notiz.
    /// </summary>
    private static bool SpecMatchesBooking(TransferReassignmentSpec spec, Booking booking)
    {
        if (booking.Date != spec.Date || booking.Note != spec.Note)
        {
            return false;
        }

        if (booking.AccountPostings.Count != 2 || booking.PoolPostings.Count != 2)
        {
            return false;
        }

        var accountOut = booking.AccountPostings.FirstOrDefault(p => p.Amount.Cents < 0);
        var accountIn = booking.AccountPostings.FirstOrDefault(p => p.Amount.Cents > 0);
        var poolOut = booking.PoolPostings.FirstOrDefault(p => p.Amount.Cents < 0);
        var poolIn = booking.PoolPostings.FirstOrDefault(p => p.Amount.Cents > 0);

        return accountOut is not null
            && accountIn is not null
            && poolOut is not null
            && poolIn is not null
            && accountOut.Amount == spec.Amount.Negate()
            && accountIn.Amount == spec.Amount
            && poolOut.Amount == spec.Amount.Negate()
            && poolIn.Amount == spec.Amount
            && accountOut.AccountId == spec.FromAccountId
            && accountIn.AccountId == spec.ToAccountId
            && poolOut.PoolId == spec.FromPoolId
            && poolIn.PoolId == spec.ToPoolId;
    }
}
