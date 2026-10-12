using Lario.Domain;

namespace Lario.Domain.MasterData;

/// <summary>
/// Stammdaten-Katalog (Schritt 3): Konten, Pools, Kategorien/Unterkategorien,
/// Steuerkategorien/Unterkategorien und vererbbare Steuer-Voreinstellungen.
///
/// Der Katalog ist der einzige Weg, die Stammdaten zu verändern: Alle
/// Invarianten werden hier geprüft — Zyklusfreiheit der Kategoriebäume,
/// gültige Referenzen, Archivierungsregeln. Ein fehlerhafter Aufruf wirft
/// und ändert den Katalog nicht (atomar auf Anwendungsebene).
/// </summary>
public sealed class MasterDataCatalog
{
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pool> _pools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Category> _categories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaxCategory> _taxCategories = new(StringComparer.Ordinal);

    public IReadOnlyCollection<Account> Accounts => _accounts.Values;
    public IReadOnlyCollection<Pool> Pools => _pools.Values;
    public IReadOnlyCollection<Category> Categories => _categories.Values;
    public IReadOnlyCollection<TaxCategory> TaxCategories => _taxCategories.Values;

    public Account? FindAccount(string id) => _accounts.GetValueOrDefault(id);
    public Pool? FindPool(string id) => _pools.GetValueOrDefault(id);
    public Category? FindCategory(string id) => _categories.GetValueOrDefault(id);
    public TaxCategory? FindTaxCategory(string id) => _taxCategories.GetValueOrDefault(id);

    public TaxPreset? FindPreset(string categoryId) =>
        _categories.GetValueOrDefault(categoryId)?.TaxPreset;

    /// <summary>
    /// Hydratierungs-Konstruktor: baut den Katalog aus bereits persistent
    /// gespeicherten Entitäten (z. B. aus der Datenbank). Die TaxPresets
    /// hängen bereits an ihren Kategorien (<c>Category.TaxPreset</c>);
    /// die Invarianten werden hier nicht erneut geprüft.
    /// </summary>
    public MasterDataCatalog(
        IEnumerable<Account> accounts,
        IEnumerable<Pool> pools,
        IEnumerable<Category> categories,
        IEnumerable<TaxCategory> taxCategories)
    {
        foreach (var account in accounts)
        {
            _accounts[account.Id] = account;
        }

        foreach (var pool in pools)
        {
            _pools[pool.Id] = pool;
        }

        foreach (var category in categories)
        {
            _categories[category.Id] = category;
        }

        foreach (var taxCategory in taxCategories)
        {
            _taxCategories[taxCategory.Id] = taxCategory;
        }
    }

    // ------------------------------------------------------------------
    // Anlage
    // ------------------------------------------------------------------

    public Account CreateAccount(string id, string name, AccountType type, bool isInterestBearing = false)
    {
        RequireNonEmpty(id, "Konto-ID");
        RequireNonEmpty(name, "Kontoname");
        var account = new Account
        {
            Id = id,
            Name = name.Trim(),
            Type = type,
            IsInterestBearing = isInterestBearing,
            Status = EntityStatus.Active,
            BaseRevision = 1,
        };
        account.ThrowIfInvalidId();
        if (!_accounts.TryAdd(id, account))
            throw new DomainConflictException($"Konto mit Id '{id}' existiert bereits.");
        return account;
    }

    public Pool CreatePool(string id, string name)
    {
        RequireNonEmpty(id, "Pool-ID");
        RequireNonEmpty(name, "Pool-Name");
        var pool = new Pool { Id = id, Name = name.Trim(), Status = EntityStatus.Active, BaseRevision = 1 };
        if (!_pools.TryAdd(id, pool))
            throw new DomainConflictException($"Pool mit Id '{id}' existiert bereits.");
        return pool;
    }

    public Category CreateCategory(string id, string name, string? parentId = null)
    {
        RequireNonEmpty(id, "Kategorie-ID");
        RequireNonEmpty(name, "Kategoriename");
        if (parentId is not null)
        {
            ValidateTreeParent(id, parentId, FindCategory, "Kategorie");
        }

        var category = new Category
        {
            Id = id,
            Name = name.Trim(),
            ParentId = parentId,
            Status = EntityStatus.Active,
            BaseRevision = 1,
        };
        if (!_categories.TryAdd(id, category))
            throw new DomainConflictException($"Kategorie mit Id '{id}' existiert bereits.");
        return category;
    }

