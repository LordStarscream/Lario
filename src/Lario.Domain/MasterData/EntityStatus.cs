namespace Lario.Domain.MasterData;

/// <summary>
/// Lebenszyklus einer Stammdaten-Entität. Entitäten werden archiviert,
/// nie gelöscht — historisch verwendete Daten bleiben nachvollziehbar
/// (Plan §9: „Bereits verwendete Steuerkategorien werden archiviert statt
/// gelöscht", analog für Konten/Pools/Kategorien).
/// </summary>
public enum EntityStatus
{
    /// <summary>Aktiv und für neue Zuordnungen verwendbar.</summary>
    Active = 0,

    /// <summary>Archiviert: historisch nachvollziehbar, aber nicht mehr
    /// als Referenz für neue Daten verwendbar.</summary>
    Archived = 1,
}
