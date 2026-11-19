using Lario.Application;
using Lario.Domain;
using Lario.Domain.MasterData;

namespace Lario.Host.Api.MasterData;

/// <summary>
/// Gemeinsames DTO-Mapping der Stammdaten (Schritt 9, Batch A1: für den
/// Sync-Bootstrap extrahiert — der Bootstrap-Snapshot muss exakt dieselben
/// DTOs/Mapping liefern wie die Stammdaten-Endpunkte). Die Fachlogik
/// bleibt in <c>MasterDataAdmin</c> / <c>MasterDataCatalog</c>; hier wird
/// nur transportiert.
/// </summary>
public static class MasterDataDtoMapper
{
    public static AccountDto ToDto(Account account) => new(
        account.Id,
        account.Name,
        AccountTypeToString(account.Type),
        account.IsInterestBearing,
        StatusToString(account.Status),
        account.BaseRevision);

    public static PoolDto ToDto(Pool pool) => new(
        pool.Id,
        pool.Name,
        StatusToString(pool.Status),
        pool.BaseRevision);

    public static CategoryDto ToDto(Category category, MasterDataCatalog catalog)
    {
        var preset = catalog.ResolveEffectivePreset(category.Id);
        return new CategoryDto(
            category.Id,
            category.Name,
            category.ParentId,
            StatusToString(category.Status),
            category.BaseRevision,
            preset is null ? null : ToPresetDto(preset, catalog));
    }

    public static TaxCategoryDto ToDto(TaxCategory taxCategory) => new(
        taxCategory.Id,
        taxCategory.Name,
        taxCategory.ParentId,
        StatusToString(taxCategory.Status),
        taxCategory.BaseRevision);

    public static TaxPresetDto ToPresetDto(TaxPreset preset, MasterDataCatalog catalog) => new(
        preset.TaxRelevant,
        preset.TaxCategoryId,
        preset.TaxCategoryId is { } id ? catalog.FindTaxCategory(id)?.Name : null,
        preset.InheritToChildren,
        preset.BaseRevision);

    public static string AccountTypeToString(AccountType type) => type switch
    {
        AccountType.Bank => "bank",
        AccountType.Savings => "savings",
        _ => throw new LarioDomainException($"Unbekannter Kontotyp '{type}'.")
    };

    public static string StatusToString(EntityStatus status) => status switch
    {
        EntityStatus.Active => "active",
        EntityStatus.Archived => "archived",
        _ => throw new LarioDomainException($"Unbekannter Status '{status}'.")
    };
}
