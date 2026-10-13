namespace Lario.Domain.Journal;

/// <summary>
/// Ein Eintrag des Delta einer Buchung auf die signierte
/// Konto-Pool-Zuordnung (Rechenvertrag §3.4): der Betrag, um den die
/// Zelle (Konto, Pool) durch die Buchung verändert wird.
/// </summary>
public sealed record AllocationDeltaEntry(string AccountId, string PoolId, Money Amount);
