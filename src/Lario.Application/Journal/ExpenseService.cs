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
    /// hat: Fachdatum, Konto, Gesamtbetrag, Notiz und Teilposten als
    /// 1:1-Abbildung nach Teilposten-Id (eindeutige Ids, Betrag, Pool,
    /// Kategorie und Notiz müssen exakt stimmen). Die Steuerkennzeichnung
    /// wird mit Herkunft verglichen (<see cref="TaxMatches"/>):
    /// - gespeicherte Kennzeichnung fehlt → nur eine Wiederholung ohne
    ///   Kennzeichnung bestätigt (beim Original gab es weder explizite
    ///   Angabe noch wirksame Voreinstellung);
    /// - gespeicherte Kennzeichnung aus Voreinstellung → nur eine
    ///   Wiederholung ohne Kennzeichnung bestätigt (die Original-Eingabe
    ///   trug keine Kennzeichnung; die Voreinstellung kann sich zwischen
    ///   Original und Wiederholung geändert oder entfernt haben —
    ///   Rechenvertrag: die Rechnung hängt nur von den gespeicherten
    ///   Eingabedaten ab);
    /// - gespeicherte explizite Kennzeichnung → die Wiederholung muss
    ///   eine Kennzeichnung mit exakt gleichen Komponenten tragen.
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

        // Die eingehenden Split-Ids müssen eindeutig sein: mit Duplikaten
        // (z. B. [sp-1, sp-1] statt [sp-1, sp-2]) ist die Vorgabe kein
        // 1:1-Abbild des Originals — auch wenn jede Zeile einzeln gegen
        // dieselbe gespeicherte Zeile passen würde.
        var seenSplitIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var split in spec.Splits)
        {
            if (!seenSplitIds.Add(split.Id))
            {
                return false;
            }

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

            if (!TaxMatches(split.Tax, stored.Tax))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Vergleicht die Kennzeichnung der Wiederholung (<c>expected</c>;
    /// null = keine explizite Angabe) mit der gespeicherten
    /// Kennzeichnung, einschließlich deren Herkunft:
    /// gespeicherte Kennzeichnung fehlt oder stammt aus einer
    /// Voreinstellung → nur „keine Angabe“ bestätigt; gespeicherte
    /// explizite Kennzeichnung → exakt gleiche Komponenten erforderlich.
    /// </summary>
    private static bool TaxMatches(TaxMarking? expected, TaxMarking? stored)
    {
        if (stored is null)
        {
            return expected is null;
        }

        if (stored.InheritedFromPreset)
        {
            // Die Original-Eingabe trug keine Kennzeichnung (Snapshot der
            // damals wirksamen Voreinstellung): nur eine Wiederholung ohne
            // Kennzeichnung bestätigt — eine explizite Kennzeichnung ist
            // ein Inhaltswechsel, auch wenn ihre Werte der gespeicherten
            // entsprechen.
            return expected is null;
        }

        return expected is not null
            && expected.Relevant == stored.Relevant
            && expected.TaxCategoryId == stored.TaxCategoryId
            && expected.MarkedAmount == stored.MarkedAmount
            && expected.Note == stored.Note;
    }
}