    public TaxCategory CreateTaxCategory(string id, string name, string? parentId = null)
    {
        RequireNonEmpty(id, "Steuerkategorie-ID");
        RequireNonEmpty(name, "Steuerkategorie-Name");
        if (parentId is not null)
        {
            ValidateTreeParent(id, parentId, FindTaxCategory, "Steuerkategorie");
        }

        var taxCategory = new TaxCategory
        {
            Id = id,
            Name = name.Trim(),
            ParentId = parentId,
            Status = EntityStatus.Active,
            BaseRevision = 1,
        };
        if (!_taxCategories.TryAdd(id, taxCategory))
            throw new DomainConflictException($"Steuerkategorie mit Id '{id}' existiert bereits.");
        return taxCategory;
    }

    // ------------------------------------------------------------------
    // Steuer-Voreinstellungen
    // ------------------------------------------------------------------

    /// <summary>
    /// Setzt die explizite Steuer-Voreinstellung einer Kategorie (1:1).
    /// Eine bestehende Voreinstellung wird ersetzt und die Revision erhöht.
    /// </summary>
    public TaxPreset SetTaxPreset(
        string categoryId,
        bool? taxRelevant,
        string? taxCategoryId,
        bool inheritToChildren)
    {
        var category = RequireActiveCategory(categoryId);
        if (taxCategoryId is not null)
        {
            var taxCategory = FindTaxCategory(taxCategoryId)
                ?? throw new InvalidReferenceException(
                    $"Steuer-Voreinstellung von '{categoryId}' referenziert unbekannte Steuerkategorie '{taxCategoryId}'.");

            if (taxCategory.Status != EntityStatus.Active)
            {
                throw new InvalidReferenceException(
                    $"Steuer-Voreinstellung von '{categoryId}' referenziert archiverte Steuerkategorie '{taxCategoryId}'.");
            }
        }

        var existing = category.TaxPreset;
        if (existing is null)
        {
            existing = new TaxPreset
            {
                CategoryId = categoryId,
                TaxRelevant = taxRelevant,
                TaxCategoryId = taxCategoryId,
                InheritToChildren = inheritToChildren,
                BaseRevision = 1,
            };
            category.TaxPreset = existing;
        }
        else
        {
            existing.TaxRelevant = taxRelevant;
            existing.TaxCategoryId = taxCategoryId;
            existing.InheritToChildren = inheritToChildren;
            existing.BaseRevision++;
        }

        return existing;
    }

    /// <summary>
    /// Entfernt die explizite Voreinstellung einer Kategorie; die wirksame
    /// Voreinstellung ergibt sich dann wieder aus der Vererbung.
    /// </summary>
    public void RemoveTaxPreset(string categoryId)
    {
        var category = RequireActiveCategory(categoryId);
        if (category.TaxPreset is null)
        {
            throw new LarioDomainException($"Kategorie '{categoryId}' hat keine Steuer-Voreinstellung.");
        }

        category.TaxPreset = null;
    }

