namespace Lario.Domain.Journal;

/// <summary>
/// Eine Zeile der expliziten Pool-Verteilung eines Anfangsbestands:
/// Betrag, der von dem Konto an den Zweck (Pool) zugeordnet wird.
/// </summary>
public sealed record PoolAllocation(string PoolId, Money Amount);
