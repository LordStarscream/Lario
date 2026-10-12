using Lario.Domain.MasterData;

namespace Lario.Application;

/// <summary>
/// Anwendungsfälle für die Stammdaten-Einrichtung (Schritt 3): Anlage,
/// Umbenennen, Archivierung, Reaktivierung und Steuer-Voreinstellungen.
/// Jeder Aufruf lädt den Katalog, wendet die Änderung über die
/// Domain-Regeln an und persistiert die geänderte Entität. Bei einem
/// Fehler bleibt der vorherige Stand erhalten.
/// </summary>
public sealed class MasterDataAdmin(IMasterDataRepository repository)
{
    // ------------------------------------------------------------------
    // Konten
    // ------------------------------------------------------------------

    public async Task<Account> CreateAccountAsync(
        string id, string name, AccountType type, bool isInterestBearing = false,
        CancellationToken cancellationToken = default)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        var account = catalog.CreateAccount(id, name, type, isInterestBearing);
        await repository.SaveAccountAsync(account, cancellationToken);
        return account;
    }

    public async Task ArchiveAccountAsync(string id, CancellationToken cancellationToken = default)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        catalog.ArchiveAccount(id);
        await repository.SaveAccountAsync(catalog.FindAccount(id)!, cancellationToken);
    }

    /// <summary>
    /// Benennt ein aktives Konto um; die Id bleibt stabil. Archivierte
    /// Konten werden nicht umbenannt (Name gehört zum historischen Verweis).
    /// </summary>
    public async Task RenameAccountAsync(
        string id, string newName, CancellationToken cancellationToken = default)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        catalog.RenameAccount(id, newName);
        await repository.SaveAccountAsync(catalog.FindAccount(id)!, cancellationToken);
    }

    /// <summary>Reaktiviert ein archiviertes Konto.</summary>
    public async Task UnarchiveAccountAsync(string id, CancellationToken cancellationToken = default)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        catalog.UnarchiveAccount(id);
        await repository.SaveAccountAsync(catalog.FindAccount(id)!, cancellationToken);
    }

    // ------------------------------------------------------------------
    // Pools
    // ------------------------------------------------------------------

    public async Task<Pool> CreatePoolAsync(string id, string name, CancellationToken cancellationToken = default)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        var pool = catalog.CreatePool(id, name);
        await repository.SavePoolAsync(pool, cancellationToken);
        return pool;
    }

    public async Task ArchivePoolAsync(string id, CancellationToken cancellationToken = default)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        catalog.ArchivePool(id);
        await repository.SavePoolAsync(catalog.FindPool(id)!, cancellationToken);
    }

    /// <summary>
    /// Benennt einen aktiven Pool um; die Id bleibt stabil. Archivierte
    /// Pools werden nicht umbenannt (Name gehört zum historischen Verweis).
    /// </summary>
    public async Task RenamePoolAsync(
        string id, string newName, CancellationToken cancellationToken = default)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        catalog.RenamePool(id, newName);
        await repository.SavePoolAsync(catalog.FindPool(id)!, cancellationToken);
    }

    /// <summary>Reaktiviert einen archivierten Pool.</summary>
    public async Task UnarchivePoolAsync(string id, CancellationToken cancellationToken = default)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        catalog.UnarchivePool(id);
        await repository.SavePoolAsync(catalog.FindPool(id)!, cancellationToken);
    }

    // ------------------------------------------------------------------
    // Kategorien (Alltag)
    // ------------------------------------------------------------------

    public async Task<Category> CreateCategoryAsync(
        string id, string name, string? parentId = null, CancellationToken cancellationToken = default)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        var category = catalog.CreateCategory(id, name, parentId);
        await repository.SaveCategoryAsync(category, cancellationToken);
        return category;
    }

    public async Task ArchiveCategoryAsync(string id, CancellationToken cancellationToken = default)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        catalog.ArchiveCategory(id);
        await repository.SaveCategoryAsync(catalog.FindCategory(id)!, cancellationToken);
    }

    /// <summary>
    /// Benennt eine aktive Kategorie um; die Id bleibt stabil. Archivierte
    /// Kategorien werden nicht umbenannt (Name gehört zum historischen Verweis).
    /// </summary>
    public async Task RenameCategoryAsync(
        string id, string newName, CancellationToken cancellationToken = default)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        catalog.RenameCategory(id, newName);
        await repository.SaveCategoryAsync(catalog.FindCategory(id)!, cancellationToken);
    }

    /// <summary>Reaktiviert eine archivierte Kategorie.</summary>
    public async Task UnarchiveCategoryAsync(string id, CancellationToken cancellationToken = default)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        catalog.UnarchiveCategory(id);
        await repository.SaveCategoryAsync(catalog.FindCategory(id)!, cancellationToken);
    }

    // ------------------------------------------------------------------
    // Steuerkategorien
    // ------------------------------------------------------------------

    public async Task<TaxCategory> CreateTaxCategoryAsync(
        string id, string name, string? parentId = null, CancellationToken cancellationToken = default)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        var taxCategory = catalog.CreateTaxCategory(id, name, parentId);
        await repository.SaveTaxCategoryAsync(taxCategory, cancellationToken);
        return taxCategory;
    }

    public async Task ArchiveTaxCategoryAsync(string id, CancellationToken cancellationToken = default)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        catalog.ArchiveTaxCategory(id);
        await repository.SaveTaxCategoryAsync(catalog.FindTaxCategory(id)!, cancellationToken);
    }

    /// <summary>
    /// Benennt eine aktive Steuerkategorie um; die Id bleibt stabil.
    /// Archivierte Steuerkategorien werden nicht umbenannt (Name gehört
    /// zum historischen Verweis).
    /// </summary>
    public async Task RenameTaxCategoryAsync(
        string id, string newName, CancellationToken cancellationToken = default)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        catalog.RenameTaxCategory(id, newName);
        await repository.SaveTaxCategoryAsync(catalog.FindTaxCategory(id)!, cancellationToken);
    }

    /// <summary>Reaktiviert eine archivierte Steuerkategorie.</summary>
    public async Task UnarchiveTaxCategoryAsync(string id, CancellationToken cancellationToken = default)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        catalog.UnarchiveTaxCategory(id);
        await repository.SaveTaxCategoryAsync(catalog.FindTaxCategory(id)!, cancellationToken);
    }

    // ------------------------------------------------------------------
    // Steuer-Voreinstellungen
    // ------------------------------------------------------------------

    /// <summary>
    /// Setzt die explizite Steuer-Voreinstellung einer Kategorie
    /// (1:1). <paramref name="taxRelevant"/> null = nicht festgelegt,
    /// <paramref name="taxCategoryId"/> null = keine gewählt.
    /// </summary>
    public async Task<TaxPreset> SetTaxPresetAsync(
        string categoryId,
        bool? taxRelevant,
        string? taxCategoryId,
        bool inheritToChildren,
        CancellationToken cancellationToken = default)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        var preset = catalog.SetTaxPreset(categoryId, taxRelevant, taxCategoryId, inheritToChildren);
        await repository.SaveCategoryAsync(catalog.FindCategory(categoryId)!, cancellationToken);
        return preset;
    }

    /// <summary>
    /// Entfernt die explizite Steuer-Voreinstellung einer Kategorie; die
    /// wirksame Voreinstellung ergibt sich dann wieder aus der Vererbung.
    /// </summary>
    public async Task RemoveTaxPresetAsync(string categoryId, CancellationToken cancellationToken = default)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        catalog.RemoveTaxPreset(categoryId);
        await repository.SaveCategoryAsync(catalog.FindCategory(categoryId)!, cancellationToken);
    }
}
