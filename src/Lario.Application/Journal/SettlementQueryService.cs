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
        var persistedByKey = persisted.ToDictionary(p =>
            (p.SourceAccountId, p.TargetPoolId, p.CreatedByBookingId));

        var views = new List<SettlementView>();
        foreach (var episode in episodes)
        {
            if (openOnly && episode.Status != SettlementStatus.Offen)
            {
                continue;
            }

            var key = (episode.SourceAccountId, episode.TargetPoolId, episode.CreatedByBookingId);
            if (!persistedByKey.TryGetValue(key, out var row))
            {
                throw new LarioDomainException(
                    $"Integritätsfehler: die abgeleitete Ausgleichsposition " +
                    $"(Konto '{episode.SourceAccountId}', Pool '{episode.TargetPoolId}', " +
                    $"Begründung '{episode.CreatedByBookingId}') hat keine persistierte Zeile.");
            }

            if (row.Amount != episode.Amount
                || row.RestAmount != episode.RestAmount
                || row.Status != episode.Status
                || row.SettledByBookingId != episode.SettledByBookingId
                || row.SettledDate != episode.SettledDate)
            {
                throw new LarioDomainException(
                    $"Integritätsfehler: die Ausgleichsposition '{row.Id}' weicht " +
                    "vom abgeleiteten Journalzustand ab.");
            }

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

            views.Add(new SettlementView(
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
                episode.SettledDate));
        }

        return views
            .OrderBy(v => v.CreatedDate)
            .ThenBy(v => v.CreatedByBookingId, StringComparer.Ordinal)
            .ThenBy(v => v.SourceAccountId, StringComparer.Ordinal)
            .ThenBy(v => v.TargetPoolId, StringComparer.Ordinal)
            .ToList();
    }
}
