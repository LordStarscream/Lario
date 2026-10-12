using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;

namespace Lario.Application.Journal;

/// <summary>
/// Anwendungsfall „Ausgabe erfassen" (Schritt 4):
/// - verlangt eine stabile Wiederholungsidentität (Id) — ohne sie kann
///   keine Buchung entstehen;
/// - prüft zuerst die Idempotenz (vor der Katalog-Validierung): dieselbe
///   Id mit gleichem Inhalt wird nicht erneut gespeichert, sondern die
///   vorhandene Buchung unverändert zurückgegeben — auch wenn die
///   beteiligten Konten/Pools/Kategorien inzwischen archiviert wurden
///   (die Steuerkennzeichnung ist ein Snapshot und wird nicht neu
///   abgeleitet); dieselbe Id mit anderem Inhalt ist ein Konflikt; eine
///   Id, die bereits eine Buchung eines anderen Typs trägt, ist
///   ebenfalls ein Konflikt;
/// - prüft bei tatsächlicher Neuanlage die Vorgabe gegen den
///   Stammdaten-Katalog (Splitregel exakt, aktive Referenzen,
///   Steuerkennzeichnung je Teilposten) und speichert Buchung, Postings
///   und Teilposten atomar.
/// </summary>
public sealed class ExpenseService(
    IMasterDataRepository masterDataRepository,
    IBookingRepository bookingRepository)
{
    /// <summary>Ergebnis einer Anlage: <see cref="Created"/> ist false bei Idempotenz-Treffer.</summary>
    public sealed record Result(bool Created, Booking Booking);

    public async Task<Result> CreateAsync(
        ExpenseSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (string.IsNullOrWhiteSpace(spec.Id))
        {
            throw new LarioDomainException(
                "Ausgabe ohne Id: eine stabile Wiederholungsidentität ist erforderlich.");
        }

        var catalog = await masterDataRepository.LoadCatalogAsync(cancellationToken);

        var existing = await bookingRepository.FindByIdAsync(spec.Id, cancellationToken);
        if (existing is not null)
        {
            if (existing.Type != BookingType.Expense)
            {
                throw new DomainConflictException(
                    $"Die Id '{spec.Id}' wird bereits von einer Buchung des Typs " +
                    $"{existing.Type} verwendet.");
            }

            if (!SpecMatchesBooking(spec, existing))
            {
                throw new DomainConflictException(
                    $"Für die Ausgabe '{spec.Id}' existiert bereits eine Buchung " +
                    "mit anderem Inhalt. Gleiches Id, anderer Inhalt ist ein Konflikt — " +
                    "die vorhandene Buchung bleibt unverändert.");
            }

            return new Result(false, existing);
        }

        // Tatsächliche Neuanlage: die Vorgabe muss aktive Stammdaten referenzieren.
        spec.Validate(catalog);

        var booking = spec.ToBooking(catalog);
        await bookingRepository.AddAsync(booking, cancellationToken);
        return new Result(true, booking);
    }

    /// <summary>
    /// Prüft, ob die Vorgabe exakt den Inhalt der vorhandenen Buchung
    /// hat: Fachdatum, Konto, Gesamtbetrag, Notiz und Teilposten
    /// (multimengen-gleich nach Teilposten-Id; Betrag, Pool, Kategorie
    /// und Notiz müssen exakt stimmen). Eine explizit übergebene
    /// Steuerkennzeichnung ist Inhalt der Vorgabe und muss exakt
    /// stimmen. Eine fehlende Kennzeichnung ist dagegen kein Konflikt:
    /// eine gespeicherte Kennzeichnung wurde bei der Anlage aus der
    /// damals wirksamen Voreinstellung materialisiert (Snapshot —
    /// Voreinstellungen können sich zwischen Original und Wiederholung
    /// geändert oder entfernt haben; Rechenvertrag: die Rechnung hängt
    /// nur von den gespeicherten Eingabedaten ab).
    /// </summary>
    private static bool SpecMatchesBooking(ExpenseSpec spec, Booking booking)
    {
        if (booking.Date != spec.Date || booking.Note != spec.Note)
        {
            return false;
        }

        if (booking.AccountPostings.Count != 1)
        {
            return false;
        }

        var accountPosting = booking.AccountPostings[0];
        if (accountPosting.AccountId != spec.AccountId
            || accountPosting.Amount != spec.TotalAmount.Negate())
        {
            return false;
        }

        var bookingSplits = booking.Splits.ToDictionary(s => s.Id, StringComparer.Ordinal);
        if (bookingSplits.Count != spec.Splits.Count)
        {
            return false;
        }

        foreach (var split in spec.Splits)
        {
            if (!bookingSplits.TryGetValue(split.Id, out var stored))
            {
                return false;
            }

            if (stored.Amount != split.Amount
                || stored.PoolId != split.PoolId
                || stored.CategoryId != split.CategoryId
                || stored.Note != split.Note)
            {
                return false;
            }

            if (split.Tax is not null && !TaxMatches(split.Tax, stored.Tax))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TaxMatches(TaxMarking expected, TaxMarking? stored)
    {
        if (stored is null)
        {
            return false;
        }

        return expected.Relevant == stored.Relevant
            && expected.TaxCategoryId == stored.TaxCategoryId
            && expected.MarkedAmount == stored.MarkedAmount
            && expected.Note == stored.Note;
    }
}
