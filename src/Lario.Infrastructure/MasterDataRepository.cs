using Lario.Domain.MasterData;
using Lario.Application;
using Microsoft.EntityFrameworkCore;

namespace Lario.Infrastructure;

/// <summary>
/// EF-Core/SQLite-Implementierung der Stammdaten-Zugriffe (Schritt 3).
/// Änderungen werden als Kopie in die getrackte Entität übernommen und
/// mit <c>SaveChanges</c> persistiert; bei einem Fehler bleibt der
/// vorherige Stand erhalten.
/// </summary>
public sealed class MasterDataRepository(LarioDbContext db) : IMasterDataRepository
{
    public async Task<MasterDataCatalog> LoadCatalogAsync(CancellationToken cancellationToken = default)
    {
        // SQLite garantiert keine Zeilenreihenfolge — der Catalog trägt die
        // kanonische Id-Ordnung (Ordinal), damit alle Konsumenten (u. a.
        // Balances, Setzungspositionen) deterministisch iterieren. Die
        // Anzeigenamen-Sortierung der Leseendpunkte bleibt unverändert.
        var accounts = await db.Accounts.AsNoTracking()
            .OrderBy(a => a.Id).ToListAsync(cancellationToken);
        var pools = await db.Pools.AsNoTracking()
            .OrderBy(p => p.Id).ToListAsync(cancellationToken);
        var categories = await db.Categories.AsNoTracking()
            .OrderBy(c => c.Id).ToListAsync(cancellationToken);
        var taxCategories = await db.TaxCategories.AsNoTracking()
            .OrderBy(t => t.Id).ToListAsync(cancellationToken);
        var presets = await db.TaxPresets.AsNoTracking()
            .OrderBy(p => p.CategoryId).ToListAsync(cancellationToken);

        // Steuer-Voreinstellungen an ihre Kategorien hängen (1:1).
        foreach (var preset in presets)
        {
            categories.FirstOrDefault(c => c.Id == preset.CategoryId)?.TaxPreset = preset;
        }

        return new MasterDataCatalog(accounts, pools, categories, taxCategories);
    }

    public async Task SaveAccountAsync(Account account, CancellationToken cancellationToken = default)
    {
        var tracked = await db.Accounts.FirstOrDefaultAsync(a => a.Id == account.Id, cancellationToken);
        if (tracked is null)
        {
            db.Accounts.Add(account);
        }
        else
        {
            tracked.Name = account.Name;
            tracked.Type = account.Type;
            tracked.IsInterestBearing = account.IsInterestBearing;
            tracked.Status = account.Status;
            tracked.BaseRevision = account.BaseRevision;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SavePoolAsync(Pool pool, CancellationToken cancellationToken = default)
    {
        var tracked = await db.Pools.FirstOrDefaultAsync(p => p.Id == pool.Id, cancellationToken);
        if (tracked is null)
        {
            db.Pools.Add(pool);
        }
        else
        {
            tracked.Name = pool.Name;
            tracked.Status = pool.Status;
            tracked.BaseRevision = pool.BaseRevision;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveCategoryAsync(Category category, CancellationToken cancellationToken = default)
    {
        var tracked = await db.Categories.FirstOrDefaultAsync(c => c.Id == category.Id, cancellationToken);
        if (tracked is null)
        {
            db.Categories.Add(new Category
            {
                Id = category.Id,
                Name = category.Name,
                ParentId = category.ParentId,
                Status = category.Status,
                BaseRevision = category.BaseRevision,
            });
        }
        else
        {
            tracked.Name = category.Name;
            tracked.ParentId = category.ParentId;
            tracked.Status = category.Status;
            tracked.BaseRevision = category.BaseRevision;
        }

        await SavePresetAsync(category, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveTaxCategoryAsync(TaxCategory taxCategory, CancellationToken cancellationToken = default)
    {
        var tracked = await db.TaxCategories.FirstOrDefaultAsync(t => t.Id == taxCategory.Id, cancellationToken);
        if (tracked is null)
        {
            db.TaxCategories.Add(taxCategory);
        }
        else
        {
            tracked.Name = taxCategory.Name;
            tracked.ParentId = taxCategory.ParentId;
            tracked.Status = taxCategory.Status;
            tracked.BaseRevision = taxCategory.BaseRevision;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Verwaltet die 1:1-Steuer-Voreinstellung einer Kategorie: anlegen,
    /// aktualisieren oder — wenn die Kategorie keine Voreinstellung mehr
    /// trägt — die vorhandene Zeile entfernen.
    /// </summary>
    private async Task SavePresetAsync(Category category, CancellationToken cancellationToken)
    {
        var existing = await db.TaxPresets
            .FirstOrDefaultAsync(p => p.CategoryId == category.Id, cancellationToken);

        if (category.TaxPreset is null)
        {
            if (existing is not null)
            {
                db.TaxPresets.Remove(existing);
            }

            return;
        }

        if (existing is null)
        {
            db.TaxPresets.Add(new TaxPreset
            {
                CategoryId = category.Id,
                TaxRelevant = category.TaxPreset.TaxRelevant,
                TaxCategoryId = category.TaxPreset.TaxCategoryId,
                InheritToChildren = category.TaxPreset.InheritToChildren,
                BaseRevision = category.TaxPreset.BaseRevision,
            });
        }
        else
        {
            existing.TaxRelevant = category.TaxPreset.TaxRelevant;
            existing.TaxCategoryId = category.TaxPreset.TaxCategoryId;
            existing.InheritToChildren = category.TaxPreset.InheritToChildren;
            existing.BaseRevision = category.TaxPreset.BaseRevision;
        }
    }
}
