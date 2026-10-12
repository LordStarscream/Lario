using Lario.Domain.MasterData;

namespace Lario.Application;

/// <summary>
/// Gezielte Schnittstelle für den Zugriff auf Stammdaten (Schritt 3).
/// Bewusst keine generische Repository-Abstraktion: die Methode drückt
/// die tatsächlichen Fachoperationen aus — Katalog laden, eine geänderte
/// Entität persistieren (inkl. ihrer Steuer-Voreinstellung bei Kategorien).
/// </summary>
public interface IMasterDataRepository
{
    /// <summary>Lädt den vollständigen Stammdaten-Katalog (inkl. archivierter Entitäten).</summary>
    Task<MasterDataCatalog> LoadCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>Persistiert eine neue oder geänderte Konto-Entität.</summary>
    Task SaveAccountAsync(Account account, CancellationToken cancellationToken = default);

    /// <summary>Persistiert eine neue oder geänderte Pool-Entität.</summary>
    Task SavePoolAsync(Pool pool, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persistiert eine neue oder geänderte Kategorie. Trägt die Kategorie
    /// eine <see cref="Category.TaxPreset"/>, wird sie mitgespeichert;
    /// ist sie null, wird eine vorhandene Voreinstellung entfernt.
    /// </summary>
    Task SaveCategoryAsync(Category category, CancellationToken cancellationToken = default);

    /// <summary>Persistiert eine neue oder geänderte Steuerkategorie.</summary>
    Task SaveTaxCategoryAsync(TaxCategory taxCategory, CancellationToken cancellationToken = default);
}
