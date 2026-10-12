using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;

namespace Lario.Domain.Tests;

/// <summary>
/// Ausgabe (Schritt 4, Rechenvertrag §3.2/§3.5): Splits summieren exakt
/// zum Gesamtbetrag; tatsächliche Poolschuld entsteht sofort; optional
/// steuerrelevante Markierung je Split (ohne steuerliche Wirkung).
/// Referenzzahlen Plan §5 Schritt 4 „Prüfen" 2 und 4.
/// </summary>
public class ExpenseSpecTests
{
    private static readonly DateOnly Ausgabedatum = new(2026, 10, 12);

    private static MasterDataCatalog Catalog()
    {
        var catalog = new MasterDataCatalog([], [], [], []);
        catalog.CreateAccount("acc-haupt", "Hauptkonto", AccountType.Bank);
        catalog.CreateAccount("acc-altes", "Altkonto", AccountType.Bank);
        catalog.ArchiveAccount("acc-altes");
        catalog.CreatePool("pool-flex", "Flex");
        catalog.CreatePool("pool-freizeit", "Freizeit");
        catalog.CreatePool("pool-altes", "Alter Pool");
        catalog.ArchivePool("pool-altes");
        catalog.CreateCategory("cat-wohnen", "Wohnen");
        catalog.CreateCategory("cat-miete", "Miete", "cat-wohnen");
        catalog.CreateCategory("cat-freizeit", "Freizeit");
        catalog.CreateCategory("cat-kino", "Kino", "cat-freizeit");
        catalog.ArchiveCategory("cat-kino");
        catalog.CreateTaxCategory("tax-vat", "Umsatzsteuer");
        catalog.ArchiveTaxCategory("tax-vat");
        catalog.CreateTaxCategory("tax-irrsinn", "Irrsinn");
        return catalog;
    }

    private static ExpenseSplitSpec Split(
        string id, Money amount, string poolId, string categoryId, TaxMarking? tax = null) =>
        new() { Id = id, Amount = amount, PoolId = poolId, CategoryId = categoryId, Tax = tax };

    private static ExpenseSpec Spec(
        string id, Money total, IReadOnlyList<ExpenseSplitSpec> splits, string accountId = "acc-haupt") =>
        new()
        {
            Id = id,
            Date = Ausgabedatum,
            AccountId = accountId,
            TotalAmount = total,
            Splits = splits,
        };

    [Fact(DisplayName = "Plan-Kernfall: 85 € = 65 € Flex + 20 € Freizeit")]
    public void PlanCase_SplitsSumToTotal()
    {
        var catalog = Catalog();
        var spec = Spec("aus-1", Money.FromCents(8_500),
        [
            Split("s1", Money.FromCents(6_500), "pool-flex", "cat-miete"),
            Split("s2", Money.FromCents(2_000), "pool-freizeit", "cat-freizeit"),
        ]);
        spec.Validate(catalog);

        var booking = spec.ToBooking(catalog);
        Assert.Equal(BookingType.Expense, booking.Type);
        Assert.Single(booking.AccountPostings);
        Assert.Equal("acc-haupt", booking.AccountPostings[0].AccountId);
        Assert.Equal(Money.FromCents(-8_500), booking.AccountPostings[0].Amount);
        Assert.Equal(2, booking.Splits.Count);
        // Tatsächliche Poolschuld entsteht sofort: je Split ein negativer
        // Poolverbrauch, je Pool zusammengefasst.
        Assert.Equal(Money.FromCents(-6_500),
            booking.PoolPostings.Single(p => p.PoolId == "pool-flex").Amount);
        Assert.Equal(Money.FromCents(-2_000),
            booking.PoolPostings.Single(p => p.PoolId == "pool-freizeit").Amount);
        Assert.Null(booking.FundingMonth);
        Assert.All(booking.Splits, s => Assert.Null(s.Tax));
    }

    [Fact(DisplayName = "Plan-Referenz 4: Splits 84,99 € zu 85 € Gesamtbetrag werden abgelehnt")]
    public void PlanCase_SplitSumOffByOneCent_IsRejected()
    {
        var spec = Spec("aus-2", Money.FromCents(8_500),
        [
            Split("s1", Money.FromCents(6_500), "pool-flex", "cat-miete"),
            Split("s2", Money.FromCents(1_999), "pool-freizeit", "cat-freizeit"),
        ]);
        Assert.Throws<LarioDomainException>(() => spec.Validate(Catalog()));
    }

