using Lario.Domain;
using Lario.Domain.Journal;
using System.Text.Json;

namespace Lario.Application.Journal;

/// <summary>
/// Anwendungsfall „Buchung korrigieren" (Schritt 6, Rechenvertrag
/// §5.2/§5.5, Design Schritt 6 §2.4/§2.5/§3.2):
/// - Korrektur = In-place-Änderung mit Basisrevision++: die Buchung
///   behält ihre Id, ihr Typ und ihr Datum, der neue Inhalt ersetzt den
///   alten — die alte und neue Fassung werden als Vorher/Nachher-Snapshot
///   (JSON: Datum, Konto, Betrag, Notiz, Split/Verteilung) in der
///   Korrektur-Historie gespeichert (Rechenvertrag §5.2, §5.5);
/// - Typen: nur Ausgaben und Einnahmen sind korrigierbar (andere Typen:
///   klare Ablehnung, Design Schritt 6 §3.2);
/// - Revision: <c>ExpectedBaseRevision</c> muss der Ist-Revision
///   entsprechen, sonst <see cref="DomainConflictException"/> mit
///   erwartetem und Ist-Wert (Rechenvertrag §5.5) — derselbe Mechanismus
///   wie beim Storno (Storno erhöht die Revision, veraltete Korrekturen
///   werden sichtbar);
/// - der neue Inhalt wird wie eine Neuanlage validiert (Katalog,
///   Split-/Zuordnungssummen) — der alte Inhalt ist irrelevant
///   (Design Schritt 6 §2.4);
/// - Doppelstorno: keine Korrektur, wenn das Original bereits storniert
///   ist (sichtbare Ablehnung, Design Schritt 6 §3.2);
/// - Begleichungspositionen werden atomar mit der Korrektur
///   nachgeführt (Vorher/Nachher-Übergangsanalyse des Journals, Design
///   Schritt 6 §3.3): z. B. ändert die Korrektur des Split-Betrags die
///   offene Begleichung (Plan-Abnahme B3);
/// - Idempotenz zuerst (vor Revisions- und Katalogprüfung): die
///   Operation-Id ist bereits in der Historie — gleicher Inhalt wird nur
///   quittiert (keine zweite Historie, Revision bleibt), gleicher
///   Operation-Id mit anderem Inhalt ist ein Konflikt (Rechenvertrag
///   §5.5).
/// </summary>
public sealed class CorrectionService(
    IBookingRepository bookingRepository,
    ISettlementPositionRepository settlementPositionRepository,
    IMasterDataRepository masterDataRepository)
{
    /// <summary>
    /// Ergebnis einer Korrektur: <see cref="Created"/> ist true, wenn die
    /// Korrektur angewendet wurde (Revision erhöht, Historie
    /// gespeichert); false bei Idempotenz-Treffer (die unveränderte
    /// Buchung wird zurückgegeben).
    /// </summary>
    public sealed record Result(bool Created, Booking Booking);

    public async Task<Result> CorrectAsync(
        CorrectionSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // Original laden.
        var original = await bookingRepository.FindByIdAsync(spec.BookingId, cancellationToken)
            ?? throw new InvalidReferenceException(
                $"Korrektur der Buchung '{spec.BookingId}': die Buchung existiert nicht.");

        // Idempotenz zuerst (vor Revisions- und Katalogprüfung): die
        // Operation-Id ist der Idempotenzschlüssel der Korrektur-Historie
        // (Rechenvertrag §5.5).
        var existingCorrection = await bookingRepository
            .FindCorrectionAsync(spec.OperationId, cancellationToken);
        if (existingCorrection is not null)
        {
            if (existingCorrection.BookingId != spec.BookingId)
            {
                throw new DomainConflictException(
                    $"Korrektur-Operation '{spec.OperationId}': diese Operation ist bereits " +
                    $"für die Buchung '{existingCorrection.BookingId}' angelegt — die " +
                    "Operation-Id ist eindeutig pro Buchung.");
            }

            if (SpecMatchesBooking(original, spec))
            {
                return new Result(false, original);
            }

            throw new DomainConflictException(
                $"Korrektur-Operation '{spec.OperationId}': diese Operation ist bereits " +
                "angelegt — aber die Buchung trägt heute anderen Inhalt. Gleiche " +
                "Operation-Id, anderer Inhalt ist ein Konflikt; die Buchung bleibt " +
                "unverändert.");
        }

        // Revision (Rechenvertrag §5.5).
        if (original.BaseRevision != spec.ExpectedBaseRevision)
        {
            throw new DomainConflictException(
                $"Korrektur der Buchung '{original.Id}': erwartete Basisrevision " +
                $"{spec.ExpectedBaseRevision} weicht von der Ist-Revision {original.BaseRevision} ab " +
                "(Revisionskonflikt). Die Buchung bleibt unverändert.")
            {
                ExpectedBaseRevision = spec.ExpectedBaseRevision,
                ActualBaseRevision = original.BaseRevision,
            };
        }

        // Neue Fassung wie Neuanlage validieren (Katalog, Typ,
        // Split-/Zuordnungssummen) — Design Schritt 6 §2.4.
        var catalog = await masterDataRepository.LoadCatalogAsync(cancellationToken);
        spec.Validate(catalog, original);

        // Doppelstorno: keine Korrektur auf ein bereits storniertes
        // Original (sichtbare Ablehnung, Design Schritt 6 §3.2).
        var journal = await bookingRepository.GetAllAsync(cancellationToken);
        if (journal.Any(b => b.Type == BookingType.Reversal && b.ReversesBookingId == original.Id))
        {
            throw new DomainConflictException(
                $"Korrektur der Buchung '{original.Id}': die Buchung ist bereits storniert " +
                "— Korrekturen auf stornierten Buchungen sind ausgeschlossen. Widerrufen " +
                "Sie das Storno zuerst (Storno des Stornos), dann ist die Korrektur möglich.");
        }

        // Vorher/Nachher-Snapshot (Rechenvertrag §5.2: JSON mit Datum,
        // Konto, Betrag, Notiz, Split/Verteilung).
        var now = DateTime.UtcNow;
        var correction = new BookingCorrection(
            spec.OperationId,
            original.Id,
            JsonSerializer.Serialize(SnapshotOf(original)),
            JsonSerializer.Serialize(SnapshotOf(spec.BuildUpdatedBooking(original, catalog))),
            spec.Note,
            now);

        // Neue Fassung bauen (dieselbe Id, Typ und Datum; Revision wird
        // hier erhöht — die Historie trägt die alte).
        var updated = spec.BuildUpdatedBooking(original, catalog);
        updated.BaseRevision = original.BaseRevision + 1;

        // Begleichungspositionen atomar mit der Korrektur nachführen
        // (Vorher = Journal mit alter Fassung, Nachher = Journal mit
        // neuer Fassung — dieselbe Übergangsanalyse wie bei der
        // Neuanlage, Design Schritt 6 §3.3).
        var persistedPositions = await settlementPositionRepository.GetAllAsync(cancellationToken);
        var changes = SettlementPositionSync.Plan(
            journal, original, updated, persistedPositions,
            () => $"set-{Guid.NewGuid():N}");

        // Atomar: Inhalt + Revision++ + Historie + Positionseffekte in
        // einer Transaktion (IBookingRepository.UpdateAsync).
        var stored = await bookingRepository.UpdateAsync(
            updated, correction, changes.Select(c => c.Row).ToList(), cancellationToken);
        return new Result(true, stored);
    }

    /// <summary>
    /// Idempotenz-Inhaltsabgleich: die aktuelle Buchung muss zum
    /// spezifizierten Korrekturinhalt passen (alle gesetzten Felder der
    /// Spezifikation sind in der Buchung vorhanden; Felder ohne
    /// Vorgabe sind irrelevant für den Abgleich).
    /// </summary>
    private static bool SpecMatchesBooking(Booking booking, CorrectionSpec spec)
    {
        if (spec.Date is { } date && booking.Date != date)
        {
            return false;
        }

        if (spec.Note is { } note && booking.Note != note)
        {
            return false;
        }

        if (spec.AccountId is { } accountId)
        {
            if (!booking.AccountPostings.Any(p => p.AccountId == accountId))
            {
                return false;
            }
        }

        if (spec.TotalAmount is { } total)
        {
            var accountAmount = booking.AccountPostings
                .Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));
            var expected = booking.Type == BookingType.Income ? total : total.Negate();
            if (accountAmount != expected)
            {
                return false;
            }
        }

        if (spec.Splits is { } splits)
        {
            var storedSplits = booking.Splits
                .OrderBy(s => s.Id, StringComparer.Ordinal)
                .Select(s => (s.Id, s.PoolId, s.Amount.Cents))
                .ToList();
            var expectedSplits = splits
                .OrderBy(s => s.Id, StringComparer.Ordinal)
                .Select(s => (s.Id, s.PoolId, s.Amount.Cents))
                .ToList();
            if (!storedSplits.SequenceEqual(expectedSplits))
            {
                return false;
            }
        }

        if (spec.Allocations is { } allocations)
        {
            var storedAllocations = booking.PoolPostings
                .OrderBy(p => p.PoolId, StringComparer.Ordinal)
                .Select(p => (p.PoolId, p.Amount.Cents))
                .ToList();
            var expectedAllocations = allocations
                .OrderBy(a => a.PoolId, StringComparer.Ordinal)
                .Select(a => (a.PoolId, a.Amount.Cents))
                .ToList();
            if (!storedAllocations.SequenceEqual(expectedAllocations))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Snapshot einer Buchungs-Fassung für die Korrektur-Historie
    /// (Rechenvertrag §5.2): Datum, Konto, Betrag, Notiz,
    /// Split/Verteilung.
    /// </summary>
    private static object SnapshotOf(Booking booking) => new
    {
        Date = booking.Date,
        Accounts = booking.AccountPostings
            .Select(p => new { p.AccountId, Amount = p.Amount.ToEuro() })
            .ToList(),
        Amount = booking.Type == BookingType.Income
            ? booking.AccountPostings.Aggregate(Money.Zero, (s, p) => s.Add(p.Amount)).ToEuro()
            : booking.AccountPostings.Aggregate(Money.Zero, (s, p) => s.Add(p.Amount)).Negate().ToEuro(),
        Note = booking.Note,
        Splits = booking.Splits
            .Select(s => new { s.Id, s.PoolId, s.CategoryId, s.Note, Amount = s.Amount.ToEuro() })
            .ToList(),
        PoolPostings = booking.PoolPostings
            .Select(p => new { p.PoolId, Amount = p.Amount.ToEuro() })
            .ToList(),
    };
}
