using Lario.Domain;
using Lario.Domain.Journal;

namespace Lario.Application.Journal;

/// <summary>
/// Leitet die internen Ausgleichseffekte einer neuen Buchung ab
/// (Rechenvertrag §3.2/§3.5): Übergangsanalyse des abgeleiteten
/// Zellenzustands — Episoden des Journals vor und nach der Buchung —
/// gegen die persistierten Positionen. Reine Funktion ohne I/O: die
/// Journalwahrheit bleibt die Abfolge der Buchungen, die
/// Positionstabelle ist deren persistente Ansicht mit stabilen
/// Identitäten.
///
/// Stabile Episodenidentität (Schritt-5-Review P1/R3): Eine Episode ist
/// ein maximaler negativer Abschnitt einer Zelle (Konto, Pool). Ihre
/// <i>Abgrenzung</i> (welche Buchung sie begründet, wo sie schließt)
/// hängt von der kanonischen Reihenfolge im Journal (Fachdatum, dann
/// Id) ab — eine neue gleichdatierte oder rückdatierte Buchung kann
/// dieselbe Episode in die Vergangenheit verschieben, vertiefen, kürzen
/// oder mehrere Episoden zu einer zusammenführen. Die <i>Reihenfolge</i>
/// der Episoden ist dagegen stabil, und die Form des Übergangs (vorher/
/// nachher) bestimmt eindeutig, was passiert:
/// - Offener Zyklus vor der Buchung: er ist dieselbe Episode nachher
///   (vertieft/gekürzt/umgeordnet — die offene Zeile trägt ihn) oder im
///   Replay vollständig ausgeglichen (Vollausgleich — die offene Zeile
///   wird kontrolliert abgeschlossen; existiert der Zyklus im Replay
///   noch als jüngste beglichene Episode, folgt die Zeile deren
///   Begleichungsreferenz, sonst wird die neue Buchung die
///   Begleichungsreferenz). Neue Episoden entstehen in diesem Übergang
///   nicht.
/// - Kein offener Zyklus vorher, offene Episode nachher: entweder <i>
///   reine Addition</i> — die vorherigen Episoden sind im nachher-
///   Zustand vollständig (werteidentisch) enthalten und die offene
///   Episode ist angehängt; dann ist es ein <i>neuer Zyklus</i> und
///   bekommt eine eigene Zeile mit neuem Id. Oder die Buchung hat den
///   jüngsten beglichenen Zyklus verlängert oder mehrere beglichene
///   Zyklen zusammengeführt — dann trägt die jüngste Zeile die offene
///   Episode (wird sie beglichen, wird sie wieder geöffnet und behält
///   ihre Identität; die älteren zusammengeführten Zeilen bleiben als
///   beglichene Historie erhalten). Ohne jede Zeile (Historie vor der
///   Positionsverwaltung) wird ebenfalls eine neue angelegt.
/// - Weder offen vorher noch offen nachher: die beglichene Historie
///   bleibt unverändert.
///
/// Damit behalten persistierte Zeilen ihre Identität
/// (Id, CreatedByBookingId, CreatedDate, Notiz) — sie werden nicht durch
/// eine Neuableitung verdrängt; nur Rest, Status und
/// Begleichungsreferenz folgen der Journalwahrheit. Neue, zulässige
/// gleichdatierte und rückdatierte Buchungen bleiben speicherbar, und
/// ein abgeschlossener Sachverhalt wird nicht durch einen späteren
/// neuen Zyklus überschrieben (Schritt-5-Review R3).
///
/// Integritätsfehler (fehlgeschlagene, sichtbare Abfrage/Speicherung):
/// - eine Zelle trägt zwei offene Zeilen (die offene Episode einer
///   Zelle ist eindeutig);
/// - die offene Episode des Journals hat keine offene Zeile (fremde
///   oder beschädigte Datenbank — der Backfill stellt diese
///   Invariante beim Start her);
/// - eine beglichene Episode des Journals, die nicht älter ist als die
///   älteste Zeile der Zelle, hat keine werteidentische Zeile — sie
///   wäre still aus der Ansicht verschwunden.
///
/// Verwaiste Zeilen ohne passende Episode (zusammengeführte bzw. voll
/// ausgeglichene Zyklen) sind dagegen legitime, unsichtbare Historie —
/// sie bleiben in der Datenbank erhalten, die Ansicht zeigt den
/// Journalzustand (Schritt-5-Review R1/R3).
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

        var beforeEpisodes = new BookingJournal(bookings).DeriveSettlementPositions();
        var afterEpisodes = new BookingJournal(bookings.Concat([newBooking])).DeriveSettlementPositions();

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

        var beforeByCell = beforeEpisodes
            .GroupBy(e => (e.SourceAccountId, e.TargetPoolId))
            .ToDictionary(g => g.Key, g => g.ToList());

        var afterByCell = afterEpisodes
            .GroupBy(e => (e.SourceAccountId, e.TargetPoolId))
            .ToDictionary(g => g.Key, g => g.ToList()); // Derivate sind pro Zelle in kanonischer Reihenfolge

        var changes = new List<PlannedChange>();
        foreach (var cell in rowsByCell.Keys.Union(afterByCell.Keys).Union(beforeByCell.Keys))
        {
            var rows = rowsByCell.GetValueOrDefault(cell) ?? [];
            var before = beforeByCell.GetValueOrDefault(cell) ?? [];
            var after = afterByCell.GetValueOrDefault(cell) ?? [];

            // Integrität: maximal eine offene Zeile pro Zelle (die offene
            // Episode der Zelle ist eindeutig).
            var openRows = rows.Count(r => r.Status == SettlementStatus.Offen);
            if (openRows > 1)
            {
                throw new LarioDomainException(
                    $"Integritätsfehler: die Zelle (Konto '{cell.Item1}', Pool '{cell.Item2}') " +
                    $"trägt {openRows} offene Ausgleichspositionen — zulässig ist maximal eine " +
                    "(die offene Episode einer Zelle ist eindeutig).");
            }

            var openRow = rows.FirstOrDefault(r => r.Status == SettlementStatus.Offen);
            var beforeOpen = before.Count > 0 && before[^1].Status == SettlementStatus.Offen;
            var afterOpen = after.Count > 0 && after[^1].Status == SettlementStatus.Offen;

            // Die geplante Änderung der Zeile, die die offene Episode
            // trägt bzw. neu anlegt — für den Historien-Check unten wird
            // sie bereits wirksam (effective) angerechnet.
            var openRowChange = (PlannedChange?)null;
            SettlementPosition? affectedRow = null;

            if (afterOpen)
            {
                var openEpisode = after[^1];

                if (beforeOpen)
                {
                    // Der offene Zyklus existiert vor der Buchung (die
                    // Buchung vertieft, verkürzt oder ordnet ihn um):
                    // dieselbe Episode — die offene Zeile trägt sie.
                    if (openRow is null)
                    {
                        if (rows.Count > 0)
                        {
                            throw new LarioDomainException(
                                $"Integritätsfehler: die Zelle (Konto '{cell.Item1}', Pool '{cell.Item2}') " +
                                "hat im Journal einen offenen Ausgleichszyklus, aber keine " +
                                "offene Ausgleichsposition. Prüfen Sie die Datenbank oder " +
                                "starten Sie eine Datenbankabstimmung.");
                        }

                        // Keine Zeile (Datenbank ohne Positionsverwaltung,
                        // z. B. Backfill nicht gelaufen): die Zeile wird
                        // jetzt angelegt.
                        openRowChange = new PlannedChange(CreateRow(openEpisode, newIdFactory), true);
                        changes.Add(openRowChange);
                    }
                    else
                    {
                        affectedRow = openRow;
                        openRowChange = UpdateIfDrifted(changes, openRow, openEpisode);
                    }
                }
                else
                {
                    // Offene Episode nachher, ohne offenen Zyklus vorher:
                    // reiner Zusatz — die vorherigen Episoden sind im
                    // nachher-Zustand vollständig (werteidentisch) enthalten
                    // und die offene Episode ist angehängt (neuer Zyklus,
                    // eigene Zeile) — oder die Buchung hat den jüngsten
                    // beglichenen Zyklus verlängert oder mehrere beglichene
                    // Zyklen zusammengeführt (die jüngste Zeile trägt die
                    // offene Episode und wird bei Bedarf wieder geöffnet).
                    // Ohne jede Zeile (Historie vor der Positionsverwaltung)
                    // wird ebenfalls eine neue Zeile angelegt.
                    if (openRow is not null)
                    {
                        // Offene Zeile ohne offene Episode: die persistierte
                        // Ansicht ist inkonsistent (sichtbarer Fehler).
                        throw new LarioDomainException(
                            $"Integritätsfehler: die Zelle (Konto '{cell.Item1}', Pool '{cell.Item2}') " +
                            "trägt eine offene Ausgleichsposition, aber das Journal enthält " +
                            "keinen offenen Ausgleichszyklus — die persistierte Ansicht ist " +
                            "inkonsistent. Prüfen Sie die Datenbank oder starten Sie eine " +
                            "Datenbankabstimmung.");
                    }

                    var pureAddition = after.Count == before.Count + 1
                        && after.Take(before.Count).SequenceEqual(before);

                    if (pureAddition || rows.Count == 0)
                    {
                        openRowChange = new PlannedChange(CreateRow(openEpisode, newIdFactory), true);
                        changes.Add(openRowChange);
                    }
                    else
                    {
                        affectedRow = rows[^1];
                        openRowChange = UpdateIfDrifted(changes, rows[^1], openEpisode);
                    }
                }
            }
            else if (beforeOpen)
            {
                // Der offene Zyklus vor der Buchung ist im Replay
                // vollständig ausgeglichen (Vollausgleich durch die neue
                // Buchung, ggf. rückdatiert): die offene Zeile wird
                // kontrolliert abgeschlossen — Identität und Höhe bleiben
                // erhalten, Status und Begleichungsreferenz folgen der
                // Journalwahrheit.
                if (openRow is not null)
                {
                    affectedRow = openRow;
                    var lastEpisode = after.Count > 0 ? after[^1] : null;
                    if (lastEpisode is not null
                        && lastEpisode.Status == SettlementStatus.Beglichen
                        && lastEpisode.CreatedByBookingId == before[^1].CreatedByBookingId)
                    {
                        // Der Zyklus existiert im Replay als jüngste beglichene
                        // Episode (Zufluss nach der Begründung): dieselbe
                        // Begründung — die Zeile folgt der Begleichungsreferenz
                        // der kanonischen Reihenfolge.
                        openRowChange = UpdateIfDrifted(changes, openRow, lastEpisode);
                    }
                    else
                    {
                        // Der Zyklus ist im Replay verschwunden (Zufluss sortiert
                        // vor der Begründung): die neue Buchung ist die
                        // Begleichungsreferenz; Identität und Höhe der Zeile
                        // bleiben erhalten.
                        openRowChange = new PlannedChange(CloseRow(openRow, newBooking), false);
                        changes.Add(openRowChange);
                    }
                }
                else if (rows.Count > 0)
                {
                    // Offener Zyklus im Journal, aber keine offene Zeile:
                    // inkonsistente persistierte Ansicht (sichtbarer Fehler).
                    throw new LarioDomainException(
                        $"Integritätsfehler: die Zelle (Konto '{cell.Item1}', Pool '{cell.Item2}') " +
                        "hat im Journal vor der Buchung einen offenen Ausgleichszyklus, " +
                        "aber keine offene Ausgleichsposition. Prüfen Sie die Datenbank " +
                        "oder starten Sie eine Datenbankabstimmung.");
                }
                // Keine Zeile (Datenbank ohne Positionsverwaltung): der
                // Zyklus hat keine Zeile — nichts abzuschließen; die
                // Historie bleibt unsichtbar.
            }
            else if (openRow is not null)
            {
                // Weder offen vorher noch offen nachher, aber eine offene
                // Zeile: die persistierte Ansicht ist inkonsistent
                // (sichtbarer Fehler).
                throw new LarioDomainException(
                    $"Integritätsfehler: die Zelle (Konto '{cell.Item1}', Pool '{cell.Item2}') " +
                    "trägt eine offene Ausgleichsposition, aber das Journal enthält " +
                    "keinen offenen Ausgleichszyklus — die persistierte Ansicht ist " +
                    "inkonsistent. Prüfen Sie die Datenbank oder starten Sie eine " +
                    "Datenbankabstimmung.");
            }
            // Sonst: keine offene Episode, keine offene Zeile — die
            // beglichene Historie bleibt unverändert (im Übergang einer
            // einzelnen Buchung kann sich der Wert einer beglichenen
            // Episode, die nicht die betroffene Zeile trägt, nicht ändern).

            // Integrität: jede beglichene Episode des nachher-Zustands,
            // die nicht älter ist als die älteste Zeile der Zelle, muss
            // eine werteidentische beglichene Zeile tragen — sonst wäre
            // sie still aus der Ansicht verschwunden (z. B.
            // handschriftlich veränderte Zeile). Die Zeilen des
            // effektiven Zustands (nach der geplanten Änderung der
            // betroffenen Zeile) werden gewertet: eine Zeile, die die
            // Buchung selbst abschließt, zählt als passend. Zeilen ohne
            // passende Episode (zusammengeführte bzw. voll ausgeglichene
            // Zyklen) sind dagegen legitime, unsichtbare Historie — sie
            // dürfen ohne Fehler bleiben.
            var effectiveRows = new List<SettlementPosition>(rows.Count + (openRowChange is { IsNew: true } ? 1 : 0));
            foreach (var row in rows)
            {
                if (ReferenceEquals(row, affectedRow) && openRowChange is { IsNew: false })
                {
                    effectiveRows.Add(openRowChange.Row);
                }
                else
                {
                    effectiveRows.Add(row);
                }
            }

            if (openRowChange is { IsNew: true })
            {
                effectiveRows.Add(openRowChange.Row);
            }

            var effectiveSettledRows = effectiveRows
                .Where(r => r.Status == SettlementStatus.Beglichen)
                .ToList();
            var oldestRow = effectiveRows.Count > 0 ? effectiveRows[0] : null;

            foreach (var episode in after.Where(e => e.Status == SettlementStatus.Beglichen))
            {
                if (effectiveSettledRows.Any(r => RowMatchesEpisode(r, episode)))
                {
                    continue;
                }

                // Ohne jede Zeile ist die gesamte Historie Altbestand
                // (vor der Positionsverwaltung) — beglichene Episoden
                // ohne Zeile sind legitim (unsichtbar, Backfill legt für
                // sie keine Zeile an).
                var beforeOldestRow = oldestRow is null
                    || episode.CreatedDate < oldestRow.CreatedDate
                    || (episode.CreatedDate == oldestRow.CreatedDate
                        && string.CompareOrdinal(
                            episode.CreatedByBookingId, oldestRow.CreatedByBookingId) < 0);
                if (!beforeOldestRow)
                {
                    throw new LarioDomainException(
                        $"Integritätsfehler: das Journal enthält eine beglichene " +
                        $"Ausgleichsepisode (Konto '{cell.Item1}', Pool '{cell.Item2}', " +
                        $"Begründung '{episode.CreatedByBookingId}'), zu der keine " +
                        "passende persistierte Position existiert. Prüfen Sie die " +
                        "Datenbank oder starten Sie eine Datenbankabstimmung.");
                }
            }
        }

        return changes;
    }

    /// <summary>
    /// Ergänzt ein Update, wenn die Zeile vom abgeleiteten Zustand
    /// abweicht; andernfalls bleibt die Zeile unverändert. Liefert die
    /// geplante Änderung (oder null, wenn die Zeile passt).
    /// </summary>
    private static PlannedChange? UpdateIfDrifted(
        List<PlannedChange> changes, SettlementPosition row, SettlementPositionState episode)
    {
        if (RowMatchesEpisode(row, episode))
        {
            return null;
        }

        var updated = new SettlementPosition
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

        var change = new PlannedChange(updated, false);
        changes.Add(change);
        return change;
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
