using Lario.Domain;
using Lario.Domain.MasterData;

namespace Lario.Domain.Tests;

/// <summary>
/// Invarianten des Stammdaten-Katalogs (Schritt 3): Anlage, Bäume ohne
/// Zyklen, Archivierungsregeln und vererbbare Steuer-Voreinstellungen.
/// </summary>
public class MasterDataCatalogTests
{
    [Fact(DisplayName = "Konto und Pool können angelegt werden; doppelte Id wird abgelehnt")]
    public void Create_DuplicateId_IsRejected()
    {
        var catalog = new MasterDataCatalog([], [], [], []);

        var account = catalog.CreateAccount("acc-haupt", "Hauptkonto", AccountType.Bank);
        var pool = catalog.CreatePool("pool-flex", "Flex");

        Assert.Equal("acc-haupt", account.Id);
        Assert.Equal(EntityStatus.Active, account.Status);
        Assert.Equal(1, account.BaseRevision);
        Assert.Equal("pool-flex", pool.Id);

        Assert.Throws<DomainConflictException>(() => catalog.CreateAccount("acc-haupt", "Doppel", AccountType.Bank));
        Assert.Throws<DomainConflictException>(() => catalog.CreatePool("pool-flex", "Doppel"));
    }

    [Fact(DisplayName = "Kategorie mit unbekanntem oder archiviertem Elternteil wird abgelehnt")]
    public void CreateCategory_InvalidParent_IsRejected()
    {
        var catalog = new MasterDataCatalog([], [], [], []);

        Assert.Throws<InvalidReferenceException>(
            () => catalog.CreateCategory("cat-x", "X", parentId: "cat-unbekannt"));

        catalog.CreateCategory("cat-wohnung", "Wohnung");
        catalog.ArchiveCategory("cat-wohnung");
        Assert.Throws<InvalidReferenceException>(
            () => catalog.CreateCategory("cat-miete", "Miete", parentId: "cat-wohnung"));
    }

    [Fact(DisplayName = "Kategoriezyklus (Selbstreferenz) wird abgelehnt")]
    public void CreateCategory_SelfReference_IsCycle()
    {
        var catalog = new MasterDataCatalog([], [], [], []);
        Assert.Throws<CycleDetectedException>(
            () => catalog.CreateCategory("cat-x", "X", parentId: "cat-x"));
    }

    [Fact(DisplayName = "Archivierte Entität kann nicht erneut archiviert werden")]
    public void Archive_AlreadyArchived_IsRejected()
    {
        var catalog = new MasterDataCatalog([], [], [], []);
        catalog.CreateAccount("acc-1", "Konto", AccountType.Bank);

        catalog.ArchiveAccount("acc-1");
        var archived = catalog.FindAccount("acc-1")!;
        Assert.Equal(EntityStatus.Archived, archived.Status);
        Assert.Equal(2, archived.BaseRevision);

        Assert.Throws<LarioDomainException>(() => catalog.ArchiveAccount("acc-1"));
        Assert.Throws<InvalidReferenceException>(() => catalog.ArchiveAccount("acc-unbekannt"));
    }

    [Fact(DisplayName = "Neue Daten dürfen archivierten Pool/Konto nicht referenzieren")]
    public void ArchivedEntities_CannotBeReferencedByNewData()
    {
        var catalog = new MasterDataCatalog([], [], [], []);
        catalog.CreateAccount("acc-1", "Konto", AccountType.Bank);
        catalog.CreatePool("pool-1", "Pool");
        catalog.ArchiveAccount("acc-1");
        catalog.ArchivePool("pool-1");

        var spec = new Journal.OpeningBalanceSpec(
            "init-1",
            DateOnly.FromDateTime(new DateTime(2026, 1, 1)),
            "acc-1",
            Money.OneEuro,
            []);
        Assert.Throws<InvalidReferenceException>(() => spec.ValidateAgainst(catalog));

        var spec2 = new Journal.OpeningBalanceSpec(
            "init-1",
            DateOnly.FromDateTime(new DateTime(2026, 1, 1)),
            "acc-neu",
            Money.OneEuro,
            [new Journal.PoolAllocation("pool-1", Money.OneEuro)]);
        catalog.CreateAccount("acc-neu", "Neu", AccountType.Bank);
        Assert.Throws<InvalidReferenceException>(() => spec2.ValidateAgainst(catalog));
    }

    [Fact(DisplayName = "Steuerkategorie mit Voreinstellungs-Referenz kann nicht archiviert werden")]
    public void ArchiveTaxCategory_ReferencedByPreset_IsRejected()
    {
        var catalog = new MasterDataCatalog([], [], [], []);
        catalog.CreateTaxCategory("tax-1", "Steuern");
        catalog.CreateCategory("cat-1", "Kategorie");
        catalog.SetTaxPreset("cat-1", taxRelevant: true, taxCategoryId: "tax-1", inheritToChildren: true);

        Assert.Throws<LarioDomainException>(() => catalog.ArchiveTaxCategory("tax-1"));

        // Ohne Referenz geht die Archivierung.
        catalog.RemoveTaxPreset("cat-1");
        catalog.ArchiveTaxCategory("tax-1");
        Assert.Equal(EntityStatus.Archived, catalog.FindTaxCategory("tax-1")!.Status);
    }
}

