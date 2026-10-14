using Lario.Domain;
using Lario.Domain.Journal;

namespace Lario.Application.Journal;

/// <summary>
/// Anwendungsfall „Buchung stornieren" (Schritt 6, Rechenvertrag
/// §5.1/§5.4/§5.5, Design Schritt 6 §2.5/§3.2):
/// - ein Storno ist eine neue Buchung vom Typ
///   <see cref="BookingType.Reversal"/> mit negierten Postings und
///   explizit gespeicherten negierten Deltas
///   (<c>ReversalDeltas == Negation(Original.AllocationDelta())</c>, kein
///   Cross-Booking-Lookup im Replay), verknüpft über
///   <c>ReversesBookingId</c> — das Original bleibt im Journal;
/// - Erfolgsfall: Reversal-Buchung UND Original-Revision++ in einer
///   einzigen Transaktion (atomar) — der Revisionszuwachs macht den
///   Doppelstorno-Strukturprüfung möglich: ein zweites Storno mit
///   veralteter Erwartungsrevision schlägt als Revisionskonflikt fehl
///   (Plan-Abnahme B1);
/// - Idempotenz zuerst (vor Katalog- und Revisionsprüfung): gleiche Id
///   mit gleichem Inhalt wird nur quittiert (keine zweite
///   Storno-Buchung, Revision bleibt); gleiche Id mit anderem Inhalt oder
///   anderem Typ ist ein Konflikt;
/// - stornierbare Typen: Ausgabe, Einnahme, Konto-Transfer, Umwidmung,
///   Verbund (Transfer+Umwidmung) und Storno („Storno eines Stornos" =
///   Widerruf); nicht stornierbar: Anfangsbestand und
///   Kontostandskorrektur (Differenzen werden durch eine neue
///   dokumentierte Korrektur ersetzt, nicht storniert);
/// - Revision: <c>ExpectedBaseRevision</c> muss der Ist-Revision des
///   Originals entsprechen, sonst <see cref="DomainConflictException"/>
///   mit erwartetem und Ist-Wert;
/// - Storno nicht möglich, wenn eine offene Begleichungsposition existiert,
///   auf die sich die Buchung bezieht (<c>RelatedSettlementId</c>) — der
///   Storno würde den offenen Bestand auf eine nicht mehr wirkende
///   Buchung zeigen; zuerst muss die Position geschlossen werden;
/// - Begleichungspositionen werden atomar mit dem Storno nachgeführt
///   (Vorher/Nachher-Übergangsanalyse des Journals, Design Schritt 6
///   §3.3): z. B. setzt ein Storno einer beglichenen Ausgabe die
///   Begleichung auf den Journalzustand zurück (Plan-Abnahme B4) — die
///   Journalwahrheit bleibt konsistent, alte Zeilen verschwinden nicht
///   still.
/// </summary>
public sealed class ReversalService(
    IBookingRepository bookingRepository,
    ISettlementPositionRepository settlementPositionRepository)
{
    /// <summary>
    /// Ergebnis eines Stornos: <see cref="Created"/> ist true, wenn das
    /// Storno als neue Buchung angelegt wurde (Original-Revision erhöht);
    /// false bei Idempotenz-Treffer (das vorhandene Storno wird
    /// zurückgegeben, nichts wurde neu geschrieben).
    /// </summary>
    public sealed record Result(bool Created, Booking Booking);

    public async Task<Result> ReverseAsync(
        ReversalSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // Strukturprüfung der Vorgabe (Ids, Selbstverknüpfung, Revision).
        spec.Validate();

        // Idempotenz zuerst: das vorhandene Storno wird zurückgeliefert,
        // bevor Katalog- oder Revisionsprüfung greifen.
        var existing = await bookingRepository.FindByIdAsync(spec.Id, cancellationToken);
        if (existing is not null)
        {
            if (existing.Type != BookingType.Reversal)
            {
                throw new DomainConflictException(
                    $"Buchungs-Id '{spec.Id}' ist bereits vergeben (Typ {existing.Type}) — " +
                    "Storno-Operationen müssen eine freie Id verwenden.");
            }

            if (existing.ReversesBookingId != spec.OriginalBookingId
                || existing.Date != spec.Date
                || existing.Note != spec.Note)
            {
                throw new DomainConflictException(
                    $"Für die Buchung '{spec.Id}' existiert bereits eine Storno-Buchung mit " +
                    "anderem Inhalt. Gleiches Id, anderer Inhalt ist ein Konflikt — die " +
                    "vorhandene Buchung bleibt unverändert.");
            }

            return new Result(false, existing);
        }

        // Original laden und Stornierbarkeit prüfen.
        var original = await bookingRepository.FindByIdAsync(spec.OriginalBookingId, cancellationToken)
            ?? throw new InvalidReferenceException(
                $"Storno '{spec.Id}': das Original '{spec.OriginalBookingId}' existiert nicht.");

        if (!IsReversible(original.Type))
        {
            throw new LarioDomainException(
                $"Storno '{spec.Id}': Buchungen vom Typ {original.Type} sind nicht stornierbar. " +
                "Anfangsbestände und Kontostandskorrekturen ändern Sie über eine neue, " +
                "dokumentierte Buchung — nicht über ein Storno.");
        }

        // Revision des Originals (Rechenvertrag §5.5).
        if (original.BaseRevision != spec.ExpectedBaseRevision)
        {
            throw new DomainConflictException(
                $"Storno '{spec.Id}' der Buchung '{original.Id}': erwartete Basisrevision " +
                $"{spec.ExpectedBaseRevision} weicht von der Ist-Revision {original.BaseRevision} ab " +
                "(Revisionskonflikt). Die Buchung bleibt unverändert.")
            {
                ExpectedBaseRevision = spec.ExpectedBaseRevision,
                ActualBaseRevision = original.BaseRevision,
            };
        }

        var bookings = await bookingRepository.GetAllAsync(cancellationToken);

        // Doppelstorno: das Original darf höchstens ein Storno tragen.
        // Mit veralteter Erwartungsrevision ist dies oben bereits ein
        // Revisionskonflikt; mit aktueller Revision wird der Doppelstorno
        // hier sichtbar abgelehnt (Design Schritt 6 §2.5/§3.2).
        if (bookings.Any(b => b.Type == BookingType.Reversal && b.ReversesBookingId == original.Id))
        {
            throw new DomainConflictException(
                $"Storno '{spec.Id}': die Buchung '{original.Id}' ist bereits storniert — " +
                "ein Doppelstorno ist ausgeschlossen. Widerrufen Sie das bestehende Storno " +
                "erstmals (neues Storno auf die Storno-Buchung), wenn Sie den Storno " +
                "zurücknehmen wollen.");
        }

        // Eine offene Begleichungsposition, auf die sich die Buchung
        // bezieht, blockiert das Storno (Design Schritt 6 §3.2): der
        // Storno würde den offenen Bestand auf eine nicht mehr wirkende
        // Buchung zeigen — die Position muss zuerst geschlossen werden.
        if (original.RelatedSettlementId is { } relatedSettlementId)
        {
            var position = await settlementPositionRepository
                .FindByIdAsync(relatedSettlementId, cancellationToken);
            if (position is not null && position.Status != SettlementStatus.Beglichen)
            {
                throw new DomainConflictException(
                    $"Storno '{spec.Id}': die Buchung '{original.Id}' ist an die offene " +
                    $"Begleichungsposition '{relatedSettlementId}' gebunden — ein Storno würde " +
                    "den offenen Bestand auf eine nicht mehr existente Buchung zeigen. " +
                    "Schließen Sie die Position zuerst (Ausgleich oder Korrektur), dann ist " +
                    "das Storno möglich.");
            }
        }

        var booking = spec.ToBooking(original);

        // Begleichungspositionen atomar mit dem Storno nachführen
        // (Vorher = Journal, Nachher = Journal + Reversal — die gesamte
        // Schritt-5-Übergangsanalyse greift ohne Sonderwege, Design
        // Schritt 6 §3.3).
        var persistedPositions = await settlementPositionRepository.GetAllAsync(cancellationToken);
        var changes = SettlementPositionSync.Plan(
            bookings, booking, persistedPositions,
            () => $"set-{Guid.NewGuid():N}");

        // Atomar: Reversal-Buchung + Original-Revision++ (eine
        // Transaktion).
        var stored = await bookingRepository.AddWithOriginalRevisionAsync(
            booking, original, changes.Select(c => c.Row).ToList(), cancellationToken);
        return new Result(true, stored);
    }

    /// <summary>
    /// Stornierbare Buchungs-Typen (Design Schritt 6 §3.2): alle Typen
    /// mit echter Konto- oder Pool-Auswirkung — Ausgabe, Einnahme,
    /// Konto-Transfer, Umwidmung, Verbund (Transfer+Umwidmung) und Storno
    /// selbst („Storno eines Stornos" = revisionsgerechter Widerruf).
    /// Nicht stornierbar: Anfangsbestand (er wird nicht rückgängig, sondern
    /// korrigiert) und Kontostandskorrektur (Differenz wird durch eine
    /// neue dokumentierte Korrektur ersetzt, nicht storniert); Erstattungen
    /// sind in Schritt 6 nicht stornierbar (sie sind vom Storno getrennt —
    /// B2).
    /// </summary>
    private static bool IsReversible(BookingType type) => type switch
    {
        BookingType.Expense => true,
        BookingType.Income => true,
        BookingType.AccountTransfer => true,
        BookingType.Reassignment => true,
        BookingType.TransferReassignment => true,
        BookingType.Reversal => true,
        _ => false,
    };
}
