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
/// Der Zustand wird aus dem Journal abgeleitet und mit der persistierten
/// Positionstabelle abgeglichen: die Journalableitung ist die Wahrheit,
/// die Tabelle liefert die stabilen Identitäten und den Statusverlauf.
/// Abweichungen sind Integritätsfehler und werden nicht still
/// ausgeglichen.
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

        // Dieselbe Pairing-Regel wie SettlementPositionSync.Plan: pro Zelle
        // trägt die jüngste Zeile die (maximal eine) offene Episode; die
        // beglichenen Episoden und Zeilen werden chronologisch gepaart.
        // Persistierte Zeilen behalten ihre Identität (Id, Begründung,
        // Notiz) über gleichdatierte und rückdatierte Buchungen — die
        // Ansicht zeigt den Journalzustand mit der stabilen Zeilen-Id.
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

            SettlementPositionState? openEpisode = null;
            var settledEpisodes = new List<SettlementPositionState>();
            var settledRows = new List<SettlementPosition>();

            if (cellEpisodes.Count > 0 && cellEpisodes[^1].Status == SettlementStatus.Offen)
            {
                openEpisode = cellEpisodes[^1];
                var lastRowCarriesOpenEpisode = rows.Count > 0
                    && (rows[^1].Status == SettlementStatus.Offen
                        || rows.Count >= cellEpisodes.Count);

                if (lastRowCarriesOpenEpisode)
                {
                    settledEpisodes = cellEpisodes
                        .Take(cellEpisodes.Count - 1)
                        .Where(e => e.Status == SettlementStatus.Beglichen)
                        .ToList();
                    settledRows = rows
                        .Take(rows.Count - 1)
                        .Where(r => r.Status == SettlementStatus.Beglichen)
                        .ToList();
                }
                else
                {
                    settledEpisodes = cellEpisodes
                        .Take(cellEpisodes.Count - 1)
                        .Where(e => e.Status == SettlementStatus.Beglichen)
                        .ToList();
                    settledRows = rows
                        .Where(r => r.Status == SettlementStatus.Beglichen)
                        .ToList();
                }
            }
            else
            {
                settledEpisodes = cellEpisodes
                    .Where(e => e.Status == SettlementStatus.Beglichen)
                    .ToList();
                settledRows = rows
                    .Where(r => r.Status == SettlementStatus.Beglichen)
                    .ToList();
            }

            // Offene Episode ↔ offene Zeile: beide Seiten müssen existieren
            // und zusammenpassen (eine rückdatierte Buchung, die die offene
            // Episode vollständig ausgleicht, fehlt eine Seite — sichtbarer
            // Fehler, kein stiller Zustand).
            var openRow = rows.FirstOrDefault(r => r.Status == SettlementStatus.Offen);
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

                if (openRow.Amount != openEpisode.Amount
                    || openRow.RestAmount != openEpisode.RestAmount
                    || openRow.SettledByBookingId != openEpisode.SettledByBookingId
                    || openRow.SettledDate != openEpisode.SettledDate)
                {
                    throw new LarioDomainException(
                        $"Integritätsfehler: die Ausgleichsposition '{openRow.Id}' weicht " +
                        "vom abgeleiteten Journalzustand ab.");
                }

                views.Add(BuildView(catalog, openEpisode, openRow));
            }

            // Beglichene Episoden ↔ beglichene Zeilen (gleiche Paarung wie
            // Plan): immer von hinten — die jüngste beglichene Episode
            // gehört zur jüngsten beglichenen Zeile, die nächjüngste zur
            // nächjüngsten usw. Überzählige älteste Zeilen (Neuöffnungen
            // ohne passende Episode) und überzählige älteste Episoden
            // ohne Zeile (Altdaten aus Zeiten vor Schritt 5) bleiben
            // unsichtbar statt falsche Wertepaare zu bilden.
            var pairCount = Math.Min(settledRows.Count, settledEpisodes.Count);
            for (var i = 0; i < pairCount; i++)
            {
                AddSettledView(
                    views, catalog,
                    settledRows[settledRows.Count - 1 - i],
                    settledEpisodes[settledEpisodes.Count - 1 - i]);
            }
        }

        return views
            .Where(v => !openOnly || v.Status == SettlementStatus.Offen)
            .OrderBy(v => v.CreatedDate)
            .ThenBy(v => v.CreatedByBookingId, StringComparer.Ordinal)
            .ThenBy(v => v.SourceAccountId, StringComparer.Ordinal)
            .ThenBy(v => v.TargetPoolId, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddSettledView(
        List<SettlementView> views, MasterDataCatalog catalog,
        SettlementPosition row, SettlementPositionState episode)
    {
        if (row.Amount != episode.Amount
            || row.RestAmount != episode.RestAmount
            || row.SettledByBookingId != episode.SettledByBookingId
            || row.SettledDate != episode.SettledDate)
        {
            throw new LarioDomainException(
                $"Integritätsfehler: die Ausgleichsposition '{row.Id}' weicht " +
                "vom abgeleiteten Journalzustand ab.");
        }

        views.Add(BuildView(catalog, episode, row));
    }

    private static SettlementView BuildView(
        MasterDataCatalog catalog, SettlementPositionState episode, SettlementPosition row)
    {
        var account = catalog.FindAccount(episode.SourceAccountId);
        var pool = catalog.FindPool(episode.TargetPoolId);
        if (account is null || pool is null)
        {
            // Die Position verweist auf Stammdaten, die nicht mehr
            // existieren (z. B. gelöscht, nicht archiviert): die
            // Ansicht bleibt möglich, der Name ist unbekannt.
            throw new LarioDomainException(
                $"Integritätsfehler: die Ausgleichsposition '{row.Id}' referenziert " +
                "unbekanntes Konto oder Pool.");
        }

        return new SettlementView(
            row.Id,
            episode.SourceAccountId,
            account.Name,
            episode.TargetPoolId,
            pool.Name,
            episode.Amount,
            episode.RestAmount,
            episode.Status,
            episode.CreatedByBookingId,
            episode.CreatedDate,
            episode.SettledByBookingId,
            episode.SettledDate);
    }
}
