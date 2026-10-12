namespace Lario.Domain.MasterData;

/// <summary>
/// Gemeinsame Basis aller Stammdaten-Entitäten.
///
/// Revision (Rechenvertrag §5.5): Jede Fachentität trägt ihre eigene
/// Basisrevision — ein pro Entität geführter Zähler, keine globale
/// Vorgangsnummer. Neue Entitäten starten mit dem einheitlichen Startwert
/// 1; jede Änderung erhöht die Revision um 1.
/// </summary>
public abstract class MasterDataEntity
{
    /// <summary>Stabile Identität (z. B. „acc-haupt").</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Lebenszyklus-Status; neue Entitäten sind aktiv.</summary>
    public EntityStatus Status { get; set; } = EntityStatus.Active;

    /// <summary>Eigene Basisrevision; Startwert 1 (Rechenvertrag §5.5).</summary>
    public int BaseRevision { get; set; } = 1;

    internal void ThrowIfInvalidId()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new LarioDomainException($"Stammdaten-ID fehlt: {GetType().Name} ohne Id.");
        }
    }
}
