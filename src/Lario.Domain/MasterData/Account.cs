namespace Lario.Domain.MasterData;

/// <summary>
/// Reales Konto (Kernbegriff aus Rechenvertrag §3.1) — strikt getrennt von
/// Pool, Kategorie und Vorhaben.
/// </summary>
public sealed class Account : MasterDataEntity
{
    public string Name { get; set; } = string.Empty;

    public AccountType Type { get; set; }

    /// <summary>Verzinstes Konto (z. B. Sparkonto); für Vorhabenparken ab Schritt 14 relevant.</summary>
    public bool IsInterestBearing { get; set; }

    public override string ToString() => $"{Id} ({Name}, {Status})";
}

/// <summary>Kontotyp; Version 1 kennt Bank- und Sparkonten.</summary>
public enum AccountType
{
    Bank = 0,

    /// <summary>Sparkonto/Verzinstes Konto.</summary>
    Savings = 1,
}
