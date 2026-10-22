using Lario.Domain.Attachments;

namespace Lario.Application.Attachments;

/// <summary>
/// Persistenz der Beleg-Metadaten (Schritt 7a, Design D1). Die Datei
/// selbst liegt im Ablageverzeichnis (<see cref="IAttachmentFileStore"/>);
/// hier nur die Metadatenzeile.
/// </summary>
public interface IAttachmentRepository
{
    /// <summary>
    /// Legt die Metadatenzeile an. Fremdschlüssel-Verletzungen
    /// (unbekannte Buchung bzw. Split-Komposit) werfen — die
    /// Anwendungsfälle prüfen referenzierte Entitäten vorher fachlich
    /// (400), die Datenbank ist die letzte Verteidigungslinie.
    /// </summary>
    Task AddAsync(Attachment attachment, CancellationToken cancellationToken = default);

    /// <summary>Lädt eine Zeile; null wenn unbekannt.</summary>
    Task<Attachment?> FindByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Alle Belege einer Buchung (Anzeige in der Buchungsliste,
    /// Design D6) — in Anlage-Reihenfolge (Created, dann Id als
    /// Tie-Breaker bei gleichem Millisekunden-Zeitstempel).
    /// </summary>
    Task<IReadOnlyList<Attachment>> GetByBookingAsync(string bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Alle Metadatenzeilen — für den Start-Abgleich (Design D9b:
    /// Metadaten ohne Datei sind zulässig, Datei ohne Metadaten nicht).
    /// </summary>
    Task<IReadOnlyList<Attachment>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Entfernt die Metadatenzeile (Design D5: Metadaten zuerst,
    /// dann Datei; die Buchung bleibt unverändert).
    /// </summary>
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Setzt die Teilposten-Zuordnung (Umlageung bei Korrektur,
    /// Design D1b: Split entfällt → <c>splitId = null</c>, die alte Id
    /// wandert nach <c>originalSplitId</c>).
    /// </summary>
    Task UpdateSplitReferenceAsync(
        string id, string? splitId, string? originalSplitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Setzt die Teilposten-Zuordnung eines vorhandenen Belegs (Nachtrag
    /// N1: manuelle Umzuordnung) und den Änderungszeitstempel.
    /// <c>originalSplitId</c> bleibt unberührt — es dokumentiert
    /// ausschließlich die systemseitige Umlageung (Design D1b).
    /// </summary>
    Task UpdateSplitAssignmentAsync(
        string id, string? splitId, DateTime updatedAtUtc, CancellationToken cancellationToken = default);
}
