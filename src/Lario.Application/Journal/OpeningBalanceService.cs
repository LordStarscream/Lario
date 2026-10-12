using Lario.Domain.Journal;

namespace Lario.Application;

/// <summary>
/// Anwendungsfall „Anfangsbestand einrichten" (Schritt 3):
/// - prüft die Vorgabedaten gegen den Stammdaten-Katalog;
/// - erzeugt die Buchung (Konto- und Poolbewegungen) und speichert sie
///   atomar;
/// - ist idempotent: dieselbe Id mit gleichem Inhalt wird nicht erneut
///   gespeichert, sondern die vorhandene Buchung zurückgegeben;
///   dieselbe Id mit anderem Inhalt ist ein Konflikt.
///
/// Der Anfangsbestand ist keine Einnahme: kein Finanzierungsmonat, keine
/// Zuweisung über Einnahme-Logik (Rechenvertrag §3.7).
/// </summary>
public sealed class OpeningBalanceService(
    IMasterDataRepository masterDataRepository,
    IBookingRepository bookingRepository)
{
    /// <summary>Ergebnis einer Anlage: <see cref="Created"/> ist false bei Idempotenz-Treffer.</summary>
    public sealed record Result(bool Created, Booking Booking);

    public async Task<Result> CreateAsync(
        OpeningBalanceSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var catalog = await masterDataRepository.LoadCatalogAsync(cancellationToken);
        spec.ValidateAgainst(catalog);

        var existing = await bookingRepository.FindByIdAsync(spec.Id, cancellationToken);
        if (existing is not null)
        {
            if (existing.Type != BookingType.OpeningBalance)
            {
                throw new Lario.Domain.DomainConflictException(
                    $"Die Id '{spec.Id}' wird bereits von einer Buchung des Typs " +
                    $"{existing.Type} verwendet.");
            }

            if (!SpecMatchesBooking(spec, existing))
            {
                throw new Lario.Domain.DomainConflictException(
                    $"Für den Anfangsbestand '{spec.Id}' existiert bereits eine Buchung " +
                    "mit anderem Inhalt. Gleiches Id, anderer Inhalt ist ein Konflikt — " +
                    "die vorhandene Buchung bleibt unverändert.");
            }

            return new Result(false, existing);
        }

        var booking = spec.ToBooking();
        await bookingRepository.AddAsync(booking, cancellationToken);
        return new Result(true, booking);
    }

    /// <summary>
    /// Prüft, ob die Vorgabe exakt den Inhalt der vorhandenen Buchung
    /// hat: Stichtag, Konto, Gesamtbetrag, Notiz und Pool-Verteilung
    /// (multimengen-gleich, Reihenfolge egal).
    /// </summary>
    private static bool SpecMatchesBooking(OpeningBalanceSpec spec, Booking booking)
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
        if (accountPosting.AccountId != spec.AccountId || accountPosting.Amount != spec.Total)
        {
            return false;
        }

        var specLines = spec.Distribution
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
