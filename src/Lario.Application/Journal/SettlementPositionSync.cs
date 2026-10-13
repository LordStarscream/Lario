using Lario.Domain;
using Lario.Domain.Journal;

namespace Lario.Application.Journal;

/// <summary>
/// Leitet die internen Ausgleichseffekte einer neuen Buchung ab
/// (Rechenvertrag §3.2/§3.5): Vergleich des abgeleiteten
/// Zellenzustands nach der Buchung gegen die persistierten Positionen.
/// Reine Funktion ohne I/O — die Journalwahrheit bleibt die Abfolge der
/// Buchungen, die Positionstabelle ist deren persistente Ansicht mit
/// stabilen Identitäten.
///
/// Stabile Episodenidentität (Schritt-5-Review P1): Eine Episode ist ein
/// maximaler negativer Abschnitt einer Zelle (Konto, Pool). Ihre
/// <i>Abgrenzung</i> (welche Buchung sie begründet, wo sie schließt)
/// hängt von der Reihenfolge im Journal ab — eine neue gleichdatierte
/// oder rückdatierte Buchung kann dieselbe Episode weiter in die
/// Vergangenheit verschieben oder mehrere Episoden zu einer
/// zusammenführen. Die <i>Reihenfolge</i> der Episoden ist dagegen
/// stabil: Episoden beginnen erst, wenn die vorangegangene abgeschlossen
/// ist, und eine Zusammenführung vereint nur benachbarte Episoden.
/// Deshalb wird pro Zelle so gepaart:
/// - Die offene Episode (falls vorhanden; maximal eine pro Zelle) ist
///   immer die jüngste Episode der Zelle. Sie wird von der jüngsten
///   Zeile getragen, wenn diese offen ist (laufender Zyklus, ggf.
///   rückdatiert in die Vergangenheit verschoben/verlängert oder mit
///   einem älteren Zyklus zusammengeführt) oder wenn es mindestens so
///   viele Zeilen wie Episoden gibt (Zyklus verschoben/verlängert oder
///   zusammengeführt; eine beglichene Zeile wird wieder geöffnet und
///   trägt den zusammengeführten Zyklus). Erst wenn es mehr Episoden
///   als Zeilen gibt und die jüngste Zeile beglichen ist, handelt es
///   sich um einen neuen Zyklus — dann wird eine neue Zeile angelegt.
///   Liegt keine Zeile vor, wird ebenfalls eine neue angelegt.
/// - Die übrigen (beglichenen) Episoden und Zeilen werden bei
///   Zeilen ≥ Episoden von hinten nach vorn gepaart (überzählige ältere
///   Zeilen zusammengeführter Historie bleiben unverändert), sonst von
///   vorn nach hinten (überzählige jüngste beglichene Episoden ohne
///   Zeile bleiben unsichtbar — Historie vor der Positionsverwaltung).
///
/// Damit behalten persistierte Zeilen ihre Identität
/// (Id, CreatedByBookingId, CreatedDate, Notiz) — sie werden nicht durch
/// eine Neuableitung verdrängt; nur Rest, Status und
/// Begleichungsreferenz folgen der Journalwahrheit. Neue, zulässige
/// gleichdatierte und rückdatierte Buchungen bleiben speicherbar: sie
/// vertiefen oder verschieben die aktuelle Episode, ohne dass eine
/// vorhandene Zeile „ihrer" abgeleiteten Episode verlustig geht.
///
/// Integritätsfehler (fehlgeschlagene, sichtbare Abfrage/Speicherung):
/// - eine Zelle trägt zwei offene Zeilen (die offene Episode einer
///   Zelle ist eindeutig);
/// - eine Zelle trägt keine Episode mehr, aber eine persistierte offene
///   Zeile — eine rückdatierte Buchung hat den Zyklus der offenen
///   Position vollständig ausgeglichen.
/// </summary>
internal static class SettlementPositionSync
{
    /// <summary>Eine geplante Zeilenänderung: neue Zeile oder Update.</summary>
    public sealed record PlannedChange(SettlementPosition Row, bool IsNew);

