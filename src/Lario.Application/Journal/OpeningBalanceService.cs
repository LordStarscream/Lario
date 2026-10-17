using Lario.Application.Journal;
using Lario.Domain.Journal;

namespace Lario.Application;

/// <summary>
/// Anwendungsfall „Anfangsbestand einrichten" (Schritt 3):
/// - verlangt eine stabile Wiederholungsidentität (Id) — ohne sie kann
///   keine Buchung entstehen;
/// - prüft zuerst die Idempotenz (vor der Katalog-Validierung): dieselbe
///   Id mit gleichem Inhalt wird nicht erneut gespeichert, sondern die
///   vorhandene Buchung unverändert zurückgegeben — auch wenn die
///   beteiligten Konten/Pools inzwischen archiviert wurden (Archivierung
///   verhindert nur neue Referenzen, nicht die Bestätigung historischer);
///   dieselbe Id mit anderem Inhalt ist ein Konflikt;
/// - prüft bei tatsächlicher Neuanlage die Vorgabedaten gegen den
///   Stammdaten-Katalog und speichert Buchung und Postings atomar;
/// - speichert zusammen mit der Buchung die internen Ausgleichseffekte
///   (Rechenvertrag §3.5): ein Anfangsbestand auf einem Konto kann die
///   Schuldzelle (Konto, Pool) verkleinern und so eine offene
///   Ausgleichsposition (teilweise) begleichen — der Journalzustand ist
///   die Wahrheit, die Positionstabelle wird in derselben Transaktion
///   nachgeführt;
/// - erzwingt die Einmaligkeitsregel (Zwischenstep 7.5): pro Konto ist
///   genau ein Anfangsbestand zulässig — der zweite Eintrag desselben
///   Kontos ist ein Datenfehler und wird mit dem fachlichen
///   Korrekturweg abgelehnt (Anfangsbestände sind nicht stornierbar).
///   Zusätzlich sichert ein partieller eindeutiger Index die Regel
///   datenbankseitig atomar.
///
/// Der Anfangsbestand ist keine Einnahme: kein Finanzierungsmonat, keine
/// Zuweisung über Einnahme-Logik (Rechenvertrag §3.7).
/// </summary>
public sealed class OpeningBalanceService(
    IMasterDataRepository masterDataRepository,
    IBookingRepository bookingRepository,
    ISettlementPositionRepository settlementPositionRepository)
{
    /// <summary>Ergebnis einer Anlage: <see cref="Created"/> ist false bei Idempotenz-Treffer.</summary>
    public sealed record Result(bool Created, Booking Booking);

    public async Task<Result> CreateAsync(
        OpeningBalanceSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // Stabile Wiederholungsidentität ist verbindlich: ohne Id kann keine
        // Buchung entstehen — Wiederholungen (z. B. nach verlorener Antwort)
        // wären sonst nicht erkennbar und würden verdoppeln.
        if (string.IsNullOrWhiteSpace(spec.Id))
        {
            throw new Lario.Domain.LarioDomainException(
                "Anfangsbestand ohne Id: eine stabile Wiederholungsidentität ist erforderlich.");
        }

        // Idempotenz vor der Katalog-Validierung (Review, Schritt 3):
        // eine vorhandene Buchung mit dieser Id wird unverändert bestätigt,
        // auch wenn die beteiligten Stammdaten inzwischen archiviert wurden.
        // Die Aktivitätsprüfung gehört ausschließlich zum Neuanlagepfad.
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

        // Tatsächliche Neuanlage: die Vorgabe muss aktive Stammdaten referenzieren.
        var catalog = await masterDataRepository.LoadCatalogAsync(cancellationToken);
        spec.ValidateAgainst(catalog);

        var bookings = await bookingRepository.GetAllAsync(cancellationToken);

        // Einmaligkeitsregel (Zwischenstep 7.5): pro Konto ist genau ein
        // Anfangsbestand zulässig — der zweite Eintrag desselben Kontos
        // ist ein Datenfehler, kein zweiter Ist-Zustand. Sichtbare
        // Ablehnung mit dem Korrekturweg; die Datenbank erzwingt die
        // Regel zusätzlich atomar über einen eindeutigen Index.
        if (bookings.Any(b => b.Type == BookingType.OpeningBalance
                              && b.OpeningBalanceAccountId == spec.AccountId))
        {
            var accountName = catalog.FindAccount(spec.AccountId)?.Name ?? spec.AccountId;
            throw new Lario.Domain.DomainConflictException(
                $"Konto '{spec.AccountId}' ({accountName}) hat bereits einen Anfangsbestand — " +
                "pro Konto ist genau einer zulässig. Ändere den vorhandenen Anfangsbestand " +
                "über die Korrektur (Anfangsbestände sind nicht stornierbar); eine zweite " +
                "Erfassung desselben Kontos ist ausgeschlossen.");
        }

        var persistedPositions = await settlementPositionRepository.GetAllAsync(cancellationToken);

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