/// <summary>
/// Vererbbare Steuer-Voreinstellungen (Schritt 3, Plan §9): explizite
/// Voreinstellung gewinnt, Vererbung nur mit InheritToChildren, ein
/// explizites Nein blockiert die Vererbung.
/// </summary>
public class TaxPresetInheritanceTests
{
    private static MasterDataCatalog BuildTree()
    {
        // cat-wurzel (preset: relevant, vererbt)
        //   cat-mittel (kein preset)
        //     cat-blatt
        //   cat-block (preset: irrelevant, nicht vererbt)
        //     cat-block-kind
        var catalog = new MasterDataCatalog([], [], [], []);
        catalog.CreateTaxCategory("tax-1", "Steuern");

        catalog.CreateCategory("cat-wurzel", "Wurzel");
        catalog.SetTaxPreset("cat-wurzel", true, "tax-1", inheritToChildren: true);

        catalog.CreateCategory("cat-mittel", "Mittel", parentId: "cat-wurzel");
        catalog.CreateCategory("cat-blatt", "Blatt", parentId: "cat-mittel");

        catalog.CreateCategory("cat-block", "Block", parentId: "cat-wurzel");
        catalog.SetTaxPreset("cat-block", false, null, inheritToChildren: false);
        catalog.CreateCategory("cat-block-kind", "BlockKind", parentId: "cat-block");

        return catalog;
    }

    [Fact(DisplayName = "Eigene explizite Voreinstellung gewinnt")]
    public void Resolve_OwnPreset_Wins()
    {
        var catalog = BuildTree();
        var preset = catalog.ResolveEffectivePreset("cat-wurzel");
        Assert.NotNull(preset);
        Assert.True(preset!.TaxRelevant);
        Assert.Equal("tax-1", preset.TaxCategoryId);
    }

    [Fact(DisplayName = "Vererbung über mehrere Ebenen ohne eigene Voreinstellung")]
    public void Resolve_InheritsAcrossLevels()
    {
        var catalog = BuildTree();
        var preset = catalog.ResolveEffectivePreset("cat-blatt");
        Assert.NotNull(preset);
        Assert.True(preset!.TaxRelevant);
        Assert.Equal("cat-wurzel", preset.CategoryId);
    }

    [Fact(DisplayName = "InheritToChildren=false: Voreinstellung bleibt auf der Ebene")]
    public void Resolve_NoInherit_StaysOnLevel()
    {
        var catalog = BuildTree();
        // cat-block selbst trägt die explizite Voreinstellung ...
        var own = catalog.ResolveEffectivePreset("cat-block");
        Assert.NotNull(own);
        Assert.False(own!.TaxRelevant);

        // ... aber vererbt sie nicht an das Kind.
        Assert.Null(catalog.ResolveEffectivePreset("cat-block-kind"));
    }

    [Fact(DisplayName = "Setzen ersetzt bestehende Voreinstellung und erhöht die Revision")]
    public void SetPreset_ReplacesAndBumpsRevision()
    {
        var catalog = new MasterDataCatalog([], [], [], []);
        catalog.CreateCategory("cat-1", "K");

        var first = catalog.SetTaxPreset("cat-1", null, null, inheritToChildren: true);
        Assert.Equal(1, first.BaseRevision);

        var second = catalog.SetTaxPreset("cat-1", true, null, inheritToChildren: false);
        Assert.Same(first, second);
        Assert.Equal(2, second.BaseRevision);
        Assert.True(second.TaxRelevant);

        catalog.RemoveTaxPreset("cat-1");
        Assert.Null(catalog.FindPreset("cat-1"));
        Assert.Throws<LarioDomainException>(() => catalog.RemoveTaxPreset("cat-1"));
    }

    [Fact(DisplayName = "Voreinstellung darf unbekannte oder archivierte Steuerkategorie nicht referenzieren")]
    public void SetPreset_InvalidTaxCategory_IsRejected()
    {
        var catalog = new MasterDataCatalog([], [], [], []);
        catalog.CreateCategory("cat-1", "K");
        catalog.CreateTaxCategory("tax-arch", "Archiviert");
        catalog.ArchiveTaxCategory("tax-arch");

        Assert.Throws<InvalidReferenceException>(
            () => catalog.SetTaxPreset("cat-1", true, taxCategoryId: "tax-unbekannt", inheritToChildren: true));
        Assert.Throws<InvalidReferenceException>(
            () => catalog.SetTaxPreset("cat-1", true, taxCategoryId: "tax-arch", inheritToChildren: true));
    }
}
