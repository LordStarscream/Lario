using Lario.Domain;
using Lario.Domain.Journal;

namespace Lario.Application.Journal;

/// <summary>
/// Anwendungsfall „Kontostandskorrektur" (Schritt 6, Rechenvertrag §5.4,
/// Design Schritt 6 §2.6/§3.2):
/// - Kontostandskorrektur = neue Buchung vom Typ
///   <see cref="BookingType.BalanceCorrection"/>: Konto-Posting (Differenz
///   zur Ist-Buchung) plus optional Notiz — KEIN Revisionsimpuls auf
///   andere Buchungen, KEIN Pool (Kontostandskorrekturen betreffen nur
///   den Kontostand, Design Schritt 6 §2.6);
/// - Idempotenz zuerst: gleiche Id mit gleichem Inhalt wird nur
///   quittiert, gleicher Id mit anderem Inhalt oder Typ ist ein
///   Konflikt;
/// - die Kontostandskorrektur wird atomar mit ihren
///   Begleichungspositionseffekten gespeichert (in der Regel keine —
///   die Buchung hat keine Konto-Pool-Zelle, Design Schritt 6 §2.2).
/// </summary>
public sealed class BalanceCorrectionService(
    IBookingRepository bookingRepository,
    ISettlementPositionRepository settlementPositionRepository,
    IMasterDataRepository masterDataRepository)
{
    /// <summary>
    /// Ergebnis einer Kontostandskorrektur: <see cref="Created"/> ist
    /// true, wenn die Buchung angelegt wurde; false bei
    /// Idempotenz-Treffer (die vorhandene Buchung wird zurückgegeben).
    /// </summary>
    public sealed record Result(bool Created, Booking Booking);

    public async Task<Result> CorrectBalanceAsync(
        BalanceCorrectionSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // Idempotenz zuerst.
        var existing = await bookingRepository.FindByIdAsync(spec.Id, cancellationToken);
        if (existing is not null)
        {
            if (existing.Type != BookingType.BalanceCorrection)
            {
                throw new DomainConflictException(
                    $"Buchungs-Id '{spec.Id}' ist bereits vergeben (Typ {existing.Type}) — " +
                    "Kontostandskorrekturen müssen eine freie Id verwenden.");
            }

            if (!IsSameContent(existing, spec))
            {
                throw new DomainConflictException(
                    $"Für die Buchung '{spec.Id}' existiert bereits eine Buchung mit " +
                    "anderem Inhalt. Gleiches Id, anderer Inhalt ist ein Konflikt — die " +
                    "vorhandene Buchung bleibt unverändert.");
            }

            return new Result(false, existing);
        }

        // Validierung wie Neuanlage (Katalog, Betrag/Notiz).
        var catalog = await masterDataRepository.LoadCatalogAsync(cancellationToken);
        spec.Validate(catalog);

        var booking = spec.ToBooking();

        // Begleichungspositionen atomar mit der Buchung nachführen —
        // in der Regel ohne Effekt (keine Konto-Pool-Zelle, Design
        // Schritt 6 §2.2), die Mechanik läuft trotzdem einmal durch.
        var journal = await bookingRepository.GetAllAsync(cancellationToken);
        var persistedPositions = await settlementPositionRepository.GetAllAsync(cancellationToken);
        var changes = SettlementPositionSync.Plan(
            journal, booking, persistedPositions,
            () => $"set-{Guid.NewGuid():N}");

        // Normale neue Buchung (kein Revisionsimpuls).
        await bookingRepository.AddAsync(booking, changes.Select(c => c.Row).ToList(), cancellationToken);
        return new Result(true, booking);
    }

    /// <summary>
    /// Inhaltliche Gleichheit (Idempotenz-Abgleich).
    /// </summary>
    private static bool IsSameContent(Booking stored, BalanceCorrectionSpec spec)
    {
        if (stored.Date != spec.Date || stored.Note != spec.Note)
        {
            return false;
        }

        var accountPostings = stored.AccountPostings.ToList();
        return accountPostings.Count == 1
            && accountPostings[0].AccountId == spec.AccountId
            && accountPostings[0].Amount == spec.Amount;
    }
}
