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
/// - Typen: nur Ausgaben, Einnahmen und Anfangsbestände sind korrigierbar
///   (andere Typen: klare Ablehnung, Design Schritt 6 §3.2; Anfangsbestand
///   ab Zwischenstep 7.5);
/// - Einmaligkeitsregel des Anfangsbestands (Zwischenstep 7.5): ein
///   Konto-Wechsel darf nicht auf ein Konto zielen, das bereits einen
///   eigenen Anfangsbestand trägt (sichtbare Ablehnung);
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
        // (Rechenvertrag §5.5). Abgeglichen wird der kanonische
        // Original-Payload — Revision, Feldpräsenz/null-Semantik und
        // Split-/Steuerinhalt — NICHT der aktuelle Buchungszustand, denn
        // die Wiederholung muss auch dann quittiert werden, wenn die
        // Buchung zwischenzeitlich weiter korrigiert wurde (Review R1 P2).
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

            if (existingCorrection.Payload == JsonSerializer.Serialize(spec))
            {
                return new Result(false, original);
            }

            throw new DomainConflictException(
                $"Korrektur-Operation '{spec.OperationId}': diese Operation ist bereits " +
                "angelegt, aber die eingereichte Vorgabe weicht vom ursprünglichen " +
                "Payload ab (Revision, Feldpräsenz oder Split-/Steuerinhalt). Gleiche " +
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

        // Doppelstorno: keine Korrektur auf ein wirksam storniertes
        // Original (sichtbare Ablehnung, Design Schritt 6 §3.2). Geprüft
        // wird der wirksame Status über die Storno-Kette: ein widerrufenes
        // Storno (Storno des Stornos) hat die Buchung wieder wirksam
        // gemacht — dann ist die Korrektur zulässig (Review R1 P2).
        var journal = await bookingRepository.GetAllAsync(cancellationToken);
        if (new BookingJournal(journal).IsEffectivelyReversed(original.Id))
        {
            throw new DomainConflictException(
                $"Korrektur der Buchung '{original.Id}': die Buchung ist storniert " +
                "— Korrekturen auf stornierten Buchungen sind ausgeschlossen. Widerrufen " +
                "Sie das Storno zuerst (Storno des Stornos), dann ist die Korrektur möglich.");
        }

        // Einmaligkeitsregel des Anfangsbestands (Zwischenstep 7.5): ein
        // Konto-Wechsel darf nicht auf ein Konto zielen, das bereits einen
        // eigenen Anfangsbestand trägt — pro Konto ist genau einer
        // zulässig. Die Prüfung ist posting-basiert, damit auch
        // Bestandszeilen mit NULL-Spalte (Legacy-Duplikate, die der
        // Backfill bewusst unangetastet lässt) den Zielkonto sperren.
        if (original.Type == BookingType.OpeningBalance
            && spec.AccountId is { } newAccountId
            && newAccountId != original.OpeningBalanceAccountId)
        {
            var occupied = journal.FirstOrDefault(b =>
                b.Type == BookingType.OpeningBalance
                && b.Id != original.Id
                && b.AccountPostings.Any(p => p.AccountId == newAccountId));
            if (occupied is not null)
            {
                throw new DomainConflictException(
                    $"Korrektur der Buchung '{original.Id}': das Konto '{newAccountId}' hat " +
                    $"bereits den Anfangsbestand '{occupied.Id}' — pro Konto ist genau ein " +
                    "Anfangsbestand zulässig. Die Korrektur bleibt abgelehnt.");
            }
        }

        // Neue Fassung bauen (dieselbe Id, Typ und Datum; Revision wird
        // hier erhöht — die Historie trägt die alte).
        var updated = spec.BuildUpdatedBooking(original, catalog);
        updated.BaseRevision = original.BaseRevision + 1;

        // Bereits gewährte Erstattungen müssen auch nach der Korrektur
        // durch die Split-Positionen gedeckt bleiben (Review R1 P2,
        // Rechenvertrag §5.3): eine Korrektur, die einen erstatteten
        // Ursprungspool entfernt oder seine Split-Position unter den
        // bereits erstatteten Betrag senkt, ist ein Konflikt — sichtbar
        // abgelehnt, nichts wird persistiert. Die Sicherheit gegen
        // parallele Erstattungen garantiert der Concurrency-Token auf
        // der Original-Revision (Review R1 P1).
        CheckRefundCoverage(updated, journal);

        // Vorher/Nachher-Snapshot (Rechenvertrag §5.2: JSON mit Datum,
        // Konto, Betrag, Notiz, Split/Verteilung, Steuerkennzeichnung).
        var now = DateTime.UtcNow;
        var correction = new BookingCorrection(
            spec.OperationId,
            original.Id,
            JsonSerializer.Serialize(SnapshotOf(original)),
            JsonSerializer.Serialize(SnapshotOf(updated)),
            spec.Note,
            JsonSerializer.Serialize(spec),
            now);

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
    /// Prüft, dass bereits gewährte Erstattungen der Ausgabe durch die
    /// Split-Positionen der korrigierten Fassung gedeckt bleiben (Review
    /// R1 P2, Rechenvertrag §5.3): je Ursprungspool darf die Summe der
    /// bereits erstatteten Beträge die korrigierte Split-Position nicht
    /// überschreiten; ein erstatteter Pool, der in der korrigierten
    /// Fassung nicht mehr vorkommt, ist immer ein Konflikt.
    /// </summary>
    private static void CheckRefundCoverage(Booking updated, IReadOnlyList<Booking> journal)
    {
        var refunds = journal
            .Where(b => b.Type == BookingType.Refund && b.RefundsExpenseId == updated.Id)
            .SelectMany(b => b.PoolPostings)
            .ToList();

        if (refunds.Count == 0)
        {
            return;
        }

        // Ausgaben tragen negative Pool-Postings; die Split-Position ist
        // der Betrag, den die Ausgabe aus dem Pool gezahlt hat.
        var correctedSplits = updated.PoolPostings
            .ToDictionary(p => p.PoolId, p => p.Amount.Negate(), StringComparer.Ordinal);

        foreach (var group in refunds.GroupBy(p => p.PoolId, StringComparer.Ordinal))
        {
            var totalRefunded = group
                .Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));

            if (!correctedSplits.TryGetValue(group.Key, out var allowed))
            {
                throw new DomainConflictException(
                    $"Korrektur der Buchung '{updated.Id}': der Pool '{group.Key}' ist in der " +
                    $"korrigierten Fassung kein Ursprungspool mehr, aber es wurden bereits " +
                    $"{totalRefunded.ToEuro():0.00} € aus diesem Pool erstattet. Bereits " +
                    "gewährte Erstattungen müssen durch die korrigierten Splits gedeckt " +
                    "bleiben (Rechenvertrag §5.3). Die Korrektur bleibt abgelehnt.");
            }

            if (totalRefunded > allowed)
            {
                throw new DomainConflictException(
                    $"Korrektur der Buchung '{updated.Id}': die korrigierte Split-Position des " +
                    $"Pools '{group.Key}' ({allowed.ToEuro():0.00} €) ist kleiner als die " +
                    $"bereits erstatteten {totalRefunded.ToEuro():0.00} €. Bereits gewährte " +
                    "Erstattungen müssen durch die korrigierten Splits gedeckt bleiben " +
                    "(Rechenvertrag §5.3). Die Korrektur bleibt abgelehnt.");
            }
        }
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
            .Select(s => new
            {
                s.Id,
                s.PoolId,
                s.CategoryId,
                s.Note,
                Amount = s.Amount.ToEuro(),
                // Steuerkennzeichnung vollständig mitspeichern (Review
                // R1 P2): auch eine rein steuerliche Korrektur muss ein
                // anderes Vorher-/Nachher-Ergebnis liefern.
                Tax = s.Tax is { } tax
                    ? new
                    {
                        tax.Relevant,
                        tax.TaxCategoryId,
                        MarkedAmount = tax.MarkedAmount?.ToEuro(),
                        tax.Note,
                        tax.InheritedFromPreset
                    }
                    : null
            })
            .ToList(),
        PoolPostings = booking.PoolPostings
            .Select(p => new { p.PoolId, Amount = p.Amount.ToEuro() })
            .ToList(),
    };
}