    /// <summary>
    /// Wirksame Steuer-Voreinstellung einer Kategorie (Vererbung nach
    /// <see cref="TaxPreset"/>-Dokumentation):
    /// - eigene explizite Voreinstellung gewinnt immer (auch explizites Nein);
    /// - sonst erbt die Kategorie von der nächsten Vorfahrin mit expliziter
    ///   Voreinstellung — aber nur, wenn deren <c>InheritToChildren</c>
    ///   gesetzt ist (Vererbung wirkt bereichsweit, auch über mehrere Ebenen
    ///   ohne eigene Voreinstellung hinweg);
    /// - eine Kategorie mit eigener Voreinstellung blockiert die Vererbung
    ///   aus höheren Ebenen für sich selbst und — wenn sie nicht vererbt —
    ///   für ihre Untergruppen.
    /// null, wenn im Pfad keine anwendbare explizite Voreinstellung liegt.
    /// </summary>
    public TaxPreset? ResolveEffectivePreset(string categoryId)
    {
        var current = FindCategory(categoryId)
            ?? throw new InvalidReferenceException($"Unbekannte Kategorie '{categoryId}'.");

        while (current is not null)
        {
            if (current.TaxPreset is not null)
            {
                // Explizite Voreinstellung auf dieser Ebene. Für die Kategorie
                // selbst gilt sie immer; für Nachkommen nur mit InheritToChildren.
                return current.Id == categoryId || current.TaxPreset.InheritToChildren
                    ? current.TaxPreset
                    : null;
            }

            if (current.ParentId is null)
            {
                return null;
            }

            current = FindCategory(current.ParentId);
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Archivierung
    // ------------------------------------------------------------------

    /// <summary>
    /// Archiviert eine Stammdaten-Entität (statt Löschung). Die Entität
    /// bleibt historisch nachvollziehbar, ist aber nicht mehr als Referenz
    /// für neue Daten verwendbar. Die Revision wird um 1 erhöht.
    /// </summary>
    public void ArchiveAccount(string id)
    {
        var account = _accounts.GetValueOrDefault(id)
            ?? throw new InvalidReferenceException($"Unbekanntes Konto '{id}'.");
        Archive(account);
    }

    public void ArchivePool(string id)
    {
        var pool = _pools.GetValueOrDefault(id)
            ?? throw new InvalidReferenceException($"Unbekannter Pool '{id}'.");
        Archive(pool);
    }

    public void ArchiveCategory(string id)
    {
        var category = _categories.GetValueOrDefault(id)
            ?? throw new InvalidReferenceException($"Unbekannte Kategorie '{id}'.");
        Archive(category);
    }

    /// <summary>
    /// Archiviert eine Steuerkategorie. Blockiert wird sie nur durch
    /// <b>aktive</b> Kategorien, deren explizite Voreinstellung sie
    /// referenziert: Diese Verweise sind aktuelle Konfiguration und müssen
    /// erst aufgelöst werden (Voreinstellung ändern oder entfernen).
    /// Voreinstellungen <b>archivierter</b> Kategorien sind historische
    /// Verweise: Sie bleiben unverändert erhalten (keine Löschung
    /// historischer Daten) und blockieren die Archivierung nicht.
    /// </summary>
    public void ArchiveTaxCategory(string id)
    {
        var taxCategory = _taxCategories.GetValueOrDefault(id)
            ?? throw new InvalidReferenceException($"Unbekannte Steuerkategorie '{id}'.");

        foreach (var category in _categories.Values)
        {
            if (category.Status == EntityStatus.Active
                && category.TaxPreset?.TaxCategoryId == id)
            {
                throw new LarioDomainException(
                    $"Steuerkategorie '{id}' wird von der Steuer-Voreinstellung der " +
                    $"aktiven Kategorie '{category.Id}' referenziert und kann nicht " +
                    "archiviert werden. Die Voreinstellung muss geändert oder entfernt werden.");
            }
        }

        Archive(taxCategory);
    }

    private static void Archive(MasterDataEntity entity)
    {
        if (entity.Status == EntityStatus.Archived)
        {
            throw new LarioDomainException($"Entität '{entity.Id}' ist bereits archiviert.");
        }

        entity.Status = EntityStatus.Archived;
        entity.BaseRevision++;
    }

    // ------------------------------------------------------------------
    // Interne Prüfungen
    // ------------------------------------------------------------------

    /// <summary>
    /// Prüft eine neue Eltern-Kind-Beziehung: Das Elternteil muss existieren,
    /// aktiv sein und darf kein Nachkomme des Kindes sein (Zyklus). Bei
    /// Neuanlagen hat das Kind noch keine Kinder, der einzige mögliche
    /// Zyklus ist die Selbstreferenz.
    /// </summary>
    private static void ValidateTreeParent<T>(
        string childId,
        string parentId,
        Func<string, T?> findEntity,
        string entityType)
        where T : MasterDataEntity
    {
        if (parentId == childId)
        {
            throw new CycleDetectedException(
                $"{entityType} '{childId}' kann nicht eigene Untergruppe sein (Zyklus).");
        }

        var parent = findEntity(parentId)
            ?? throw new InvalidReferenceException(
                $"{entityType} '{childId}' referenziert unbekanntes Elternteil '{parentId}'.");

        if (parent.Status != EntityStatus.Active)
        {
            throw new InvalidReferenceException(
                $"{entityType} '{childId}' kann nicht unter archiviertes Elternteil '{parentId}' angelegt werden.");
        }
    }

    private Category RequireActiveCategory(string categoryId)
    {
        var category = FindCategory(categoryId)
            ?? throw new InvalidReferenceException($"Unbekannte Kategorie '{categoryId}'.");
        if (category.Status != EntityStatus.Active)
        {
            throw new InvalidReferenceException(
                $"Kategorie '{categoryId}' ist archiviert und kann keine neue Voreinstellung tragen.");
        }

        return category;
    }

    private static void RequireNonEmpty(string? value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new LarioDomainException($"{what} fehlt oder ist leer.");
        }
    }
}
