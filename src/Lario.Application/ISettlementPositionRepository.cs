using Lario.Domain.Journal;

namespace Lario.Application;

/// <summary>
/// Gezielte Leseschnittstelle für die internen Ausgleichspositionen
/// (Rechenvertrag §3.5). Die Positionen sind die persistent gemachte
/// Ansicht des Journals: Wahrheit ist das Journal, die Tabelle trägt
/// stabile Identitäten und den Statusverlauf. Schreibzugriffe laufen
/// immer über <see cref="IBookingRepository.AddAsync(Booking,
/// System.Collections.Generic.IReadOnlyCollection{SettlementPosition},
/// System.Threading.CancellationToken)"/>, damit Buchung und
/// Positionseffekte atomar bleiben.
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
}
