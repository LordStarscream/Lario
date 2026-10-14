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
/// Stabile Episodenidentität (Schritt-5-Review P1/R3/R4): Eine Episode
/// ist ein maximaler negativer Abschnitt einer Zelle (Konto, Pool). Ihre
/// <i>Abgrenzung</i> (welche Buchung sie begründet, wo sie schließt,
/// welche Höhe sie hat) hängt von der kanonischen Reihenfolge im Journal
/// (Fachdatum, dann Id) ab — eine neue gleichdatierte oder rückdatierte
/// Buchung kann dieselbe Episode in die Vergangenheit verschieben,
/// vertiefen, kürzen, ihren Wert verändern oder mehrere Episoden zu
/// einer zusammenführen. Die <i>Reihenfolge</i> der Episoden ist dagegen
/// stabil, und die Form des Übergangs (vorher/nachher) bestimmt eindeutig,
/// was passiert:
/// - Offener Zyklus vor der Buchung:
///   - Läuft er im Replay bis zum Journalende (<i>Fortsetzung</i>), ist
///     er dieselbe Episode nachher (vertieft/gekürzt/umgeordnet — die
///     offene Zeile trägt ihn und nimmt dessen Werte an); verkürzt oder
///     verändert die Buchung daneben eine beglichene Episode derselben
///     Zelle (z. B. rückdatierter Zufluss), folgt deren bestehende Zeile
///     dem Replay — Identität bleibt (Schritt-5-Review R5).
///   - Wurde er durch die Buchung abgeschlossen, ist es ein <i>Split</i>
///     (der linke Teil — gleiche Begründung — ist beglichen und schließt
///     die offene Zeile; der neue offene Zyklus bekommt eine eigene
///     Zeile mit neuem Id) oder ein <i>Vollausgleich</i> (der Zyklus
///     verschwindet aus dem Replay — die offene Zeile wird kontrolliert
///     abgeschlossen; existiert der Zyklus im Replay noch als jüngste
///     beglichene Episode, folgt die Zeile deren Begleichungsreferenz,
///     sonst wird die neue Buchung die Begleichungsreferenz; folgt ein
///     neuer offener Zyklus, bekommt er eine eigene Zeile mit neuem Id).
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
///   bleibt, was sie ist — eine beglichene Episode, die eine Zeile
///   begründungsgleich trägt (gleiche erstellende Buchung), übernimmt
///   ihre veränderten Werte (z. B. rückdatierter Zufluss vor der
///   Begründung kürzt die Episode — Schritt-5-Review R4); andernfalls
///   bleibt die werteidentische Zeile unverändert.
///
/// Einheitlicher Historien-Abgleich (Schritt-5-Review R6): nach der
/// Behandlung des offenen Zyklus werden in ALLEN Übergangsformen die
/// beglichenen Episoden des nachher-Zustands gegen die effektiven
/// Zeilen abgeglichen (geplante Änderungen sind bereits eingepreist) —
/// erst werteidentisch, dann per stabiler Identität (Begründungsbuchung
/// + Fachdatum). Eine identitätsgleiche, aber wertverschiedene Zeile
/// nimmt die Replay-Werte an (die neue Buchung hat die Episode
/// legitim verändert: verkürzt, vertieft, zusammengeführt); geänderte
/// Zeilen werden nach Row.Id zusammengeführt (Upsert), es werden nie
/// widersprüchliche Duplikate geplant. So bleibt die verkürzte/beglichene
/// Historie auch dann rekonstruierbar, wenn dieselbe Buchung zugleich
/// einen offenen Zyklus schließt (R6: der Vollausgleich offener
/// Position wird gespeichert, nicht abgelehnt).
///
/// Integrität VOR der ersten Überschreibung (Schritt-5-Review R6): jede
/// Episode des VORHER-Zustands, die nicht älter ist als die älteste
/// Zeile der Zelle, muss eine werteidentische Trägerzeile haben — nur
/// die neue Buchung darf eine Zeile verändern, und das zeigt sich als
/// vorher-Episode ≠ nachher-Episode. Eine Episode, die ihre Zeile nicht
/// (mehr) werteidentisch trägt, ist Manipulation und wird sichtbar
/// gemeldet, statt von einem Zweig still überschrieben oder
/// verschluckt zu werden.
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
/// - eine VORHER-Episode (jünger als der Altbestand) hat keine
///   werteidentische Trägerzeile (Manipulation — Schritt-5-Review R6);
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

    /// <summary>
    /// Übergang „neue Buchung wird angehängt": vorher = Journal
    /// <paramref name="bookings"/>, nachher = Journal plus
    /// <paramref name="newBooking"/>.
    /// </summary>
    public static IReadOnlyList<PlannedChange> Plan(
        IReadOnlyList<Booking> bookings,
        Booking newBooking,
        IReadOnlyList<SettlementPosition> persisted,
        Func<string> newIdFactory)
        => PlanCore(
            bookings,
            bookings.Concat([newBooking]).ToList(),
            newBooking, persisted, newIdFactory);

    /// <summary>
    /// Übergang „Buchung wird in-place korrigiert“ (Schritt 6):
    /// vorher = Journal <paramref name="bookings"/> (enthält das
    /// Original), nachher = Journal mit der korrigierten Buchung
    /// <paramref name="newBooking"/> anstelle des Originals
    /// <paramref name="oldBooking"/> — dieselbe Id, neuer Inhalt. Der
    /// Übergang wird mit derselben Übergangsanalyse abgearbeitet wie die
    /// Neuanlage: die korrigierte Buchung ist die einzige Ursache für
    /// Abweichungen zwischen vorher- und nachher-Zustand, und
    /// <paramref name="newBooking"/> ist die Begleichungsreferenz, wenn
    /// ein offener Zyklus im Replay verschwindet.
    /// </summary>
    public static IReadOnlyList<PlannedChange> Plan(
        IReadOnlyList<Booking> bookings,
        Booking oldBooking,
        Booking newBooking,
        IReadOnlyList<SettlementPosition> persisted,
        Func<string> newIdFactory)
        => PlanCore(
            bookings,
            bookings.Where(b => b.Id != oldBooking.Id).Concat([newBooking]).ToList(),
            newBooking, persisted, newIdFactory);

    private static IReadOnlyList<PlannedChange> PlanCore(
        IReadOnlyList<Booking> beforeBookings,
        IReadOnlyList<Booking> afterBookings,
        Booking referenceBooking,
        IReadOnlyList<SettlementPosition> persisted,
        Func<string> newIdFactory)
    {
        ArgumentNullException.ThrowIfNull(beforeBookings);
        ArgumentNullException.ThrowIfNull(afterBookings);
        ArgumentNullException.ThrowIfNull(referenceBooking);
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(newIdFactory);

        var beforeEpisodes = new BookingJournal(beforeBookings).DeriveSettlementPositions();
        var afterEpisodes = new BookingJournal(afterBookings).DeriveSettlementPositions();

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

            // Schritt-5-Review R6: Integrität des VORHER-Zustands —
            // bevor irgendein Zweig eine Zeile verändert, muss jede
            // Zeile, die eine vorher-Episode per stabiler Identität
            // trägt, zu dieser werteidentisch sein. Nur die neue Buchung
            // darf eine Zeile verändern, und das zeigt sich als
            // vorher-Episode ≠ nachher-Episode; eine Abweichung hier ist
            // bereits vorhandene Korruption (Manipulation) und wird
            // sichtbar gemeldet, statt still überschrieben oder
            // verschluckt zu werden.
            ValidateBeforeState(rows, before, cell);

            if (afterOpen && beforeOpen)
            {
                var openEpisode = after[^1];
                var openBeforeEpisode = before[^1];

                // Läuft der alte offene Zyklus im Replay bis zum
                // Journalende? Er ist fortgesetzt, wenn keine beglichene
                // Episode des nachher-Zustands innerhalb seines Abschnitts
                // schließt (ihrer Begleichungspunkt liegt nach der
                // Begründung des alten Zyklus). Schließt dagegen eine
                // beglichene Episode innerhalb des alten Abschnitts, ist
                // der alte Zyklus durch die Buchung abgeschlossen —
                // entweder <i>Split</i> (der linke Teil schließt, ein
                // neuer offener Zyklus folgt) oder <i>Vollausgleich</i>
                // (der Zyklus verschwindet aus dem Replay; ein neuer
                // offener Zyklus kann folgen) — Schritt-5-Review R4.
                var oldCycleContinues = !after.Any(e =>
                    e.Status == SettlementStatus.Beglichen
                    && CompareIdentity(
                        e.SettledDate!.Value, e.SettledByBookingId!,
                        openBeforeEpisode.CreatedDate, openBeforeEpisode.CreatedByBookingId) > 0);

                if (oldCycleContinues)
                {
                    // Fortsetzung: dieselbe Episode — die offene Zeile
                    // trägt sie und nimmt deren Werte an (Vertiefung,
                    // Teilzahlung, gleichdatierte oder rückdatierte
                    // Umordnung).
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
                        changes.Add(new PlannedChange(CreateRow(openEpisode, newIdFactory), true));
                    }
                    else
                    {
                        UpdateIfDrifted(changes, openRow, openEpisode);
                    }

                    // Beglichene Episoden derselben Zelle (eine
                    // rückdatierte Buchung kann sie verkürzen oder
                    // verändern, während der offene Zyklus weiterläuft —
                    // Schritt-5-Review R5) werden unten im einheitlichen
                    // Historien-Abgleich gegen die effektiven Zeilen
                    // gewertet.
                }
                else
                {
                    // Der alte offene Zyklus wurde durch die Buchung
                    // abgeschlossen. Offene Zeile muss vorhanden sein
                    // (außer: keine Zeile — Historie ohne
                    // Positionsverwaltung).
                    if (openRow is null && rows.Count > 0)
                    {
                        throw new LarioDomainException(
                            $"Integritätsfehler: die Zelle (Konto '{cell.Item1}', Pool '{cell.Item2}') " +
                            "hat im Journal einen abgeschlossenen Ausgleichszyklus, aber " +
                            "keine offene Ausgleichsposition. Prüfen Sie die Datenbank oder " +
                            "starten Sie eine Datenbankabstimmung.");
                    }

                    // Split-Test: der linke Teil des alten Zyklus (gleiche
                    // Begründung) existiert im Replay als beglichene
                    // Episode — dann ist es ein Split; sonst hat der Zyklus
                    // im Replay vollständig verschwindend aufgewogen
                    // (Vollausgleich, ggf. rückdatiert).
                    var leftPart = after.FirstOrDefault(e =>
                        e.Status == SettlementStatus.Beglichen
                        && e.CreatedByBookingId == openBeforeEpisode.CreatedByBookingId
                        && e.CreatedDate == openBeforeEpisode.CreatedDate);

                    if (openRow is not null)
                    {
                        if (leftPart is not null)
                        {
                            // Split: die offene Zeile schließt mit den
                            // Werten des linken Teils (Identität bleibt),
                            // der neue offene Zyklus bekommt unten eine
                            // eigene Zeile.
                            changes.Add(new PlannedChange(
                                ApplyEpisodeValues(openRow, leftPart), false));
                        }
                        else
                        {
                            // Vollausgleich: der Zyklus verschwindet aus
                            // dem Replay — kontrollierter Abschluss, die
                            // neue Buchung ist die Begleichungsreferenz
                            // (Schritt-5-Review P1a).
                            changes.Add(new PlannedChange(CloseRow(openRow, referenceBooking), false));
                        }
                    }

                    // Neuer offener Zyklus (Split oder Vollausgleich +
                    // neuer Zyklus): eigene Zeile mit neuem Id — ein
                    // abgeschlossener Sachverhalt wird nicht durch ihn
                    // überschrieben (Schritt-5-Review R3/R4).
                    changes.Add(new PlannedChange(CreateRow(openEpisode, newIdFactory), true));
                }
            }
            else if (afterOpen)
            {
                var openEpisode = after[^1];

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
                    changes.Add(new PlannedChange(CreateRow(openEpisode, newIdFactory), true));
                }
                else
                {
                    UpdateIfDrifted(changes, rows[^1], openEpisode);
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
                    var lastEpisode = after.Count > 0 ? after[^1] : null;
                    if (lastEpisode is not null
                        && lastEpisode.Status == SettlementStatus.Beglichen
                        && lastEpisode.CreatedByBookingId == before[^1].CreatedByBookingId)
                    {
                        // Der Zyklus existiert im Replay als jüngste beglichene
                        // Episode (Zufluss nach der Begründung): dieselbe
                        // Begründung — die Zeile folgt der Begleichungsreferenz
                        // der kanonischen Reihenfolge.
                        UpdateIfDrifted(changes, openRow, lastEpisode);
                    }
                    else
                    {
                        // Der Zyklus ist im Replay verschwunden (Zufluss sortiert
                        // vor der Begründung): die neue Buchung ist die
                        // Begleichungsreferenz; Identität und Höhe der Zeile
                        // bleiben erhalten.
                        changes.Add(new PlannedChange(CloseRow(openRow, referenceBooking), false));
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
            // Schritt-5-Review R6: einheitlicher Historien-Abgleich in
            // ALLEN Übergangsformen — die beglichenen Episoden des
            // nachher-Zustands werden gegen die effektiven Zeilen
            // gewertet (geplante Änderungen des offenen-Zyklus-Zweigs
            // sind bereits eingepreist). Erst werteidentisch (z. B. die
            // gerade geschlossene Zeile eines Splits), dann per stabiler
            // Identität (die Buchung hat die Episode legitim verändert —
            // die Zeile nimmt deren Werte an, Schritt-5-Review R4/R5).
            ReconcileSettledEpisodes(rows, changes, after, cell);

            // Integrität: jede beglichene Episode des nachher-Zustands,
            // die nicht älter ist als die älteste Zeile der Zelle, muss
            // eine werteidentische beglichene Zeile tragen — sonst wäre
            // sie still aus der Ansicht verschwunden (z. B.
            // handschriftlich veränderte Zeile). Die Zeilen des
            // effektiven Zustands (nach den geplanten Änderungen) werden
            // gewertet: eine Zeile, die die Buchung selbst abschließt,
            // zählt als passend. Zeilen ohne passende Episode
            // (zusammengeführte bzw. voll ausgeglichene Zyklen) sind
            // dagegen legitime, unsichtbare Historie — sie dürfen ohne
            // Fehler bleiben.
            // Updates erhalten die Zeilenidentität (Id) — die geplante
            // Ersatzzeile ist per Id zuordnen; neue Zeilen haben ein
            // frisches Id.
            var effectiveRows = BuildEffectiveRowsForCell(rows, changes, cell);

            var effectiveSettledRows = effectiveRows
                .Where(r => r.Status == SettlementStatus.Beglichen)
                .ToList();
            var oldestRow = effectiveRows
                .OrderBy(r => r.CreatedDate)
                .ThenBy(r => r.CreatedByBookingId, StringComparer.Ordinal)
                .FirstOrDefault();

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
                    || CompareIdentity(
                        episode.CreatedDate, episode.CreatedByBookingId,
                        oldestRow.CreatedDate, oldestRow.CreatedByBookingId) < 0;
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

        var change = new PlannedChange(ApplyEpisodeValues(row, episode), false);
        UpsertChange(changes, change.Row);
        return change;
    }

    /// <summary>
    /// Integrität des VORHER-Zustands (Schritt-5-Review R6): jede Episode
    /// des vorher-Zustands, die nicht älter ist als die älteste Zeile der
    /// Zelle, muss eine werteidentische Trägerzeile haben. Nur die neue
    /// Buchung darf eine Zeile verändern — das zeigt sich als
    /// vorher-Episode ≠ nachher-Episode und wird vom übrigen Abgleich
    /// behandelt. Eine Episode, die keine werteidentische Zeile mehr hat,
    /// hat sie durch Manipulation verloren (die Ansicht wäre sonst still
    /// unrichtig oder die Manipulation würde still verschluckt/überschrieben
    /// werden).
    /// <para>
    /// Die Trägerzeile muss nicht die stabile Identität der Episode tragen
    /// (zusammengeführte Zyklen behalten ihre eigene Begründung —
    /// Schritt-5-Review R3). Episoden, die älter sind als die älteste
    /// Zeile (bzw. bei einer Zelle ohne jede Zeile: die gesamte
    /// Historie), sind Altbestand vor der Positionsverwaltung und
    /// legitim unsichtbar. Zeilen ohne tragendes Episode (zusammengeführte
    /// bzw. voll ausgeglichene Zyklen) sind legitime, unsichtbare
    /// Historie und bleiben.
    /// </para>
    /// </summary>
    private static void ValidateBeforeState(
        List<SettlementPosition> rows,
        IReadOnlyList<SettlementPositionState> before,
        (string SourceAccountId, string TargetPoolId) cell)
    {
        if (rows.Count == 0)
        {
            // Ohne Zeilen ist die gesamte Historie Altbestand (vor der
            // Positionsverwaltung) — legitim unsichtbar.
            return;
        }

        var oldestRow = rows
            .OrderBy(r => r.CreatedDate)
            .ThenBy(r => r.CreatedByBookingId, StringComparer.Ordinal)
            .First();

        foreach (var episode in before)
        {
            var beforeOldestRow = CompareIdentity(
                episode.CreatedDate, episode.CreatedByBookingId,
                oldestRow.CreatedDate, oldestRow.CreatedByBookingId) < 0;
            if (beforeOldestRow)
            {
                // Altbestand vor der Positionsverwaltung — bleibt
                // unsichtbar (der Backfill legt für ihn keine Zeile an).
                continue;
            }

            if (rows.Any(r => RowMatchesEpisode(r, episode)))
            {
                continue;
            }

            throw new LarioDomainException(
                $"Integritätsfehler: das Journal enthält die Ausgleichsepisode " +
                $"(Konto '{cell.Item1}', Pool '{cell.Item2}', Begründung '{episode.CreatedByBookingId}') " +
                "des Zustands vor dieser Buchung, zu der keine passende persistierte " +
                "Position existiert — die persistierte Ansicht ist inkonsistent. " +
                "Prüfen Sie die Datenbank oder starten Sie eine Datenbankabstimmung.");
        }
    }

    /// <summary>
    /// Einheitlicher Abgleich der beglichenen Episoden des nachher-Zustands
    /// gegen die effektiven Zeilen (Schritt-5-Review R6): geänderte Zeilen
    /// werden im Zustand NACH den geplanten Änderungen gewertet, sodass
    /// eine Buchung, die zugleich einen offenen Zyklus schließt und eine
    /// beglichene Episode verkürzt, gespeichert statt abgelehnt wird.
    /// <para>
    /// Zuordnung je Episode (Reihenfolge der Episoden):
    /// <list type="number">
    /// <item>werteidentische, noch nicht zugeordnete Zeile — fertig
    ///   (z. B. die gerade geschlossene Zeile eines Splits);</item>
    /// <item>Zeile mit stabiler Identität (Begründungsbuchung + Fachdatum),
    ///   aber abweichenden Werten — die Buchung hat die Episode legitim
    ///   verändert (verkürzt, vertieft, zusammengeführt): die Zeile nimmt
    ///   die Replay-Werte an, Identität bleibt (Schritt-5-Review R4/R5);</item>
    /// <item>keine tragende Zeile — der Historien-Integritätscheck unten
    ///   entscheidet (fehlende Zeile = Integritätsfehler, außer Altbestand
    ///   vor der Positionsverwaltung).</item>
    /// </list>
    /// </para>
    /// </summary>
    private static void ReconcileSettledEpisodes(
        List<SettlementPosition> rows,
        List<PlannedChange> changes,
        IReadOnlyList<SettlementPositionState> after,
        (string SourceAccountId, string TargetPoolId) cell)
    {
        var effectiveRows = BuildEffectiveRowsForCell(rows, changes, cell);
        var taken = new HashSet<string>();
        foreach (var episode in after.Where(e => e.Status == SettlementStatus.Beglichen))
        {
            var valueMatch = effectiveRows.FirstOrDefault(r =>
                r.Status == SettlementStatus.Beglichen
                && !taken.Contains(r.Id)
                && RowMatchesEpisode(r, episode));
            if (valueMatch is not null)
            {
                taken.Add(valueMatch.Id);
                continue;
            }

            var identityMatch = effectiveRows.FirstOrDefault(r =>
                r.Status == SettlementStatus.Beglichen
                && !taken.Contains(r.Id)
                && r.CreatedByBookingId == episode.CreatedByBookingId
                && r.CreatedDate == episode.CreatedDate);
            if (identityMatch is not null && !RowMatchesEpisode(identityMatch, episode))
            {
                taken.Add(identityMatch.Id);
                UpsertChange(changes, ApplyEpisodeValues(identityMatch, episode));
            }
        }
    }

    /// <summary>
    /// Effektiver Zeilenzustand einer Zelle nach den geplanten
    /// Änderungen: bestehende Zeilen, ersetzt durch geplante Updates
    /// (letzte Änderung je Id gewinnt), ergänzt um neue Zeilen —
    /// gefiltert auf die Zelle (changes enthält Änderungen aller
    /// bereits bearbeiteten Zellen).
    /// </summary>
    private static List<SettlementPosition> BuildEffectiveRowsForCell(
        List<SettlementPosition> rows,
        List<PlannedChange> changes,
        (string SourceAccountId, string TargetPoolId) cell)
    {
        var updatesById = new Dictionary<string, SettlementPosition>();
        foreach (var change in changes.Where(c => !c.IsNew
            && c.Row.SourceAccountId == cell.Item1
            && c.Row.TargetPoolId == cell.Item2))
        {
            updatesById[change.Row.Id] = change.Row;
        }

        var effective = rows
            .Select(r => updatesById.TryGetValue(r.Id, out var replacement) ? replacement : r)
            .ToList();

        foreach (var change in changes.Where(c => c.IsNew
            && c.Row.SourceAccountId == cell.Item1
            && c.Row.TargetPoolId == cell.Item2))
        {
            effective.Add(change.Row);
        }

        return effective;
    }

    /// <summary>
    /// Führt geänderte Zeilen nach Row.Id zusammen (Upsert): es werden
    /// nie mehrere widersprüchliche Updates derselben Zeile geplant —
    /// eine spätere Änderung ersetzt die frühere (Schritt-5-Review R6).
    /// </summary>
    private static void UpsertChange(List<PlannedChange> changes, SettlementPosition updatedRow)
    {
        var index = changes.FindIndex(c => !c.IsNew && c.Row.Id == updatedRow.Id);
        if (index >= 0)
        {
            changes[index] = new PlannedChange(updatedRow, false);
        }
        else
        {
            changes.Add(new PlannedChange(updatedRow, false));
        }
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

    /// <summary>
    /// Wendet die Werte einer Episode auf eine bestehende Zeile an —
    /// Identität (Id, Konto, Pool, Begründung, Datum, Notiz) bleibt,
    /// Höhe, Rest, Status und Begleichungsreferenz folgen der
    /// Journalwahrheit.
    /// </summary>
    private static SettlementPosition ApplyEpisodeValues(
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

    /// <summary>
    /// Kanonische Reihenfolge: Fachdatum, dann Id (lexikografisch) —
    /// dieselbe Reihenfolge wie das Journal.
    /// </summary>
    private static int CompareIdentity(
        DateOnly dateA, string idA, DateOnly dateB, string idB)
    {
        var byDate = dateA.CompareTo(dateB);
        return byDate != 0
            ? byDate
            : string.CompareOrdinal(idA, idB);
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
