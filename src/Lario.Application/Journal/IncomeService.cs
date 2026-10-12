using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;

namespace Lario.Application.Journal;

/// <summary>
/// Anwendungsfall „Einnahme erfassen" (Schritt 4):
/// - verlangt eine stabile Wiederholungsidentität (Id) — ohne sie kann
///   keine Buchung entstehen;
/// - prüft zuerst die Idempotenz (vor der Katalog-Validierung): dieselbe
///   Id mit gleichem Inhalt wird nicht erneut gespeichert, sondern die
///   vorhandene Buchung unverändert zurückgegeben — auch wenn die
///   beteiligten Konten/Pools inzwischen archiviert wurden; dieselbe Id
///   mit anderem Inhalt ist ein Konflikt; eine Id, die bereits eine
///   Buchung eines anderen Typs trägt, ist ebenfalls ein Konflikt;
/// - prüft bei tatsächlicher Neuanlage die Vorgabe gegen den
///   Stammdaten-Katalog und speichert Buchung und Postings atomar
///   (vollständige Einnahmeverteilung, exakt ein Kontozugang,
///   materialisierter Finanzierungsmonat).
/// </summary>
public sealed class IncomeService(
    IMasterDataRepository masterDataRepository,
    IBookingRepository bookingRepository)
{
    /// <summary>Ergebnis einer Anlage: <see cref="Created"/> ist false bei Idempotenz-Treffer.</summary>
    public sealed record Result(bool Created, Booking Booking);

    public async Task<Result> CreateAsync(
        IncomeSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (string.IsNullOrWhiteSpace(spec.Id))
        {
            throw new LarioDomainException(
                "Einnahme ohne Id: eine stabile Wiederholungsidentität ist erforderlich.");
        }

        var catalog = await masterDataRepository.LoadCatalogAsync(cancellationToken);

        var existing = await bookingRepository.FindByIdAsync(spec.Id, cancellationToken);
        if (existing is not null)
        {
            if (existing.Type != BookingType.Income)
            {
                throw new DomainConflictException(
                    $"Die Id '{spec.Id}' wird bereits von einer Buchung des Typs " +
                    $"{existing.Type} verwendet.");
            }

            if (!SpecMatchesBooking(spec, existing))
            {
                throw new DomainConflictException(
                    $"Für die Einnahme '{spec.Id}' existiert bereits eine Buchung " +
                    "mit anderem Inhalt. Gleiches Id, anderer Inhalt ist ein Konflikt — " +
                    "die vorhandene Buchung bleibt unverändert.");
            }

            return new Result(false, existing);
        }

        // Tatsächliche Neuanlage: die Vorgabe muss aktive Stammdaten referenzieren.
        spec.Validate(catalog);

        var booking = spec.ToBooking();
        await bookingRepository.AddAsync(booking, cancellationToken);
        return new Result(true, booking);
    }

    /// <summary>
    /// Prüft, ob die Vorgabe exakt den Inhalt der vorhandenen Buchung
    /// hat: Fachdatum, Konto, Gesamtbetrag, Notiz, wirksamer
    /// Finanzierungsmonat und Pool-Verteilung (multimengen-gleich,
    /// Reihenfolge egal).
    /// </summary>
    private static bool SpecMatchesBooking(IncomeSpec spec, Booking booking)
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
        if (accountPosting.AccountId != spec.AccountId || accountPosting.Amount != spec.Amount)
        {
            return false;
        }

        if (booking.FundingMonth != spec.EffectiveFundingMonth())
        {
            return false;
        }

        var specLines = spec.Allocations
            .OrderBy(l => l.PoolId, StringComparer.Ordinal)
            .Select(l => (l.PoolId, l.Amount))
            .ToList();

        var bookingLines = booking.PoolPostings
            .OrderBy(l => l.PoolId, StringComparer.Ordinal)
            .Select(l => (l.PoolId, l.Amount))
            .ToList();

        return specLines.SequenceEqual(bookingLines);
    }
}