    public static IReadOnlyList<PlannedChange> Plan(
        IReadOnlyList<Booking> bookings,
        Booking newBooking,
        IReadOnlyList<SettlementPosition> persisted,
        Func<string> newIdFactory)
    {
        ArgumentNullException.ThrowIfNull(bookings);
        ArgumentNullException.ThrowIfNull(newBooking);
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(newIdFactory);

        var afterJournal = new BookingJournal(bookings.Concat([newBooking]));
        var afterEpisodes = afterJournal.DeriveSettlementPositions();

        // Zeilen pro Zelle in chronologischer Reihenfolge: (CreatedDate,
        // CreatedByBookingId) sind stabile Attribute — sie werden nach der
        // Anlage nie umgeschrieben und bleiben damit eine eindeutige,
        // wachstumsfeste Sortierung der Episoden einer Zelle.
        var rowsByCell = persisted
            .GroupBy(p => (p.SourceAccountId, p.TargetPoolId))
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderBy(r => r.CreatedDate)
                    .ThenBy(r => r.CreatedByBookingId, StringComparer.Ordinal)
                    .ToList());

        var episodesByCell = afterEpisodes
            .GroupBy(e => (e.SourceAccountId, e.TargetPoolId))
            .ToDictionary(
                g => g.Key,
                g => g.ToList()); // Derivate sind pro Zelle in kanonischer Reihenfolge

        var changes = new List<PlannedChange>();
        foreach (var cell in rowsByCell.Keys.Union(episodesByCell.Keys))
        {
            var rows = rowsByCell.GetValueOrDefault(cell) ?? [];
            var episodes = episodesByCell.GetValueOrDefault(cell) ?? [];

            var openRows = rows.Count(r => r.Status == SettlementStatus.Offen);
            if (openRows > 1)
            {
                throw new LarioDomainException(
                    $"Integritätsfehler: die Zelle (Konto '{cell.Item1}', Pool '{cell.Item2}') " +
                    $"trägt {openRows} offene Ausgleichspositionen — zulässig ist maximal eine " +
                    "(die offene Episode einer Zelle ist eindeutig).");
            }

            if (episodes.Count == 0 || rows.Count == 0)
            {
                if (episodes.Count == 0)
                {
                    // Die Zelle trägt keine negative Episode mehr. Eine
                    // offene Zeile hätte keinen Zyklus, den sie trägt —
                    // eine rückdatierte Buchung hat die Episode
                    // vollständig ausgeglichen. Das ist ein sichtbarer
                    // Konflikt, kein stiller Zustand.
                    var openRow = rows.FirstOrDefault(r => r.Status == SettlementStatus.Offen);
                    if (openRow is not null)
                    {
                        throw new LarioDomainException(
                            $"Integritätsfehler: in der Zelle (Konto '{cell.Item1}', " +
                            $"Pool '{cell.Item2}') ist die offene Ausgleichsposition " +
                            $"(Zeile '{openRow.Id}', aus Buchung '{openRow.CreatedByBookingId}') " +
                            "im Journal vollständig ausgeglichen — der Zyklus existiert " +
                            "nicht mehr. Bitte die Buchungen prüfen und die Position " +
                            "korrigieren.");
                    }

                    // Beglichene Zeilen ohne Episoden bleiben unverändert
                    // (unsichtbare Historie; das Journal bleibt die
                    // Wahrheit).
                    continue;
                }

                // Eine Zelle trägt maximal eine offene Episode. Ohne Zeile
                // wird eine offene Episode nachgeführt (neue stabile Id,
                // deterministische CreatedByBookingId); beglichene
                // Episoden ohne Zeile bleiben unsichtbar.
                if (episodes[0].Status == SettlementStatus.Offen)
                {
                    changes.Add(new PlannedChange(CreateRow(episodes[0], newIdFactory), true));
                }

                continue;
            }

            var lastEpisode = episodes[^1];
            var settledEpisodes = new List<SettlementPositionState>();
            var settledRows = new List<SettlementPosition>();

            if (lastEpisode.Status == SettlementStatus.Offen)
            {
                // Die offene Episode ist die jüngste Episode der Zelle.
                var lastRow = rows[^1];
                if (lastRow.Status == SettlementStatus.Offen ||
                    rows.Count >= episodes.Count)
                {
                    // Die jüngste Zeile trägt die offene Episode: ihr
                    // Zyklus ist laufend, in die Vergangenheit verschoben
                    // (Zyklus verlängert, Anzahl unverändert) oder mit
                    // einem älteren Zyklus zusammengeführt. War die Zeile
                    // beglichen, wird sie wieder geöffnet und trägt den
                    // zusammengeführten Zyklus.
                    if (!RowMatchesEpisode(lastRow, lastEpisode))
                    {
                        changes.Add(new PlannedChange(UpdateRow(lastRow, lastEpisode), false));
                    }

                    settledEpisodes = episodes.Take(episodes.Count - 1).ToList();
                    settledRows = rows.Take(rows.Count - 1).ToList();
                }
                else
                {
                    // Episoden > Zeilen und jüngste Zeile beglichen: die
                    // offene Episode ist ein neuer Zyklus — sie bekommt
                    // eine eigene Zeile; die vorhandenen Zeilen gehören
                    // zur beglichenen Historie.
                    changes.Add(new PlannedChange(CreateRow(lastEpisode, newIdFactory), true));
                    settledEpisodes = episodes.Take(episodes.Count - 1).ToList();
                    settledRows = rows.ToList();
                }
            }
            else
            {
                settledEpisodes = episodes.ToList();
                settledRows = rows.ToList();
            }

            // Beglichene Episoden ↔ beglichene Zeilen. Bei
            // Zeilen ≥ Episoden (Zusammenführung) bleiben die überzähligen
            // ältesten Zeilen unverändert (beglichene Historie) — Paaren
            // von hinten. Bei Episoden > Zeilen (Historie ohne
            // Positionsverwaltung) bleiben die überzähligen jüngsten
            // beglichenen Episoden unsichtbar — Paaren von vorn.
            if (settledRows.Count >= settledEpisodes.Count)
            {
                for (var i = 0; i < settledEpisodes.Count; i++)
                {
                    AddUpdateIfDrifted(
                        changes,
                        settledRows[settledRows.Count - 1 - i],
                        settledEpisodes[settledEpisodes.Count - 1 - i]);
                }
            }
            else
            {
                for (var i = 0; i < settledRows.Count; i++)
                {
                    AddUpdateIfDrifted(changes, settledRows[i], settledEpisodes[i]);
                }
            }
        }

