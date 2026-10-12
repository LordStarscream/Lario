namespace Lario.Domain.MasterData;

/// <summary>
/// Alltagskategorie mit optionaler Unterkategorie (Eltern-Kind-Baum,
/// Rechenvertrag §3.1). Unabhängig von Steuerkategorien, Konten und Pools.
/// </summary>
public sealed class Category : MasterDataEntity
{
    /// <summary>Id der übergeordneten Kategorie; null bei Wurzeln.</summary>
    public string? ParentId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Steuer-Voreinstellung dieser Kategorie (1:1, optional).</summary>
    public TaxPreset? TaxPreset { get; set; }

    public override string ToString() => $"{Id} ({Name}, {Status})";
}
