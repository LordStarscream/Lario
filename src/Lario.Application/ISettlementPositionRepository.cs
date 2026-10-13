using Lario.Domain.Journal;

namespace Lario.Application;

/// <summary>
/// Gezielte Schnittstelle für die internen Ausgleichspositionen
/// (Rechenvertrag §3.5). Die Positionen sind die persistent gemachte
/// Ansicht des Journals: Wahrheit ist das Journal, die Tabelle trägt
/// stabile Identitäten und den Statusverlauf. Der reguläre Schreibweg
/// (Buchung zusammen mit ihren Positionseffekten) läuft über
/// <see cref="IBookingRepository.AddAsync(Booking,
/// System.Collections.Generic.IReadOnlyCollection{SettlementPosition},
/// System.Threading.CancellationToken)"/>, damit Buchung und
/// Positionseffekte atomar bleiben; <see cref="AddAsync"/> dient dem
/// Upgrade-Backfill (Schritt-5-Review P2d), der fehlende Zeilen ohne
/// neue Buchung nachführt.
/// </summary>
public interface ISettlementPositionRepository
{
    /// <summary>
    /// Lädt eine Position nach ihrer stabilen Identität; null, wenn
    /// unbekannt.
    /// </summary>
    Task<SettlementPosition?> FindByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lädt alle Positionen (offen und beglichen).
    /// </summary>
    Task<IReadOnlyList<SettlementPosition>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fügt neue Zeilen in einer einzigen Transaktion hinzu (Upgrade-
    /// Backfill, Schritt-5-Review P2d) — ein Teilkommitt ist
    /// ausgeschlossen. Die übergebenen Zeilen müssen neue stabile Ids
    /// tragen (es darf keine Zeile mit dieser Id geben).
    /// </summary>
    Task AddAsync(
        IReadOnlyCollection<SettlementPosition> positions,
        CancellationToken cancellationToken = default);
}
