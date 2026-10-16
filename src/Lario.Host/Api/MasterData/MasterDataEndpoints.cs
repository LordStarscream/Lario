using Lario.Application;
using Lario.Domain;
using Lario.Domain.MasterData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Lario.Host.Api.MasterData;

/// <summary>
/// Stammdaten-Endpunkte (Schritt 7, T2; Design §2.4). Alle Endpunkte
/// liegen unter <c>/api/v1</c> und sind durch die ApiAuthMiddleware (T1)
/// geschützt; Fehler folgen dem Schritt-7-Fehlerformat über
/// <c>ExceptionToHttpResult</c>.
///
/// <b>Keine Fachlogik hier</b>: alle Prüfungen und Zustandsübergänge
/// laufen in <c>MasterDataAdmin</c> / <c>MasterDataCatalog</c> (Schritt 3,
/// fixiert). Die Endpunkte mappen nur DTO ↔ Domain. Die Admin-Methoden
/// für Umbenennen/Archivieren/Reaktivieren geben kein Entity zurück —
/// die Antwort-DTOs werden aus einem erneuten Katalog-Laden aufgebaut
/// (gleicher Scope, dieselbe persistierte Entität).
///
/// Konventionen:
/// * Anlage → 200 + DTO (Id wird hostseitig erzeugt; das Design legt
///   keine Client-Ids für Stammdaten fest).
/// * Umbenennen/Archivieren/Reaktivieren → 200 + aktualisiertes DTO
///   (inkl. neuer <c>baseRevision</c>).
/// * Unbekannte Id → <c>InvalidReferenceException</c> → 400 invalid_reference.
/// * Enumerations als stabile Strings: Kontotyp <c>bank|savings</c>,
///   Status <c>active|archived</c>.
/// </summary>
public static class MasterDataEndpoints
{
    public static void MapMasterData(this IEndpointRouteBuilder endpoints)
    {
        // ---------- Konten ----------
        endpoints.MapGet("/api/v1/accounts", GetAccounts)
            .WithName("listAccounts")
            .Produces<IReadOnlyList<AccountDto>>(StatusCodes.Status200OK);

        endpoints.MapGet("/api/v1/accounts/{id}", GetAccount)
            .WithName("getAccount")
            .Produces<AccountDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapPost("/api/v1/accounts", CreateAccount)
            .WithName("createAccount")
            .Produces<AccountDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapPut("/api/v1/accounts/{id}/name", RenameAccount)
            .WithName("renameAccount")
            .Produces<AccountDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapPost("/api/v1/accounts/{id}/archive", ArchiveAccount)
            .WithName("archiveAccount")
            .Produces<AccountDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapPost("/api/v1/accounts/{id}/unarchive", UnarchiveAccount)
            .WithName("unarchiveAccount")
            .Produces<AccountDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        // ---------- Pools ----------
        endpoints.MapGet("/api/v1/pools", GetPools)
            .WithName("listPools")
            .Produces<IReadOnlyList<PoolDto>>(StatusCodes.Status200OK);

        endpoints.MapGet("/api/v1/pools/{id}", GetPool)
            .WithName("getPool")
            .Produces<PoolDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapPost("/api/v1/pools", CreatePool)
            .WithName("createPool")
            .Produces<PoolDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapPut("/api/v1/pools/{id}/name", RenamePool)
            .WithName("renamePool")
            .Produces<PoolDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapPost("/api/v1/pools/{id}/archive", ArchivePool)
            .WithName("archivePool")
            .Produces<PoolDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapPost("/api/v1/pools/{id}/unarchive", UnarchivePool)
            .WithName("unarchivePool")
            .Produces<PoolDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        // ---------- Kategorien (inkl. wirksamer Steuer-Voreinstellung) ----------
        endpoints.MapGet("/api/v1/categories", GetCategories)
            .WithName("listCategories")
            .Produces<IReadOnlyList<CategoryDto>>(StatusCodes.Status200OK);

        endpoints.MapGet("/api/v1/categories/{id}", GetCategory)
            .WithName("getCategory")
            .Produces<CategoryDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapPost("/api/v1/categories", CreateCategory)
            .WithName("createCategory")
            .Produces<CategoryDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapPut("/api/v1/categories/{id}/name", RenameCategory)
            .WithName("renameCategory")
            .Produces<CategoryDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapPost("/api/v1/categories/{id}/archive", ArchiveCategory)
            .WithName("archiveCategory")
            .Produces<CategoryDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapPost("/api/v1/categories/{id}/unarchive", UnarchiveCategory)
            .WithName("unarchiveCategory")
            .Produces<CategoryDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapPut("/api/v1/categories/{id}/tax-preset", SetTaxPreset)
            .WithName("setTaxPreset")
            .Produces<TaxPresetDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapDelete("/api/v1/categories/{id}/tax-preset", RemoveTaxPreset)
            .WithName("removeTaxPreset")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        // ---------- Steuerkategorien ----------
        endpoints.MapGet("/api/v1/tax-categories", GetTaxCategories)
            .WithName("listTaxCategories")
            .Produces<IReadOnlyList<TaxCategoryDto>>(StatusCodes.Status200OK);

        endpoints.MapGet("/api/v1/tax-categories/{id}", GetTaxCategory)
            .WithName("getTaxCategory")
            .Produces<TaxCategoryDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapPost("/api/v1/tax-categories", CreateTaxCategory)
            .WithName("createTaxCategory")
            .Produces<TaxCategoryDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapPut("/api/v1/tax-categories/{id}/name", RenameTaxCategory)
            .WithName("renameTaxCategory")
            .Produces<TaxCategoryDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapPost("/api/v1/tax-categories/{id}/archive", ArchiveTaxCategory)
            .WithName("archiveTaxCategory")
            .Produces<TaxCategoryDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapPost("/api/v1/tax-categories/{id}/unarchive", UnarchiveTaxCategory)
            .WithName("unarchiveTaxCategory")
            .Produces<TaxCategoryDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);
    }

    // ---------- Konten ----------

    private static async Task<IResult> GetAccounts(
        IMasterDataRepository repository, CancellationToken cancellationToken)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        var dtos = catalog.Accounts
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .Select(ToDto)
            .ToList();
        return Results.Ok(dtos);
    }

    private static async Task<IResult> GetAccount(
        string id, IMasterDataRepository repository, CancellationToken cancellationToken)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        return Results.Ok(ToDto(FindAccountOrThrow(catalog, id)));
    }

    private static async Task<IResult> CreateAccount(
        CreateAccountRequest? request,
        MasterDataAdmin admin,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new LarioDomainException(
                "Antrag fehlt: POST /accounts benötigt einen JSON-Körper (name, type).");
        }

        var account = await admin.CreateAccountAsync(
            NewId("acc"), request.Name ?? string.Empty, ParseAccountType(request.Type),
            request.IsInterestBearing ?? false, cancellationToken);
        return Results.Ok(ToDto(account));
    }

    private static async Task<IResult> RenameAccount(
        string id,
        RenameRequest? request,
        MasterDataAdmin admin,
        IMasterDataRepository repository,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new LarioDomainException(
                "Antrag fehlt: PUT /accounts/{id}/name benötigt einen JSON-Körper (name).");
        }

        await admin.RenameAccountAsync(id, request.Name ?? string.Empty, cancellationToken);
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        return Results.Ok(ToDto(FindAccountOrThrow(catalog, id)));
    }

    private static async Task<IResult> ArchiveAccount(
        string id,
        MasterDataAdmin admin,
        IMasterDataRepository repository,
        CancellationToken cancellationToken)
    {
        await admin.ArchiveAccountAsync(id, cancellationToken);
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        return Results.Ok(ToDto(FindAccountOrThrow(catalog, id)));
    }

    private static async Task<IResult> UnarchiveAccount(
        string id,
        MasterDataAdmin admin,
        IMasterDataRepository repository,
        CancellationToken cancellationToken)
    {
        await admin.UnarchiveAccountAsync(id, cancellationToken);
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        return Results.Ok(ToDto(FindAccountOrThrow(catalog, id)));
    }

    // ---------- Pools ----------

    private static async Task<IResult> GetPools(
        IMasterDataRepository repository, CancellationToken cancellationToken)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        var dtos = catalog.Pools
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .Select(ToDto)
            .ToList();
        return Results.Ok(dtos);
    }

    private static async Task<IResult> GetPool(
        string id, IMasterDataRepository repository, CancellationToken cancellationToken)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        var pool = catalog.FindPool(id)
            ?? throw new InvalidReferenceException($"Unbekannter Pool '{id}'.");
        return Results.Ok(ToDto(pool));
    }

    private static async Task<IResult> CreatePool(
        CreatePoolRequest? request,
        MasterDataAdmin admin,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new LarioDomainException(
                "Antrag fehlt: POST /pools benötigt einen JSON-Körper (name).");
        }

        var pool = await admin.CreatePoolAsync(NewId("pool"), request.Name ?? string.Empty, cancellationToken);
        return Results.Ok(ToDto(pool));
    }

    private static async Task<IResult> RenamePool(
        string id,
        RenameRequest? request,
        MasterDataAdmin admin,
        IMasterDataRepository repository,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new LarioDomainException(
                "Antrag fehlt: PUT /pools/{id}/name benötigt einen JSON-Körper (name).");
        }

        await admin.RenamePoolAsync(id, request.Name ?? string.Empty, cancellationToken);
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        var pool = catalog.FindPool(id)
            ?? throw new InvalidReferenceException($"Unbekannter Pool '{id}'.");
        return Results.Ok(ToDto(pool));
    }

    private static async Task<IResult> ArchivePool(
        string id,
        MasterDataAdmin admin,
        IMasterDataRepository repository,
        CancellationToken cancellationToken)
    {
        await admin.ArchivePoolAsync(id, cancellationToken);
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        var pool = catalog.FindPool(id)
            ?? throw new InvalidReferenceException($"Unbekannter Pool '{id}'.");
        return Results.Ok(ToDto(pool));
    }

    private static async Task<IResult> UnarchivePool(
        string id,
        MasterDataAdmin admin,
        IMasterDataRepository repository,
        CancellationToken cancellationToken)
    {
        await admin.UnarchivePoolAsync(id, cancellationToken);
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        var pool = catalog.FindPool(id)
            ?? throw new InvalidReferenceException($"Unbekannter Pool '{id}'.");
        return Results.Ok(ToDto(pool));
    }

    // ---------- Kategorien ----------

    private static async Task<IResult> GetCategories(
        IMasterDataRepository repository, CancellationToken cancellationToken)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        var dtos = catalog.Categories
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => ToDto(c, catalog))
            .ToList();
        return Results.Ok(dtos);
    }

    private static async Task<IResult> GetCategory(
        string id, IMasterDataRepository repository, CancellationToken cancellationToken)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        return Results.Ok(ToDto(FindCategoryOrThrow(catalog, id), catalog));
    }

    private static async Task<IResult> CreateCategory(
        CreateCategoryRequest? request,
        MasterDataAdmin admin,
        IMasterDataRepository repository,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new LarioDomainException(
                "Antrag fehlt: POST /categories benötigt einen JSON-Körper (name).");
        }

        var category = await admin.CreateCategoryAsync(
            NewId("cat"), request.Name ?? string.Empty, request.ParentId, cancellationToken);

        // Katalog laden, um die wirksame Steuer-Voreinstellung (ggf.
        // von der übergeordneten Kategorie geerbt) für die Antwort
        // auflösen zu können.
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        return Results.Ok(ToDto(FindCategoryOrThrow(catalog, category.Id), catalog));
    }

    private static async Task<IResult> RenameCategory(
        string id,
        RenameRequest? request,
        MasterDataAdmin admin,
        IMasterDataRepository repository,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new LarioDomainException(
                "Antrag fehlt: PUT /categories/{id}/name benötigt einen JSON-Körper (name).");
        }

        await admin.RenameCategoryAsync(id, request.Name ?? string.Empty, cancellationToken);
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        return Results.Ok(ToDto(FindCategoryOrThrow(catalog, id), catalog));
    }

    private static async Task<IResult> ArchiveCategory(
        string id,
        MasterDataAdmin admin,
        IMasterDataRepository repository,
        CancellationToken cancellationToken)
    {
        await admin.ArchiveCategoryAsync(id, cancellationToken);
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        return Results.Ok(ToDto(FindCategoryOrThrow(catalog, id), catalog));
    }

    private static async Task<IResult> UnarchiveCategory(
        string id,
        MasterDataAdmin admin,
        IMasterDataRepository repository,
        CancellationToken cancellationToken)
    {
        await admin.UnarchiveCategoryAsync(id, cancellationToken);
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        return Results.Ok(ToDto(FindCategoryOrThrow(catalog, id), catalog));
    }

    private static async Task<IResult> SetTaxPreset(
        string id,
        TaxPresetRequest? request,
        MasterDataAdmin admin,
        IMasterDataRepository repository,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new LarioDomainException(
                "Antrag fehlt: PUT /categories/{id}/tax-preset benötigt einen JSON-Körper.");
        }

        var preset = await admin.SetTaxPresetAsync(
            id, request.TaxRelevant, request.TaxCategoryId,
            request.InheritToChildren ?? false, cancellationToken);

        // Katalog laden, um den Steuerkategorie-Namen für die Antwort
        // auflösen zu können (die Verwaltung gibt nur die Voreinstellung
        // zurück).
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        return Results.Ok(ToPresetDto(preset, catalog));
    }

    private static async Task<IResult> RemoveTaxPreset(
        string id, MasterDataAdmin admin, CancellationToken cancellationToken)
    {
        await admin.RemoveTaxPresetAsync(id, cancellationToken);
        return Results.NoContent();
    }

    // ---------- Steuerkategorien ----------

    private static async Task<IResult> GetTaxCategories(
        IMasterDataRepository repository, CancellationToken cancellationToken)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        var dtos = catalog.TaxCategories
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .Select(ToDto)
            .ToList();
        return Results.Ok(dtos);
    }

    private static async Task<IResult> GetTaxCategory(
        string id, IMasterDataRepository repository, CancellationToken cancellationToken)
    {
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        var taxCategory = catalog.FindTaxCategory(id)
            ?? throw new InvalidReferenceException($"Unbekannte Steuerkategorie '{id}'.");
        return Results.Ok(ToDto(taxCategory));
    }

    private static async Task<IResult> CreateTaxCategory(
        CreateTaxCategoryRequest? request,
        MasterDataAdmin admin,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new LarioDomainException(
                "Antrag fehlt: POST /tax-categories benötigt einen JSON-Körper (name).");
        }

        var taxCategory = await admin.CreateTaxCategoryAsync(
            NewId("taxcat"), request.Name ?? string.Empty, request.ParentId, cancellationToken);
        return Results.Ok(ToDto(taxCategory));
    }

    private static async Task<IResult> RenameTaxCategory(
        string id,
        RenameRequest? request,
        MasterDataAdmin admin,
        IMasterDataRepository repository,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new LarioDomainException(
                "Antrag fehlt: PUT /tax-categories/{id}/name benötigt einen JSON-Körper (name).");
        }

        await admin.RenameTaxCategoryAsync(id, request.Name ?? string.Empty, cancellationToken);
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        var taxCategory = catalog.FindTaxCategory(id)
            ?? throw new InvalidReferenceException($"Unbekannte Steuerkategorie '{id}'.");
        return Results.Ok(ToDto(taxCategory));
    }

    private static async Task<IResult> ArchiveTaxCategory(
        string id,
        MasterDataAdmin admin,
        IMasterDataRepository repository,
        CancellationToken cancellationToken)
    {
        await admin.ArchiveTaxCategoryAsync(id, cancellationToken);
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        var taxCategory = catalog.FindTaxCategory(id)
            ?? throw new InvalidReferenceException($"Unbekannte Steuerkategorie '{id}'.");
        return Results.Ok(ToDto(taxCategory));
    }

    private static async Task<IResult> UnarchiveTaxCategory(
        string id,
        MasterDataAdmin admin,
        IMasterDataRepository repository,
        CancellationToken cancellationToken)
    {
        await admin.UnarchiveTaxCategoryAsync(id, cancellationToken);
        var catalog = await repository.LoadCatalogAsync(cancellationToken);
        var taxCategory = catalog.FindTaxCategory(id)
            ?? throw new InvalidReferenceException($"Unbekannte Steuerkategorie '{id}'.");
        return Results.Ok(ToDto(taxCategory));
    }

    // ---------- Mappings (DTO ↔ Domain) ----------

    private static Account FindAccountOrThrow(MasterDataCatalog catalog, string id) =>
        catalog.FindAccount(id)
            ?? throw new InvalidReferenceException($"Unbekanntes Konto '{id}'.");

    private static Category FindCategoryOrThrow(MasterDataCatalog catalog, string id) =>
        catalog.FindCategory(id)
            ?? throw new InvalidReferenceException($"Unbekannte Kategorie '{id}'.");

    private static AccountDto ToDto(Account account) => new(
        account.Id,
        account.Name,
        AccountTypeToString(account.Type),
        account.IsInterestBearing,
        StatusToString(account.Status),
        account.BaseRevision);

    private static PoolDto ToDto(Pool pool) => new(
        pool.Id,
        pool.Name,
        StatusToString(pool.Status),
        pool.BaseRevision);

    private static CategoryDto ToDto(Category category, MasterDataCatalog catalog)
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

    private static TaxCategoryDto ToDto(TaxCategory taxCategory) => new(
        taxCategory.Id,
        taxCategory.Name,
        taxCategory.ParentId,
        StatusToString(taxCategory.Status),
        taxCategory.BaseRevision);

    private static TaxPresetDto ToPresetDto(TaxPreset preset, MasterDataCatalog catalog) => new(
        preset.TaxRelevant,
        preset.TaxCategoryId,
        preset.TaxCategoryId is { } id ? catalog.FindTaxCategory(id)?.Name : null,
        preset.InheritToChildren,
        preset.BaseRevision);

    // ---------- Protokoll-Hilfen (keine Fachlogik) ----------

    /// <summary>
    /// Erzeugt hostseitige Stammdaten-Ids (GUID). Das Design legt keine
    /// Client-Ids für Stammdaten fest (anders als bei Buchungen, T4).
    /// </summary>
    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static string AccountTypeToString(AccountType type) => type switch
    {
        AccountType.Bank => "bank",
        AccountType.Savings => "savings",
        _ => throw new LarioDomainException($"Unbekannter Kontotyp '{type}'.")
    };

    private static AccountType ParseAccountType(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new LarioDomainException(
                "Kontotyp fehlt; erlaubt sind 'bank' und 'savings'.")
            : value.Trim().ToLowerInvariant() switch
            {
                "bank" => AccountType.Bank,
                "savings" => AccountType.Savings,
                _ => throw new LarioDomainException(
                    $"Kontotyp '{value}' ist unbekannt; erlaubt sind 'bank' und 'savings'.")
            };

    private static string StatusToString(EntityStatus status) => status switch
    {
        EntityStatus.Active => "active",
        EntityStatus.Archived => "archived",
        _ => throw new LarioDomainException($"Unbekannter Status '{status}'.")
    };
}
