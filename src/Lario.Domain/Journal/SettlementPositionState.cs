namespace Lario.Domain.Journal;

/// <summary>
/// Derivierter Zustand einer internen Ausgleichsposition (Rechenvertrag
/// §3.5): deterministisch aus dem Buchungsjournal ableitbar. Die
/// Persistierte <see cref="SettlementPosition"/> ist eine materialisierte
/// Ansicht dieses Zustands (zusätzlich mit stabiler Id und Notiz); die
/// Ableitung ist die Quelle der Wahrheit und wird in Tests gegen den
/// gespeicherten Zustand geprüft.
///
/// Identität einer Position (Episode): (Konto, Pool, erstellende
/// Buchung). Eine Zelle kann zu einem Zeitpunkt maximal eine offene
/// Position tragen; nach Abschluss (Rest 0) beginnt bei erneuter
/// negativer Zuordnung eine neue Episode (neue erstellende Buchung).
/// </summary>
public sealed record SettlementPositionState(
    string SourceAccountId,
    string TargetPoolId,
    Money Amount,
    Money RestAmount,
    SettlementStatus Status,
    string CreatedByBookingId,
    DateOnly CreatedDate,
    string? SettledByBookingId,
    DateOnly? SettledDate)
{
    /// <summary>
    /// Natürlicher Schlüssel der Episode: (Konto, Pool, erstellende Buchung).
    /// </summary>
    public (string AccountId, string PoolId, string CreatedByBookingId) Key =>
        (SourceAccountId, TargetPoolId, CreatedByBookingId);
}
