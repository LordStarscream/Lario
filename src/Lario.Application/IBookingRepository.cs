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

    /// <summary>
    /// Fügt eine neue Buchung zusammen mit ihren Ausgleichseffekten UND
    /// der Revisionserhöhung der verknüpften Original-Kette in einer
    /// einzigen Transaktion hinzu (Design Schritt 6 §2.5/§3.2: der
    /// Erfolgsfall eines Stornos ist „Reversal-Buchung + Ketten-
    /// Revision++" atomar). <paramref name="originalChain"/> enthält
    /// zuerst das direkte Original und dann seine Vorfahren bis zur
    /// Wurzel (Storno des Stornos, …); jedes Kettenglied erhält in
    /// derselben Transaktion eine Revisionserhöhung mit eigenem
    /// Concurrency-Token (Review R2 B2).
    /// </summary>
    /// <remarks>
    /// Review R3 P1: Die <c>BaseRevision</c> jedes Kettenglieds ist die
    /// Revision des Lesezustands, gegen den der Anwendungsfall seine
    /// Prüfungen gelaufen hat (direktes Original: die Instanz mit der
    /// von der Vorgabe getragenen Revision; Vorfahren: die Einträge des
    /// geprüften Journals). Der Repository übernimmt diese Revisionen
    /// unverändert als Optimistic-Concurrency-Basis und liest keine
    /// aktuelleren Werte nach — eine parallele Commit zwischen Prüfung
    /// und Schreiben (z. B. Korrektur des Wurzeloriginals oder neues
    /// direktes Storno) verschiebt eine Revision und führt zum
    /// Revisionskonflikt samt vollständigem Rollback, statt dass das
    /// geprüfte Ergebnis auf veraltetem Stand committet würde.
    /// </remarks>
    Task<Booking> AddWithOriginalRevisionAsync(
        Booking booking,
        IReadOnlyList<Booking> originalChain,
        IReadOnlyCollection<SettlementPosition> settlements,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lädt den Korrektur-Historieneintrag (Design Schritt 6 §2.4) nach
    /// seiner Operation-Id; null, wenn unbekannt. Die Operation-Id ist der
    /// Idempotenzschlüssel der Korrektur (Rechenvertrag §5.5).
    /// </summary>
    Task<BookingCorrection?> FindCorrectionAsync(
        string operationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Aktualisiert eine bestehende Buchung In-place (In-place-Korrektur,
    /// Design Schritt 6 §2.5) zusammen mit dem zugehörigen
    /// Historieneintrag in einer einzigen Transaktion:
    /// - die Buchung <paramref name="updated"/> (Id muss existieren) wird
    ///   in ihren skalaren Feldern (Typ bleibt, Datum, Notiz,
    ///   Finanzierungsmonat, Basisrevision, Storno-/Erstattungs-Referenzen)
    ///   aktualisiert und ihre Postings, Teilposten, Storno-Deltas und
    ///   Zweck-/Umwidmungszeilen ersetzt (alte Zeilen werden gelöscht, neue
    ///   wie im <see cref="AddAsync"/>-Muster angelegt);
    /// - <paramref name="correction"/> (Vorher/Nachher-Snapshot) wird in
    ///   der Korrektur-Historie eingetragen;
    /// - <paramref name="settlements"/> enthält die anzulegenden bzw. zu
    ///   aktualisierenden Ausgleichspositionen, wie sie aus dem
    ///   Vorher/Nachher-Vergleich des Journals abgeleitet sind (gleiche
    ///   Semantik wie bei <see cref="AddAsync(Booking,
    ///   System.Collections.Generic.IReadOnlyCollection{Lario.Domain.Journal
    ///   .SettlementPosition}, System.Threading.CancellationToken)"/>);
    /// - ein Teilkommitt ist ausgeschlossen: Buchung, Historie und
    ///   Positionszustände stehen oder fallen gemeinsam (Rechenvertrag
    ///   §5.4: „nie unbemerkt überschrieben").
    /// </summary>
    /// <returns>Die persistierte Buchung, nachgeladen inkl. aller Postings.</returns>
    Task<Booking> UpdateAsync(
        Booking updated,
        BookingCorrection correction,
        IReadOnlyCollection<SettlementPosition> settlements,
        CancellationToken cancellationToken = default);
}