    [Theory]
    [InlineData(8_501)]
    [InlineData(8_499)]
    public void SplitSum_NotEqualTotal_IsRejected(long splitSumCents)
    {
        var spec = Spec("aus-3", Money.FromCents(8_500),
        [
            Split("s1", Money.FromCents(splitSumCents - 1_000), "pool-flex", "cat-miete"),
            Split("s2", Money.FromCents(1_000), "pool-freizeit", "cat-freizeit"),
        ]);
        Assert.Throws<LarioDomainException>(() => spec.Validate(Catalog()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-8_500)]
    public void TotalAmount_ZeroOrNegative_IsRejected(long cents)
    {
        var spec = Spec("aus-4", Money.FromCents(cents),
        [Split("s1", Money.FromCents(10_000), "pool-flex", "cat-miete")]);
        Assert.Throws<LarioDomainException>(() => spec.Validate(Catalog()));
    }

    [Fact(DisplayName = "Leere Splitliste wird abgelehnt")]
    public void EmptySplits_AreRejected()
    {
        var spec = Spec("aus-5", Money.FromCents(10_000), []);
        Assert.Throws<LarioDomainException>(() => spec.Validate(Catalog()));
    }

    [Fact(DisplayName = "Doppelte Split-ID wird abgelehnt")]
    public void DuplicateSplitIds_AreRejected()
    {
        var spec = Spec("aus-6", Money.FromCents(20_000),
        [
            Split("s1", Money.FromCents(10_000), "pool-flex", "cat-miete"),
            Split("s1", Money.FromCents(10_000), "pool-freizeit", "cat-freizeit"),
        ]);
        Assert.Throws<LarioDomainException>(() => spec.Validate(Catalog()));
    }

    [Theory]
    [InlineData("acc-geister")]
    [InlineData("acc-altes")]
    public void UnknownOrArchivedAccount_Throws(string accountId)
    {
        var spec = Spec("aus-7", Money.FromCents(10_000),
            [Split("s1", Money.FromCents(10_000), "pool-flex", "cat-miete")],
            accountId: accountId);
        Assert.Throws<InvalidReferenceException>(() => spec.Validate(Catalog()));
    }

    [Fact(DisplayName = "Archivierte Pools und Kategorien werden abgelehnt")]
    public void ArchivedReferences_AreRejected()
    {
        var spec = Spec("aus-8", Money.FromCents(20_000),
        [
            Split("s1", Money.FromCents(10_000), "pool-altes", "cat-miete"),
            Split("s2", Money.FromCents(10_000), "pool-flex", "cat-kino"),
        ]);
        Assert.Throws<InvalidReferenceException>(() => spec.Validate(Catalog()));
    }

    [Fact(DisplayName = "Split mit Nullbetrag wird abgelehnt")]
    public void ZeroSplitAmount_IsRejected()
    {
        var spec = Spec("aus-9", Money.FromCents(10_000),
        [
            Split("s1", Money.FromCents(10_000), "pool-flex", "cat-miete"),
            Split("s2", Money.Zero, "pool-freizeit", "cat-freizeit"),
        ]);
        Assert.Throws<LarioDomainException>(() => spec.Validate(Catalog()));
    }

    // ---------------------------------------------------------------------
    // Steuerkennzeichnung (Schritt 4, Rechenvertrag §3.5)
    // ---------------------------------------------------------------------

    [Fact(DisplayName = "Steuermarkierung: relevant + Kategorie + Teilbetrag ≤ Splitbetrag")]
    public void TaxMarking_ValidPartialAmount()
    {
        var catalog = Catalog();
        var spec = Spec("aus-10", Money.FromCents(10_000),
        [
            Split("s1", Money.FromCents(10_000), "pool-flex", "cat-miete",
                new TaxMarking { Relevant = true, TaxCategoryId = "tax-irrsinn", MarkedAmount = Money.FromCents(1_900) }),
        ]);
        spec.Validate(catalog);

        var booking = spec.ToBooking(catalog);
        var marking = booking.Splits[0].Tax!;
        Assert.True(marking.Relevant);
        Assert.Equal("tax-irrsinn", marking.TaxCategoryId);
        Assert.Equal(Money.FromCents(1_900), marking.MarkedAmount);
        // Markierung ist nur Fachdaten: Salden bleiben unangetastet.
        Assert.Equal(Money.FromCents(-10_000), booking.AccountPostings[0].Amount);
    }

    [Fact(DisplayName = "Steuermarkierung: Teilbetrag über Splitbetrag wird abgelehnt")]
    public void TaxMarking_PartialAmountExceedsSplit_IsRejected()
    {
        var spec = Spec("aus-11", Money.FromCents(10_000),
        [
            Split("s1", Money.FromCents(10_000), "pool-flex", "cat-miete",
                new TaxMarking { Relevant = true, TaxCategoryId = "tax-irrsinn", MarkedAmount = Money.FromCents(10_001) }),
        ]);
        Assert.Throws<LarioDomainException>(() => spec.Validate(Catalog()));
    }

    [Fact(DisplayName = "Steuermarkierung: Teilbetrag exakt bis zum Splitbetrag ist zulässig")]
    public void TaxMarking_PartialAmountEqualToSplit_IsAllowed()
    {
        var catalog = Catalog();
        var spec = Spec("aus-12", Money.FromCents(10_000),
        [
            Split("s1", Money.FromCents(10_000), "pool-flex", "cat-miete",
                new TaxMarking { Relevant = true, TaxCategoryId = "tax-irrsinn", MarkedAmount = Money.FromCents(10_000) }),
        ]);
        spec.Validate(catalog);
        Assert.NotNull(spec.ToBooking(catalog).Splits[0].Tax);
    }

    [Fact(DisplayName = "Steuermarkierung: nicht relevant, aber mit Kategorie/Teilbetrag → abgelehnt")]
    public void TaxMarking_NotRelevant_WithDetails_IsRejected()
    {
        var withCategory = Spec("aus-13a", Money.FromCents(10_000),
        [
            Split("s1", Money.FromCents(10_000), "pool-flex", "cat-miete",
                new TaxMarking { Relevant = false, TaxCategoryId = "tax-irrsinn" }),
        ]);
        Assert.Throws<LarioDomainException>(() => withCategory.Validate(Catalog()));

        var withAmount = Spec("aus-13b", Money.FromCents(10_000),
        [
            Split("s1", Money.FromCents(10_000), "pool-flex", "cat-miete",
                new TaxMarking { Relevant = false, MarkedAmount = Money.FromCents(100) }),
        ]);
        Assert.Throws<LarioDomainException>(() => withAmount.Validate(Catalog()));
    }

    [Fact(DisplayName = "Steuermarkierung: unbekannter oder archivierter Steuerkategorie wird abgelehnt")]
    public void TaxMarking_UnknownOrArchivedTaxCategory_IsRejected()
    {
        var unknown = Spec("aus-14a", Money.FromCents(10_000),
        [
            Split("s1", Money.FromCents(10_000), "pool-flex", "cat-miete",
                new TaxMarking { Relevant = true, TaxCategoryId = "tax-geister" }),
        ]);
        Assert.Throws<InvalidReferenceException>(() => unknown.Validate(Catalog()));

        var archived = Spec("aus-14b", Money.FromCents(10_000),
        [
            Split("s1", Money.FromCents(10_000), "pool-flex", "cat-miete",
                new TaxMarking { Relevant = true, TaxCategoryId = "tax-vat" }),
        ]);
        Assert.Throws<InvalidReferenceException>(() => archived.Validate(Catalog()));
    }

    [Fact(DisplayName = "Steuermarkierung: negativer Teilbetrag wird abgelehnt")]
    public void TaxMarking_NegativeAmount_IsRejected()
    {
        var spec = Spec("aus-15", Money.FromCents(10_000),
        [
            Split("s1", Money.FromCents(10_000), "pool-flex", "cat-miete",
                new TaxMarking { Relevant = true, TaxCategoryId = "tax-irrsinn", MarkedAmount = Money.FromCents(-1) }),
        ]);
        Assert.Throws<LarioDomainException>(() => spec.Validate(Catalog()));
    }

    // ---------------------------------------------------------------------
    // Steuer-Voreinstellung: Materialisierung (Plan §9)
    // ---------------------------------------------------------------------

    private static MasterDataCatalog CatalogWithPreset(bool? taxRelevant, string? taxCategoryId)
    {
        var catalog = Catalog();
        catalog.SetTaxPreset("cat-miete", taxRelevant, taxCategoryId, inheritToChildren: true);
        return catalog;
    }

    [Fact(DisplayName = "Voreinstellung 'ja' wird ohne explizite Markierung materialisiert")]
    public void Preset_Relevant_IsMaterialized()
    {
        var catalog = CatalogWithPreset(true, "tax-irrsinn");
        var spec = Spec("steu-1", Money.FromCents(10_000),
            [Split("s1", Money.FromCents(10_000), "pool-flex", "cat-miete")]);
        spec.Validate(catalog);

        var marking = spec.ToBooking(catalog).Splits[0].Tax!;
        Assert.True(marking.Relevant);
        Assert.Equal("tax-irrsinn", marking.TaxCategoryId);
        Assert.Null(marking.MarkedAmount);
    }

    [Fact(DisplayName = "Voreinstellung 'nein' wird als explizites Nein materialisiert")]
    public void Preset_NotRelevant_IsMaterialized()
    {
        var catalog = CatalogWithPreset(false, null);
        var spec = Spec("steu-2", Money.FromCents(10_000),
            [Split("s1", Money.FromCents(10_000), "pool-flex", "cat-miete")]);
        spec.Validate(catalog);

        var marking = spec.ToBooking(catalog).Splits[0].Tax!;
        Assert.False(marking.Relevant);
        Assert.Null(marking.TaxCategoryId);
    }

    [Fact(DisplayName = "Keine Voreinstellung → keine Markierung")]
    public void NoPreset_NoMarking()
    {
        var catalog = Catalog();
        var spec = Spec("steu-3", Money.FromCents(10_000),
            [Split("s1", Money.FromCents(10_000), "pool-flex", "cat-miete")]);
        spec.Validate(catalog);
        Assert.Null(spec.ToBooking(catalog).Splits[0].Tax);
    }

    [Fact(DisplayName = "Voreinstellung ohne festgelegte Relevanz (null) → keine Markierung")]
    public void Preset_NullRelevance_NoMarking()
    {
        var catalog = CatalogWithPreset(null, null);
        var spec = Spec("steu-4", Money.FromCents(10_000),
            [Split("s1", Money.FromCents(10_000), "pool-flex", "cat-miete")]);
        spec.Validate(catalog);
        Assert.Null(spec.ToBooking(catalog).Splits[0].Tax);
    }

    [Fact(DisplayName = "Ererbte Voreinstellung der übergeordneten Kategorie wird materialisiert")]
    public void InheritedPreset_IsMaterialized()
    {
        var catalog = Catalog();
        // Voreinstellung liegt an der übergeordneten Kategorie 'Wohnen'.
        catalog.SetTaxPreset("cat-wohnen", true, "tax-irrsinn", inheritToChildren: true);

        var spec = Spec("steu-5", Money.FromCents(10_000),
            [Split("s1", Money.FromCents(10_000), "pool-flex", "cat-miete")]);
        spec.Validate(catalog);
        Assert.NotNull(spec.ToBooking(catalog).Splits[0].Tax);
    }

    [Fact(DisplayName = "Explizites 'nein' übersteuert eine 'ja'-Voreinstellung")]
    public void ExplicitNo_OverridesPresetYes()
    {
        var catalog = CatalogWithPreset(true, "tax-irrsinn");
        var spec = Spec("steu-6", Money.FromCents(10_000),
        [
            Split("s1", Money.FromCents(10_000), "pool-flex", "cat-miete",
                new TaxMarking { Relevant = false, Note = "bewusst ohne" }),
        ]);
        spec.Validate(catalog);

        var marking = spec.ToBooking(catalog).Splits[0].Tax!;
        Assert.False(marking.Relevant);
        Assert.Equal("bewusst ohne", marking.Note);
    }

    [Fact(DisplayName = "Explizite Markierung übersteuert die Voreinstellung")]
    public void ExplicitMarking_OverridesPreset()
    {
        var catalog = CatalogWithPreset(true, "tax-irrsinn");
        var spec = Spec("steu-7", Money.FromCents(10_000),
        [
            Split("s1", Money.FromCents(10_000), "pool-flex", "cat-miete",
                new TaxMarking { Relevant = true, TaxCategoryId = "tax-irrsinn", MarkedAmount = Money.FromCents(500) }),
        ]);
        spec.Validate(catalog);

        var marking = spec.ToBooking(catalog).Splits[0].Tax!;
        Assert.Equal(Money.FromCents(500), marking.MarkedAmount);
    }

    [Fact(DisplayName = "Voreinstellung wird nur auf steuerrelevante Split-Kategorien angewandt")]
    public void Preset_AppliesPerSplit()
    {
        var catalog = CatalogWithPreset(true, "tax-irrsinn");
        var spec = Spec("steu-8", Money.FromCents(20_000),
        [
            Split("s1", Money.FromCents(12_000), "pool-flex", "cat-miete"),
            Split("s2", Money.FromCents(8_000), "pool-freizeit", "cat-freizeit"),
        ]);
        spec.Validate(catalog);

        var booking = spec.ToBooking(catalog);
        Assert.NotNull(booking.Splits.Single(s => s.Id == "s1").Tax);
        // Die Freizeit-Kategorie hat keine Voreinstellung → unmarkiert.
        Assert.Null(booking.Splits.Single(s => s.Id == "s2").Tax);
    }
}
