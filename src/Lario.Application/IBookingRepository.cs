using Lario.Domain.Journal;

namespace Lario.Application;

/// <summary>
/// Gezielte Schnittstelle für das Buchungsjournal (Schritt 3).
/// <see cref="AddAsync"/> speichert Buchung und alle zusammengehörigen
/// Postings atomar — eine Teilübernahme ist unmöglich (Rechenvertrag §3.2).
/// </summary>
public interface IBookingRepository
{
    /// <summary>Lädt eine Buchung inkl. aller Postings; null, wenn unbekannt.</summary>
    Task<Booking?> FindByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Lädt alle Buchungen inkl. aller Postings (Snapshot für Saldenabfragen).</summary>
    Task<IReadOnlyList<Booking>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fügt eine neue Buchung mit allen Postings in einer Transaktion hinzu.
    /// Existiert die Id bereits, ist das ein Fehler (Idempotenzprüfung
    /// obliegt dem Aufrufer, siehe <c>OpeningBalanceService</c>).
    /// </summary>
    Task AddAsync(Booking booking, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fügt eine neue Buchung zusammen mit ihren internen
    /// Ausgleichseffekten (Rechenvertrag §3.2/§3.5) in einer einzigen
    /// Transaktion hinzu: Buchung, Postings, Teilposten und
    /// Positionszustände werden gemeinsam committet — ein Teilkommitt ist
    /// ausgeschlossen.
    /// <paramref name="settlements"/> enthält die anzulegenden (Id noch
    /// nicht gespeichert) bzw. zu aktualisierenden (Id bereits gespeichert)
    /// Positionen, wie sie aus dem Vorher/Nachher-Vergleich des Journals
    /// abgeleitet sind; leere Sammlung, wenn die Buchung keine Effekte hat.
    /// </summary>
    Task AddAsync(
        Booking booking,
        IReadOnlyCollection<SettlementPosition> settlements,
        CancellationToken cancellationToken = default);
}
