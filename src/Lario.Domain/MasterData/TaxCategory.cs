namespace Lario.Domain.MasterData;

/// <summary>
/// Steuerkategorie (Rechenvertrag §3.1, Plan §9): eigener, frei anlegbarer
/// Baum mit optionalen Unterkategorien — unabhängig von Alltagskategorien,
/// Konten und Pools. Die Zuordnung zu einer Steuerkategorie verändert keine
/// Finanzsalden.
/// </summary>
public sealed class TaxCategory : MasterDataEntity
{
    /// <summary>Id der übergeordneten Steuerkategorie; null bei Wurzeln.</summary>
    public string? ParentId { get; set; }

    public string Name { get; set; } = string.Empty;

    public override string ToString() => $"{Id} ({Name}, {Status})";
}