        return changes;
    }

    private static void AddUpdateIfDrifted(
        List<PlannedChange> changes,
        SettlementPosition row,
        SettlementPositionState episode)
    {
        if (RowMatchesEpisode(row, episode))
        {
            return;
        }

        changes.Add(new PlannedChange(UpdateRow(row, episode), false));
    }

    /// <summary>
    /// Passt eine persistierte Zeile an den abgeleiteten Zustand an —
    /// Identität (Id, Konto, Pool, erstellende Buchung, Fachdatum, Notiz)
    /// bleibt unverändert; Rest, Status und Begleichungsreferenz folgen
    /// der Journalwahrheit.
    /// </summary>
    private static SettlementPosition UpdateRow(
        SettlementPosition row, SettlementPositionState episode)
    {
        return new SettlementPosition
        {
            Id = row.Id,
            SourceAccountId = row.SourceAccountId,
            TargetPoolId = row.TargetPoolId,
            CreatedByBookingId = row.CreatedByBookingId,
            CreatedDate = row.CreatedDate,
            Note = row.Note,
            Amount = episode.Amount,
            RestAmount = episode.RestAmount,
            Status = episode.Status,
            SettledByBookingId = episode.SettledByBookingId,
            SettledDate = episode.SettledDate,
        };
    }

    private static bool RowMatchesEpisode(
        SettlementPosition row, SettlementPositionState episode)
    {
        return row.Amount == episode.Amount
            && row.RestAmount == episode.RestAmount
            && row.Status == episode.Status
            && row.SettledByBookingId == episode.SettledByBookingId
            && row.SettledDate == episode.SettledDate;
    }

    private static SettlementPosition CreateRow(
        SettlementPositionState episode, Func<string> newIdFactory)
    {
        return new SettlementPosition
        {
            Id = newIdFactory(),
            SourceAccountId = episode.SourceAccountId,
            TargetPoolId = episode.TargetPoolId,
            CreatedByBookingId = episode.CreatedByBookingId,
            CreatedDate = episode.CreatedDate,
            Amount = episode.Amount,
            RestAmount = episode.RestAmount,
            Status = episode.Status,
            SettledByBookingId = episode.SettledByBookingId,
            SettledDate = episode.SettledDate,
        };
    }
}
