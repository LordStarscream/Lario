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
/// Vollausgleich einer offenen Position durch die neue Buchung
/// (Schritt-5-Review P1a): Eine gültige Einnahme, die den offenen Zyklus
/// vollständig abdeckt (auch rückdatiert oder gleichdatiert mit
/// lexikografisch kleinerer Id — dann sortiert der Zufluss vor der
/// Begründung der Zelle und die Episode existiert im Replay nicht mehr),
/// wird nicht abgelehnt. Die Positionshistorie bleibt erhalten: die
/// offene Zeile wird kontrolliert abgeschlossen (Status beglichen,
/// Begleichungsreferenz die neue Buchung bzw. die jüngste beglichene
/// Episode des Zyklus), und die gültige Buchung wird atomar mit ihr
/// gespeichert.
///
/// Integritätsfehler (fehlgeschlagene, sichtbare Abfrage/Speicherung):
/// - eine Zelle trägt zwei offene Zeilen (die offene Episode einer
///   Zelle ist eindeutig).
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

            var openRow = rows.FirstOrDefault(r => r.Status == SettlementStatus.Offen);
            // Die offene Episode (falls vorhanden) ist immer die jüngste
            // Episode der Zelle (Ableitungsregel) — nie episodes[0], wenn
            // ältere beglichene Episoden ohne Zeile vorhanden sind
            // (Schritt-5-Review P1b).
            var openEpisode = episodes.Count > 0
                && episodes[^1].Status == SettlementStatus.Offen
                ? episodes[^1]
                : null;

            List<SettlementPosition> settledRows;
            List<SettlementPositionState> settledEpisodes;

            if (openEpisode is not null)
            {
                if (openRow is not null)
                {
                    // Die offene Zeile trägt die offene Episode: ihr Zyklus
                    // ist laufend, in die Vergangenheit verschoben (Zyklus
                    // verlängert) oder mit einem älteren Zyklus
                    // zusammengeführt — nur die Werte folgen der
                    // Journalwahrheit, die Identität bleibt.
                    if (!RowMatchesEpisode(openRow, openEpisode))
                    {
                        changes.Add(new PlannedChange(UpdateRow(openRow, openEpisode), false));
                    }

                    settledRows = rows.Where(r => !ReferenceEquals(r, openRow)).ToList();
                    settledEpisodes = episodes.Take(episodes.Count - 1).ToList();
                }
                else if (rows.Count >= episodes.Count)
                {
                    // Die jüngste Zeile trägt die offene Episode (Zyklus
                    // verschoben/verlängert oder zusammengeführt): war sie
                    // beglichen, wird sie wieder geöffnet und trägt den
                    // zusammengeführten Zyklus.
                    var lastRow = rows[^1];
                    if (!RowMatchesEpisode(lastRow, openEpisode))
                    {
                        changes.Add(new PlannedChange(UpdateRow(lastRow, openEpisode), false));
                    }

                    settledRows = rows.Take(rows.Count - 1).ToList();
                    settledEpisodes = episodes.Take(episodes.Count - 1).ToList();
                }
                else
                {
                    // Episoden > Zeilen und keine offene Zeile: die offene
                    // Episode ist ein neuer Zyklus — sie bekommt eine
                    // eigene Zeile (neue stabile Id); die vorhandenen
                    // Zeilen und älteren beglichenen Episoden ohne Zeile
                    // gehören zur unsichtbaren Historie.
                    changes.Add(new PlannedChange(CreateRow(openEpisode, newIdFactory), true));

                    settledRows = rows.ToList();
                    settledEpisodes = episodes.Take(episodes.Count - 1).ToList();
                }
            }
            else if (openRow is not null)
            {
                // Die neue Buchung hat die offene Episode vollständig
                // abgedeckt (Schritt-5-Review P1a): die Zelle ist
                // ausgeglichen. Die Positionshistorie bleibt erhalten —
                // die offene Zeile wird kontrolliert abgeschlossen und
                // die gültige Buchung wird atomar mit ihr gespeichert.
                var lastEpisode = episodes.Count > 0 ? episodes[^1] : null;
                if (lastEpisode is not null && lastEpisode.Amount == openRow.Amount)
                {
                    // Der Zyklus existiert im Replay als jüngste beglichene
                    // Episode: die Zeile folgt der Journalwahrheit (u. a.
                    // die Begleichungsreferenz der kanonischen
                    // Reihenfolge).
                    if (!RowMatchesEpisode(openRow, lastEpisode))
                    {
                        changes.Add(new PlannedChange(UpdateRow(openRow, lastEpisode), false));
                    }

                    settledRows = rows.Where(r => !ReferenceEquals(r, openRow)).ToList();
                    settledEpisodes = episodes.Take(episodes.Count - 1).ToList();
                }
                else
                {
                    // Der Zyklus existiert im Replay nicht mehr (der
                    // Zufluss sortiert vor der Begründung der Zelle —
                    // rückdatiert oder gleichdatiert mit kleinerer Id):
                    // die Zeile wird mit der neuen Buchung als
                    // Begleichungsreferenz abgeschlossen.
                    changes.Add(new PlannedChange(CloseRow(openRow, newBooking), false));

                    settledRows = rows.Where(r => !ReferenceEquals(r, openRow)).ToList();
                    settledEpisodes = episodes.ToList();
                }
            }
            else
            {
                // Weder offene Episode noch offene Zeile: beglichene
                // Historie bleibt unverändert (das Journal bleibt die
                // Wahrheit).
                settledRows = rows.ToList();
                settledEpisodes = episodes.ToList();
            }

            // Beglichene Episoden ↔ beglichene Zeilen: von hinten nach
            // vorn paaren. Überzählige älteste Zeilen (Zusammenführung)
            // bzw. älteste Episoden ohne Zeile (Historie vor der
            // Positionsverwaltung) bleiben unverändert/unsichtbar.
            var pairs = Math.Min(settledRows.Count, settledEpisodes.Count);
            for (var i = 0; i < pairs; i++)
            {
                AddUpdateIfDrifted(
                    changes,
                    settledRows[settledRows.Count - 1 - i],
                    settledEpisodes[settledEpisodes.Count - 1 - i]);
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

    /// <summary>
    /// Schließt eine offene Zeile, deren Zyklus im Replay nicht mehr
    /// existiert (der Zufluss sortiert vor der Begründung der Zelle):
    /// Identität und Höhe bleiben, Begleichungsreferenz ist die neue
    /// Buchung — die Historie bleibt nachvollziehbar (Schritt-5-Review
    /// P1a).
    /// </summary>
    private static SettlementPosition CloseRow(
        SettlementPosition row, Booking closingBooking)
    {
        return new SettlementPosition
        {
            Id = row.Id,
            SourceAccountId = row.SourceAccountId,
            TargetPoolId = row.TargetPoolId,
            CreatedByBookingId = row.CreatedByBookingId,
            CreatedDate = row.CreatedDate,
            Note = row.Note,
            Amount = row.Amount,
            RestAmount = Money.Zero,
            Status = SettlementStatus.Beglichen,
            SettledByBookingId = closingBooking.Id,
            SettledDate = closingBooking.Date,
        };
    }
}
