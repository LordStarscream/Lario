using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;

namespace Lario.Application.Journal;

/// <summary>
/// Abfrage der internen Ausgleichspositionen (Rechenvertrag §3.5):
/// „Die Anwendung zeigt, welche internen Ausgleiche offen sind, wie
/// hoch sie sind, durch welche Buchung sie begründet wurden und welche
/// Buchung sie beglichen hat."
///
/// Die Ansicht ist zeilengetrieben: jede persistierte Position zeigt
/// ihren gespeicherten Zustand mit der stabilen Zeilenidentität (Id,
/// Begründung, Fachdatum). Die abgeleiteten Episoden des Journals
/// (<see cref="BookingJournal.DeriveSettlementPositions"/>) dienen als
/// Wahrheit zur Prüfung und für die offene Position:
/// - Die offene Position zeigt den Journalzustand (die Episode kann
///   rückdatiert in die Vergangenheit verschoben, vertieft oder mit
///   anderen Episoden zusammengeführt worden sein) mit der stabilen
///   Zeilen-Id.
/// - Beglichene Zeilen, die zu keiner Episode passen (zusammengeführte
///   Zyklen), bleiben sichtbar, wenn der zusammengeführte Zyklus im
///   Journal weiterbesteht (beglichene Episode mit derselben
///   Begründung) — andernfalls sind sie unsichtbare Historie (die
///   jüngste Zeile trägt den vereinigten Zyklus).
///
/// Integritätsfehler (fehlgeschlagene, sichtbare Abfrage — keine
/// stille Korrektur):
/// - eine Zelle trägt zwei offene Zeilen;
/// - die offene Episode und die offene Zeile existieren nicht
///   zusammen oder passen nicht zusammen;
/// - eine beglichene Episode, die nicht älter ist als die älteste
///   Zeile der Zelle und nicht begründungsgleich zu ihr ist, hat
///   keine passende Zeile — sie wäre still verschwunden.
/// </summary>
public sealed class SettlementQueryService(
    IMasterDataRepository masterDataRepository,
    IBookingRepository bookingRepository,
    ISettlementPositionRepository settlementPositionRepository)
{
    /// <summary>Eine Ausgleichsposition mit ihren Stammdaten-Namen.</summary>
    public sealed record SettlementView(
        string Id,
        string SourceAccountId,
        string SourceAccountName,
        string TargetPoolId,
        string TargetPoolName,
        Money Amount,
        Money RestAmount,
        SettlementStatus Status,
        string CreatedByBookingId,
        DateOnly CreatedDate,
        string? SettledByBookingId,
        DateOnly? SettledDate);

    /// <summary>
    /// Alle Ausgleichspositionen (offen und beglichen), sortiert nach
    /// Fachdatum und Buchungsid der Begründung.
    /// </summary>
    public async Task<IReadOnlyList<SettlementView>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return (await LoadAsync(cancellationToken, openOnly: false)).ToList();
    }

    /// <summary>
    /// Nur die offenen Ausgleichspositionen, sortiert nach Fachdatum und
    /// Buchungsid der Begründung.
    /// </summary>
    public async Task<IReadOnlyList<SettlementView>> GetOpenAsync(
        CancellationToken cancellationToken = default)
    {
        return (await LoadAsync(cancellationToken, openOnly: true)).ToList();
    }

    private async Task<IReadOnlyList<SettlementView>> LoadAsync(
        CancellationToken cancellationToken, bool openOnly)
    {
        var catalog = await masterDataRepository.LoadCatalogAsync(cancellationToken);
        var bookings = await bookingRepository.GetAllAsync(cancellationToken);
        var persisted = await settlementPositionRepository.GetAllAsync(cancellationToken);

        var episodes = new BookingJournal(bookings).DeriveSettlementPositions();

        var rowsByCell = persisted
            .GroupBy(p => (p.SourceAccountId, p.TargetPoolId))
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderBy(r => r.CreatedDate)
                    .ThenBy(r => r.CreatedByBookingId, StringComparer.Ordinal)
                    .ToList());

        var episodesByCell = episodes
            .GroupBy(e => (e.SourceAccountId, e.TargetPoolId))
            .ToDictionary(
                g => g.Key,
                g => g.ToList()); // Derivate sind pro Zelle in kanonischer Reihenfolge

        var views = new List<SettlementView>();
        foreach (var cell in rowsByCell.Keys.Union(episodesByCell.Keys))
        {
            var rows = rowsByCell.GetValueOrDefault(cell) ?? [];
            var cellEpisodes = episodesByCell.GetValueOrDefault(cell) ?? [];

            var openRows = rows.Count(r => r.Status == SettlementStatus.Offen);
            if (openRows > 1)
            {
                throw new LarioDomainException(
                    $"Integritätsfehler: die Zelle (Konto '{cell.Item1}', Pool '{cell.Item2}') " +
                    $"trägt {openRows} offene Ausgleichspositionen — zulässig ist maximal eine " +
                    "(die offene Episode einer Zelle ist eindeutig).");
            }

            var openRow = rows.FirstOrDefault(r => r.Status == SettlementStatus.Offen);
            var openEpisode = cellEpisodes.Count > 0
                && cellEpisodes[^1].Status == SettlementStatus.Offen
                    ? cellEpisodes[^1]
                    : null;

            // Offene Episode ↔ offene Zeile: beide Seiten müssen
            // zusammenpassen (die Synchronisation hält die Invariante),
            // sonst ist die persistierte Ansicht inkonsistent. Die
            // Ansicht zeigt den Journalzustand (Episode) mit der
            // stabilen Zeilen-Id — die Episode kann rückdatiert
            // verschoben, vertieft oder zusammengeführt worden sein.
            if (openEpisode is not null || openRow is not null)
            {
                if (openEpisode is null || openRow is null)
                {
                    throw new LarioDomainException(
                        $"Integritätsfehler: die Zelle (Konto '{cell.Item1}', Pool '{cell.Item2}') " +
                        $"trägt eine offene Ausgleichsposition nur in " +
                        (openEpisode is null ? "der Tabelle" : "dem Journal") +
                        " — abgeleitete Episode und persistierte Zeile müssen zusammenpassen.");
                }

                if (!ValuesMatch(openRow, openEpisode))
                {
                    throw new LarioDomainException(
                        $"Integritätsfehler: die Ausgleichsposition '{openRow.Id}' weicht " +
                        "vom abgeleiteten Journalzustand ab.");
                }

                views.Add(BuildOpenView(catalog, openRow, openEpisode));
            }

            // Beglichene Zeilen: sichtbar, wenn
            //  (1) eine werteidentische beglichene Episode existiert
            //      (normale Historie),
            //  (2) eine beglichene Episode mit derselben Begründung
            //      existiert (zusammengeführter Zyklus — die jüngste
            //      Zeile trägt den vereinigten Wert, die älteren
            //      bleiben als Historie sichtbar), oder
            //  (3) der Zyklus der Zeile im Replay verschwunden ist
            //      (z. B. rückdatierte Begleichung, die vor der
            //      Begründung sortiert) und die Zeile nicht in eine
            //      offene zusammengeführte Episode aufgegangen ist —
            //      erkennbar daran, dass die offene Episode nach der
            //      Begründung der Zeile beginnt: der Zyklus der Zeile
            //      ist ein abgeschlossener, separater Sachverhalt.
            //  Andernfalls: unsichtbare Historie (die Zeile ist in eine
            //  offene zusammengeführte Episode aufgegangen — die
            //  jüngste Zeile trägt sie).
            var settledEpisodes = cellEpisodes
                .Where(e => e.Status == SettlementStatus.Beglichen)
                .ToList();

            var matchedEpisodeIndexes = new HashSet<int>();
            foreach (var row in rows.Where(r => r.Status == SettlementStatus.Beglichen))
            {
                // (1) werteidentische Episode (eins zu eins).
                int? matchIndex = null;
                for (var i = 0; i < settledEpisodes.Count; i++)
                {
                    if (matchedEpisodeIndexes.Contains(i))
                    {
                        continue;
                    }

                    if (ValuesMatch(row, settledEpisodes[i]))
                    {
                        matchIndex = i;
                        break;
                    }
                }
                if (matchIndex is not null)
                {
                    matchedEpisodeIndexes.Add(matchIndex.Value);
                    views.Add(BuildSettledView(catalog, row));
                    continue;
                }

                // (2) zusammengeführter Zyklus: Episode mit derselben
                // Begründung (Konto, Pool, erstellende Buchung, Fachdatum).
                var mergedEpisode = settledEpisodes
                    .FirstOrDefault(e => e.CreatedByBookingId == row.CreatedByBookingId
                        && e.CreatedDate == row.CreatedDate);
                if (mergedEpisode is not null)
                {
                    views.Add(BuildSettledView(catalog, row));
                    continue;
                }

                // (3) verschollener Zyklus: die Zeile ist nicht in eine
                // offene zusammengeführte Episode aufgegangen (die
                // offene Episode beginnt nach der Begründung der Zeile
                // — separater, abgeschlossener Sachverhalt) oder es gibt
                // keine offene Episode.
                var subsumedByOpenEpisode = openEpisode is not null
                    && CompareIdentity(
                        row.CreatedDate, row.CreatedByBookingId,
                        openEpisode.CreatedDate, openEpisode.CreatedByBookingId) > 0;
                if (!subsumedByOpenEpisode)
                {
                    views.Add(BuildSettledView(catalog, row));
                }
                // Sonst unsichtbar (Historie).
            }

            // Beglichene Episoden ohne passende Zeile: legitim nur, wenn
            // sie älter sind als die älteste Zeile der Zelle (Historie
            // vor der Positionsverwaltung — der Backfill legt Zeilen nur
            // für offene Episoden an) oder ohne jede Zeile. Jede andere
            // Episode ohne Zeile wäre still verschwunden —
            // Integritätsfehler.
            if (rows.Count > 0)
            {
                var oldestRow = rows[0];
                for (var i = 0; i < settledEpisodes.Count; i++)
                {
                    if (matchedEpisodeIndexes.Contains(i))
                    {
                        continue; // Gepaart.
                    }

                    var episode = settledEpisodes[i];
                    var sameIdentityRow = rows.Any(r =>
                        r.CreatedByBookingId == episode.CreatedByBookingId
                        && r.CreatedDate == episode.CreatedDate);
                    if (sameIdentityRow)
                    {
                        continue; // Begründungsgleich zu einer Zeile (zusammengeführter Zyklus).
                    }

                    var beforeOldestRow = episode.CreatedDate < oldestRow.CreatedDate
                        || (episode.CreatedDate == oldestRow.CreatedDate
                            && string.CompareOrdinal(
                                episode.CreatedByBookingId, oldestRow.CreatedByBookingId) < 0);
                    if (!beforeOldestRow)
                    {
                        throw new LarioDomainException(
                            $"Integritätsfehler: das Journal enthält eine beglichene " +
                            $"Ausgleichsepisode (Konto '{cell.Item1}', Pool '{cell.Item2}', " +
                            $"Begründung '{episode.CreatedByBookingId}'), zu der keine " +
                            "passende persistierte Position existiert.");
                    }
                }
            }
            // Ohne Zeilen: gesamte Historie ist Altbestand (unsichtbar).
        }

        return views
            .Where(v => !openOnly || v.Status == SettlementStatus.Offen)
            .OrderBy(v => v.CreatedDate)
            .ThenBy(v => v.CreatedByBookingId, StringComparer.Ordinal)
            .ThenBy(v => v.SourceAccountId, StringComparer.Ordinal)
            .ThenBy(v => v.TargetPoolId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Vergleicht zwei Begründungsidentitäten (Fachdatum, dann Buchungsid
    /// — die kanonische Journal-Reihenfolge) mit -1/0/+1.
    /// </summary>
    private static int CompareIdentity(
        DateOnly aDate, string aId, DateOnly bDate, string bId)
    {
        var byDate = aDate.CompareTo(bDate);
        return byDate != 0
            ? byDate
            : string.CompareOrdinal(aId, bId);
    }

    /// <summary>
    /// Wertegleichheit einer persistierten Zeile und einer abgeleiteten
    /// Episode (Identitätsfelder wie Id und Notiz gehören nicht dazu).
    /// </summary>
    private static bool ValuesMatch(
        SettlementPosition row, SettlementPositionState episode)
    {
        return row.Amount == episode.Amount
            && row.RestAmount == episode.RestAmount
            && row.Status == episode.Status
            && row.SettledByBookingId == episode.SettledByBookingId
            && row.SettledDate == episode.SettledDate;
    }

    /// <summary>
    /// Prüft die Stammdatenreferenzen der Zeile und liefert die Namen.
    /// </summary>
    private static (string AccountName, string PoolName) ResolveNames(
        MasterDataCatalog catalog, SettlementPosition row)
    {
        var account = catalog.FindAccount(row.SourceAccountId);
        var pool = catalog.FindPool(row.TargetPoolId);
        if (account is null || pool is null)
        {
            // Die Position verweist auf Stammdaten, die nicht mehr
            // existieren (z. B. gelöscht, nicht archiviert): die
            // Ansicht bleibt möglich, der Name ist unbekannt.
            throw new LarioDomainException(
                $"Integritätsfehler: die Ausgleichsposition '{row.Id}' referenziert " +
                "unbekanntes Konto oder Pool.");
        }

        return (account.Name, pool.Name);
    }

    /// <summary>
    /// Offene Position: stabile Zeilen-Id und Stammdaten von der Zeile,
    /// Werte und Begründung vom Journal (Episode) — die Episode kann
    /// rückdatiert verschoben, vertieft oder zusammengeführt worden sein.
    /// </summary>
    private static SettlementView BuildOpenView(
        MasterDataCatalog catalog,
        SettlementPosition row,
        SettlementPositionState episode)
    {
        var (accountName, poolName) = ResolveNames(catalog, row);
        return new SettlementView(
            row.Id,
            row.SourceAccountId,
            accountName,
            row.TargetPoolId,
            poolName,
            episode.Amount,
            episode.RestAmount,
            episode.Status,
            episode.CreatedByBookingId,
            episode.CreatedDate,
            episode.SettledByBookingId,
            episode.SettledDate);
    }

    /// <summary>
    /// Beglichene Position: alles von der Zeile. Die Synchronisation hält
    /// die Werte mit der abgeleiteten Episode gleich; die Begründung ist
    /// die stabile Historie der Zeile — auch wenn die zusammengeführte
    /// Episode eine andere Begründung trägt.
    /// </summary>
    private static SettlementView BuildSettledView(
        MasterDataCatalog catalog, SettlementPosition row)
    {
        var (accountName, poolName) = ResolveNames(catalog, row);
        return new SettlementView(
            row.Id,
            row.SourceAccountId,
            accountName,
            row.TargetPoolId,
            poolName,
            row.Amount,
            row.RestAmount,
            row.Status,
            row.CreatedByBookingId,
            row.CreatedDate,
            row.SettledByBookingId,
            row.SettledDate);
    }
}
