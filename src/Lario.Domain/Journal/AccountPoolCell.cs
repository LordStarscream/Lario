namespace Lario.Domain.Journal;

/// <summary>
/// Eine Zelle der signierten Konto-Pool-Zuordnung (Rechenvertrag §3.4):
/// der Betrag, den das Konto physisch für den Zweck (Pool) trägt.
/// Negativ = interne Vorfinanzierung (Verknüpfung mit einer offenen
/// Ausgleichsposition, §3.5).
/// </summary>
public sealed record AccountPoolCell(string AccountId, string PoolId, Money Amount);
