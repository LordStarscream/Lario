using Lario.Application;
using Lario.Domain.Journal;

namespace Lario.Application.Journal;

/// <summary>
/// Upgrade-Backfill für interne Ausgleichspositionen (Schritt 5,
/// Review-P2d): Datenbanken aus Zeiten vor Schritt 5 (z. B. nur bis
/// <c>SplitMarkingProvenance</c> migriert) können legitime offene
/// Ausgleichspositionen tragen — eine negative Konto-Pool-Zuordnung ohne
/// persistierte Zeile. Der Backfill materialisiert genau diese
/// fehlenden offenen Positionen, atomar und idempotent (ein zweiter
/// Lauf legt nichts mehr an), und läuft beim Host-Start nach der
/// Migration — vor der ersten Abfrage. Danach kann
/// <see cref="SettlementQueryService"/> auf die Invariante „offene
/// Episode hat eine Zeile“ vertrauen.
///
/// Beglichene Episoden ohne Zeile bleiben unsichtbar (Historie vor der
/// Positionsverwaltung). Zeilen werden nur für Zellen angelegt, die
/// gar keine persistierte Zeile haben: die offene Episode ist immer die
/// jüngste Episode einer Zelle und damit nur ungepaart, wenn es keine
/// Zeile gibt (Tail-Pairing, siehe <see cref="SettlementPositionSync"/>).
/// </summary>
public sealed class SettlementPositionBackfillService(
    IBookingRepository bookingRepository,
    ISettlementPositionRepository settlementPositionRepository)
{
    /// <summary>
    /// Führt den Backfill aus und liefert die Anzahl der angelegten
    /// Zeilen (0, wenn die Datenbank bereits konsistent ist).
    /// </summary>
    public async Task<int> EnsureConsistentAsync(CancellationToken cancellationToken = default)
    {
        var bookings = await bookingRepository.GetAllAsync(cancellationToken);
        var persisted = await settlementPositionRepository.GetAllAsync(cancellationToken);

        var episodes = new BookingJournal(bookings).DeriveSettlementPositions();

        var cellsWithRows = persisted
            .Select(p => (p.SourceAccountId, p.TargetPoolId))
            .ToHashSet();

        // Offene Episoden in Zellen ohne jede persistierte Zeile — das
        // sind die fehlenden legitimen Positionen (z. B. unterdeckte
        // Ausgabe einer Ausgabe-Einnahme-Datenbank vor Schritt 5).
        var missing = episodes
            .Where(e => e.Status == SettlementStatus.Offen)
            .Where(e => !cellsWithRows.Contains((e.SourceAccountId, e.TargetPoolId)))
            .ToList();

        if (missing.Count == 0)
        {
            return 0;
        }

        var rows = missing
            .Select(e => new SettlementPosition
            {
                Id = $"set-{Guid.NewGuid():N}",
                SourceAccountId = e.SourceAccountId,
                TargetPoolId = e.TargetPoolId,
                CreatedByBookingId = e.CreatedByBookingId,
                CreatedDate = e.CreatedDate,
                Amount = e.Amount,
                RestAmount = e.RestAmount,
                Status = e.Status,
                SettledByBookingId = e.SettledByBookingId,
                SettledDate = e.SettledDate,
                Note = "Nachgeführte Position (Upgrade auf die Positionsverwaltung).",
            })
            .ToList();

        await settlementPositionRepository.AddAsync(rows, cancellationToken);
        return rows.Count;
    }
}
