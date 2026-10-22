namespace Lario.Domain.Attachments;

/// <summary>
/// Beleg (PDF/Bild) mit Metadaten und Zuordnung zu einer Buchung
/// (Schritt 7a, Design D1): der Beleg nachweist eine Ausgabe —
/// optional nur einen ihrer Teilposten (Split), sonst die gesamte
/// Ausgabe. Die Datei selbst liegt unter einem serverseitig erzeugten
/// Namen im Ablageverzeichnis (Design D2); hier werden nur die
/// Metadaten persistiert.
///
/// Begriffsregel: Anhänge sind keine Finanzbuchung — sie verändern
/// keinen Kontostand, keinen Pool und keine Revision. Das Entfernen
/// einer Zuordnung (Zeile + Datei) berührt die Buchung nicht
/// (Design D5); die Buchungsreferenz bleibt über Restrict erhalten.
/// </summary>
public sealed class Attachment
{
    /// <summary>Stabile Identität (serverseitig erzeugte GUID, ohne Bindestriche).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Vom Client übergebene Dateibezeichnung — reine Anzeige. Aus ihr
    /// wird niemals ein Dateipfad abgeleitet (keine Path-Traversal,
    /// Design D2).
    /// </summary>
    public string OriginalFilename { get; init; } = string.Empty;

    /// <summary>
    /// Gültiger MIME-Typ: <c>application/pdf</c>, <c>image/jpeg</c>,
    /// <c>image/png</c> — deklariert vom Client und gegen die
    /// Magic-Bytes der Datei geprüft (Design D3).
    /// </summary>
    public string ContentType { get; init; } = string.Empty;

    /// <summary>Größe der Datei in Byte (exakter Inhalt, ohne Multipart-Overhead).</summary>
    public long SizeBytes { get; init; }

    /// <summary>SHA-256-Prüfsumme der Datei (hex, 64 Zeichen).</summary>
    public string Sha256 { get; init; } = string.Empty;

    /// <summary>
    /// Serverseitig erzeugter Dateiname im Ablageverzeichnis
    /// (<c>{Id}{.pdf|.jpg|.png}</c>, Design D2) — die einzige Verbindung
    /// zwischen Metadatenzeile und Datei auf dem Datenträger.
    /// </summary>
    public string StoredName { get; init; } = string.Empty;

    /// <summary>
    /// Zugehörige Buchung (erforderlich, Design D1). Die Buchung ist
    /// immer Teil der Zuordnung — ohne Buchung existiert kein Beleg.
    /// </summary>
    public string BookingId { get; init; } = string.Empty;

    /// <summary>
    /// Optional belegter Teilposten (Komposit-Fremdschlüssel
    /// (BookingId, SplitId) auf ExpenseSplits — die Teilposten-Id ist
    /// nur innerhalb der Buchung eindeutig). null = der Beleg nachweist
    /// die gesamte Ausgabe einschließlich aller Teilposten.
    /// Setbar, damit das Repository den Umlageungsfall der Korrektur
    /// pflegen kann (Design D1b: Split entfällt → null, die alte Id
    /// wandert nach <see cref="OriginalSplitId"/>).
    /// </summary>
    public string? SplitId { get; set; }

    /// <summary>
    /// Ursprünglich belegter Teilposten — nur gesetzt, wenn eine
    /// Korrektur den Split entfernt und den Beleg auf die Gesamtbuchung
    /// umgehängt hat (Design D1b; Nachvollziehbarkeit). Bewusst ohne
    /// Fremdschlüssel: die Zeile darf den gelöschten Split referenzieren,
    /// sonst würde der FK die Nachvollziehbarkeit blockieren.
    /// </summary>
    public string? OriginalSplitId { get; set; }

    /// <summary>Technischer UTC-Zeitpunkt der Ablage (Fachdatum der Buchung bleibt unberührt).</summary>
    public DateTime CreatedAtUtc { get; init; }
}
