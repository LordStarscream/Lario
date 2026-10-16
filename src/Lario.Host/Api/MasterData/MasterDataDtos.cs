namespace Lario.Host.Api.MasterData;

/// <summary>
/// Stammdaten-DTOs (Schritt 7, T2): reine Transportformen für die
/// Endpunkte <c>/api/v1/accounts|pools|categories|tax-categories</c>
/// (Design §2.4). Die Fachlogik liegt in <c>MasterDataAdmin</c> /
/// <c>MasterDataCatalog</c>; hier werden nur primitive JSON-Typen
/// übertragen (Money-Regel D6 greift hier nicht — es gibt keine Beträge).
/// Enumerations werden als stabile Strings übertragen:
/// Kontotyp <c>bank|savings</c>, Status <c>active|archived</c>.
/// </summary>

/// <summary>Konto (Rechenvertrag §3.1).</summary>
public sealed record AccountDto(
    string Id,
    string Name,
    string Type,
    bool IsInterestBearing,
    string Status,
    int BaseRevision);

/// <summary>Pool (Zweck; Rechenvertrag §3.1).</summary>
public sealed record PoolDto(
    string Id,
    string Name,
    string Status,
    int BaseRevision);

/// <summary>
/// Alltagskategorie (Rechenvertrag §3.1). <see cref="TaxPreset"/> trägt
/// die <b>wirksame</b> Steuer-Voreinstellung (eigene oder geerbt) für
/// die UI-Vorbelegung (Design §3); null = keine wirksame Voreinstellung.
/// </summary>
public sealed record CategoryDto(
    string Id,
    string Name,
    string? ParentId,
    string Status,
    int BaseRevision,
    TaxPresetDto? TaxPreset);

/// <summary>Steuerkategorie (eigener Baum; Rechenvertrag §3.1).</summary>
public sealed record TaxCategoryDto(
    string Id,
    string Name,
    string? ParentId,
    string Status,
    int BaseRevision);

/// <summary>
/// Wirksame Steuer-Voreinstellung einer Kategorie (Vererbung nach
/// <c>TaxPreset</c>-Semantik). <see cref="TaxRelevant"/> null = nicht
/// festgelegt (keine Markierung); <see cref="TaxCategoryId"/> null =
/// keine gewählt. <see cref="TaxCategoryName"/> dient der Anzeige.
/// </summary>
public sealed record TaxPresetDto(
    bool? TaxRelevant,
    string? TaxCategoryId,
    string? TaxCategoryName,
    bool InheritToChildren,
    int BaseRevision);

/// <summary>Anlage Konto: Name, Typ (<c>bank|savings</c>), zinsbringend (Default false).</summary>
public sealed record CreateAccountRequest(
    string? Name,
    string? Type,
    bool? IsInterestBearing);

/// <summary>Anlage Pool: Name.</summary>
public sealed record CreatePoolRequest(string? Name);

/// <summary>Anlage Kategorie: Name, optional übergeordnete Kategorie.</summary>
public sealed record CreateCategoryRequest(string? Name, string? ParentId);

/// <summary>Anlage Steuerkategorie: Name, optional übergeordnete Steuerkategorie.</summary>
public sealed record CreateTaxCategoryRequest(string? Name, string? ParentId);

/// <summary>Umbenennen (alle vier Stammdatenarten): neuer Name.</summary>
public sealed record RenameRequest(string? Name);

/// <summary>
/// Steuer-Voreinstellung setzen (<c>PUT /categories/{id}/tax-preset</c>).
/// <see cref="TaxRelevant"/> null = nicht festgelegt;
/// <see cref="TaxCategoryId"/> null = keine gewählt;
/// <see cref="InheritToChildren"/> null = keine Vererbung (Default false).
/// </summary>
public sealed record TaxPresetRequest(
    bool? TaxRelevant,
    string? TaxCategoryId,
    bool? InheritToChildren);
