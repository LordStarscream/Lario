namespace Lario.Domain.MasterData;

/// <summary>
/// Zweck, in den Geld eingeordnet ist (z. B. Flex, Freizeit) — Kernbegriff
/// aus Rechenvertrag §3.1, strikt getrennt von Konto und Kategorie.
/// Der Poolbestand ist die Spaltensumme der signierten Konto-Pool-Zuordnung
/// (abgeleitet aus den PoolPostings, Rechenvertrag §3.4).
/// </summary>
public sealed class Pool : MasterDataEntity
{
    public string Name { get; set; } = string.Empty;

    public override string ToString() => $"{Id} ({Name}, {Status})";
}
