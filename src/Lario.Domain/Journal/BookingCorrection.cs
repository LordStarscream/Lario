namespace Lario.Domain.Journal;

/// <summary>
/// Eintrag der Korrektur-Historie (Design Schritt 6 §2.4): dokumentiert eine
/// In-place-Änderung einer Buchung als Vorher/Nachher-Snapshot, damit keine
/// Revision unbemerkt überschrieben wird. Die Historie wird atomar mit der
/// Änderung geschrieben; <see cref="OperationId"/> ist der
/// Idempotenzschlüssel (Rechenvertrag §5.5: gleiche OperationId + gleicher
/// Inhalt wird nur quittiert, gleiche OperationId + anderer Inhalt ist ein
/// Konflikt).
/// </summary>
public sealed class BookingCorrection
{
    /// <summary>
    /// Initialer, leerer Konstrutor (EF-Materialisierung: EF Core setzt die
    /// Eigenschaften erst nach der Konstruktion).
    /// </summary>
    public BookingCorrection()
    {
    }

    /// <summary>
    /// Erzeugt einen Historieneintrag. OperationId, BookingId und beide
    /// Snapshots müssen gesetzt sein — ein Eintrag ohne Vorher/Nachher-Zustand
    /// ist fachlich wertlos.
    /// </summary>
    /// <exception cref="LarioDomainException">
    /// Wenn eine der Pflichtangaben leer oder leer nach Trim ist.
    /// </exception>
    public BookingCorrection(
        string operationId,
        string bookingId,
        string oldSnapshot,
        string newSnapshot,
        string? note,
        DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new LarioDomainException("Korrektur-Historieneintrag ohne OperationId.");
        }

        if (string.IsNullOrWhiteSpace(bookingId))
        {
            throw new LarioDomainException(
                $"Korrektur-Historieneintrag '{operationId}' ohne Buchung-Id.");
        }

        if (string.IsNullOrWhiteSpace(oldSnapshot))
        {
            throw new LarioDomainException(
                $"Korrektur-Historieneintrag '{operationId}' ohne Vorher-Snapshot.");
        }

        if (string.IsNullOrWhiteSpace(newSnapshot))
        {
            throw new LarioDomainException(
                $"Korrektur-Historieneintrag '{operationId}' ohne Nachher-Snapshot.");
        }

        OperationId = operationId;
        BookingId = bookingId;
        OldSnapshot = oldSnapshot;
        NewSnapshot = newSnapshot;
        Note = note;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// OperationId des Korrekturvorgangs (Primärschlüssel, Idempotenzschlüssel
    /// nach Rechenvertrag §5.5).
    /// </summary>
    public string OperationId { get; set; } = string.Empty;

    /// <summary>Stabile Id der geänderten Buchung.</summary>
    public string BookingId { get; set; } = string.Empty;

    /// <summary>
    /// Vorher-Zustand als JSON (Datum, Konto, Betrag, Notiz,
    /// Split/Verteilung) — die Version vor der Korrektur.
    /// </summary>
    public string OldSnapshot { get; set; } = string.Empty;

    /// <summary>
    /// Nachher-Zustand als JSON (gleiche Struktur wie
    /// <see cref="OldSnapshot"/>) — der neue Inhalt nach der Korrektur.
    /// </summary>
    public string NewSnapshot { get; set; } = string.Empty;

    /// <summary>Begründung der Korrektur, optional.</summary>
    public string? Note { get; set; }

    /// <summary>Zeitpunkt der Korrektur (UTC, ohne Zeitzone-Annahme).</summary>
    public DateTime CreatedAtUtc { get; set; }
}
