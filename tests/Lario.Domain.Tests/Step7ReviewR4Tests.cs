using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;
using Xunit;

namespace Lario.Domain.Tests;

/// <summary>
/// Regressionstests für PR #9 Review Runde 2 (P2, Korrektur/Steuer):
///
/// Die Client-Vorbelegung setzte bei unmarkierten Splits
/// taxMode=„inherit“; hatte die Kategorie inzwischen eine relevante
/// Voreinstellung bekommen, materialisierte eine Korrektur plötzlich
/// neue Steuerkennzeichnungen, obwohl der Benutzer an den Splits
/// nichts Steuerliches geändert hatte.
///
/// Fix (Server, <see cref="CorrectionSpec"/>): <c>tax = null</c> bei
/// einem Teilposten heißt „unverändert“ (dieselbe Konvention wie die
/// übrigen Korrekturfelder) — ein bestehender Teilposten behält seine
/// ursprüngliche Kennzeichnung vollständig (inkl. Steuer-Notiz und
/// bisheriger Nichtmarkierung), ein neuer Teilposten wird aus der
/// Kategorie-Voreinstellung materialisiert wie bei der Neuanlage.
/// Eine Steueränderung muss explizit gesendet werden.
/// </summary>
public class Step7ReviewR4Tests
{
    private static readonly DateOnly Datum = new(2026, 10, 15);

    /// <summary>
    /// Referenzzahlen: 85-€-Ausgabe in zwei Splits (65 € Flex /
    /// 20 € Freizeit), beide Kategorie „Miete“. Split s1 ist
    /// explizit markiert (Steuerkategorie „Zoll“, Teilbetrag 20 €,
    /// Steuer-Notiz), Split s2 ist unmarkiert — obwohl „Miete“
    /// inzwischen eine relevante Voreinstellung („Irrsinn“) trägt.
    /// </summary>
    private static (Booking original, MasterDataCatalog catalog) Original()
    {
        var catalog = new MasterDataCatalog([], [], [], []);
        catalog.CreateAccount("acc-haupt", "Hauptkonto", AccountType.Bank);
        catalog.CreatePool("pool-flex", "Flex");
        catalog.CreatePool("pool-freizeit", "Freizeit");
        catalog.CreateCategory("cat-miete", "Miete");
        catalog.CreateTaxCategory("tax-zoll", "Zoll");
        catalog.CreateTaxCategory("tax-irrsinn", "Irrsinn");
        // Inzwischen eingerichtete relevante Voreinstellung: würde bei
        // stiller Materialisierung neue Kennzeichnungen erzeugen.
        catalog.SetTaxPreset("cat-miete", true, "tax-irrsinn", inheritToChildren: false);

        var original = new Booking(
            "bk-exp-85",
            BookingType.Expense,
            Datum,
            "Gemischter Einkauf",
            [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(-8_500) }],
            [
                new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(-6_500) },
                new PoolPosting { PoolId = "pool-freizeit", Amount = Money.FromCents(-2_000) },
            ],
            splits:
            [
                new ExpenseSplit
                {
                    Id = "s1",
                    Amount = Money.FromCents(6_500),
                    PoolId = "pool-flex",
                    CategoryId = "cat-miete",
                    Tax = new TaxMarking
                    {
                        Relevant = true,
                        TaxCategoryId = "tax-zoll",
                        MarkedAmount = Money.FromCents(2_000),
                        Note = "Vorsteuer 19 %",
                        InheritedFromPreset = false,
                    },
                },
                new ExpenseSplit
                {
                    Id = "s2",
                    Amount = Money.FromCents(2_000),
                    PoolId = "pool-freizeit",
                    CategoryId = "cat-miete",
                    // Historisch unmarkiert — keine Kennzeichnung.
                },
            ]);

        return (original, catalog);
    }

    [Fact(DisplayName = "R2-P2: Unveränderter Split behält Kennzeichnung inkl. Steuer-Notiz (keine stille Materialisierung)")]
    public void Correction_KeepsOriginalMarkingInclusiveOfTaxNote()
    {
        var (original, catalog) = Original();

        // Split-Notiz von s1 geändert, Beträge neu aufgeteilt — die
        // Steuer-Eingaben bleiben unangetastet (Client sendet tax = null).
        var spec = new CorrectionSpec(
            original.Id, "op-r4-1", original.BaseRevision,
            TotalAmount: Money.FromCents(8_000),
            Splits:
            [
                new ExpenseSplitSpec { Id = "s1", Amount = Money.FromCents(5_500), PoolId = "pool-flex", CategoryId = "cat-miete", Note = "korrigiert" },
                new ExpenseSplitSpec { Id = "s2", Amount = Money.FromCents(2_000), PoolId = "pool-freizeit", CategoryId = "cat-miete" },
                new ExpenseSplitSpec { Id = "s3", Amount = Money.FromCents(500), PoolId = "pool-flex", CategoryId = "cat-miete" },
            ]);
        spec.Validate(catalog, original);

        var updated = spec.BuildUpdatedBooking(original, catalog);

        var s1 = updated.Splits.Single(s => s.Id == "s1");
        var tax = s1.Tax!;
        // Vollständige Original-Kennzeichnung — nicht die Voreinstellung:
        Assert.True(tax.Relevant);
        Assert.Equal("tax-zoll", tax.TaxCategoryId);
        Assert.Equal(Money.FromCents(2_000), tax.MarkedAmount);
        Assert.Equal("Vorsteuer 19 %", tax.Note);
        Assert.False(tax.InheritedFromPreset);
        // Split-Notiz geändert:
        Assert.Equal("korrigiert", s1.Note);
    }

    [Fact(DisplayName = "R2-P2: Bisherige Nichtmarkierung bleibt erhalten, obwohl die Kategorie jetzt eine relevante Voreinstellung trägt")]
    public void Correction_KeepsHistoricalUnmarkedState()
    {
        var (original, catalog) = Original();

        var spec = new CorrectionSpec(
            original.Id, "op-r4-2", original.BaseRevision,
            TotalAmount: Money.FromCents(8_000),
            Splits:
            [
                new ExpenseSplitSpec { Id = "s1", Amount = Money.FromCents(6_000), PoolId = "pool-flex", CategoryId = "cat-miete" },
                new ExpenseSplitSpec { Id = "s2", Amount = Money.FromCents(2_000), PoolId = "pool-freizeit", CategoryId = "cat-miete" },
            ]);
        spec.Validate(catalog, original);

        var updated = spec.BuildUpdatedBooking(original, catalog);

        // s2 war unmarkiert und wurde nicht angerührt → bleibt unmarkiert;
        // die neue Voreinstellung wird nicht still nachgetragen.
        Assert.Null(updated.Splits.Single(s => s.Id == "s2").Tax);
    }

    [Fact(DisplayName = "R2-P2: Neuer Split ohne Angabe wird wie bei der Neuanlage aus der Voreinstellung materialisiert")]
    public void Correction_MaterializesPresetForNewSplit()
    {
        var (original, catalog) = Original();

        var spec = new CorrectionSpec(
            original.Id, "op-r4-3", original.BaseRevision,
            TotalAmount: Money.FromCents(8_000),
            Splits:
            [
                new ExpenseSplitSpec { Id = "s1", Amount = Money.FromCents(6_000), PoolId = "pool-flex", CategoryId = "cat-miete" },
                new ExpenseSplitSpec { Id = "s2", Amount = Money.FromCents(1_500), PoolId = "pool-freizeit", CategoryId = "cat-miete" },
                new ExpenseSplitSpec { Id = "s3", Amount = Money.FromCents(500), PoolId = "pool-flex", CategoryId = "cat-miete" },
            ]);
        spec.Validate(catalog, original);

        var updated = spec.BuildUpdatedBooking(original, catalog);

        var s3 = updated.Splits.Single(s => s.Id == "s3");
        Assert.NotNull(s3.Tax);
        Assert.True(s3.Tax!.Relevant);
        Assert.Equal("tax-irrsinn", s3.Tax.TaxCategoryId);
        Assert.Null(s3.Tax.MarkedAmount);
        Assert.True(s3.Tax.InheritedFromPreset);
    }

    [Fact(DisplayName = "R2-P2: Explizit geänderte Kennzeichnung ersetzt die ursprüngliche (Steuer-Notiz geht verloren)")]
    public void Correction_ExplicitTaxChangeReplacesOriginal()
    {
        var (original, catalog) = Original();

        var spec = new CorrectionSpec(
            original.Id, "op-r4-4", original.BaseRevision,
            TotalAmount: Money.FromCents(8_000),
            Splits:
            [
                new ExpenseSplitSpec
                {
                    Id = "s1",
                    Amount = Money.FromCents(6_000),
                    PoolId = "pool-flex",
                    CategoryId = "cat-miete",
                    Tax = new TaxMarking { Relevant = true, TaxCategoryId = "tax-irrsinn" },
                },
                new ExpenseSplitSpec { Id = "s2", Amount = Money.FromCents(2_000), PoolId = "pool-freizeit", CategoryId = "cat-miete" },
            ]);
        spec.Validate(catalog, original);

        var updated = spec.BuildUpdatedBooking(original, catalog);

        var s1 = updated.Splits.Single(s => s.Id == "s1");
        Assert.True(s1.Tax!.Relevant);
        Assert.Equal("tax-irrsinn", s1.Tax.TaxCategoryId);
        Assert.Null(s1.Tax.MarkedAmount);
        Assert.Null(s1.Tax.Note);
        Assert.False(s1.Tax.InheritedFromPreset);
    }

    [Fact(DisplayName = "R2-P2: Explizite Nichtmarkierung (relevant = false) ersetzt eine bisherige Markierung")]
    public void Correction_ExplicitIrrelevantReplacesOriginal()
    {
        var (original, catalog) = Original();

        var spec = new CorrectionSpec(
            original.Id, "op-r4-5", original.BaseRevision,
            TotalAmount: Money.FromCents(8_000),
            Splits:
            [
                new ExpenseSplitSpec
                {
                    Id = "s1",
                    Amount = Money.FromCents(6_000),
                    PoolId = "pool-flex",
                    CategoryId = "cat-miete",
                    Tax = new TaxMarking { Relevant = false },
                },
                new ExpenseSplitSpec { Id = "s2", Amount = Money.FromCents(2_000), PoolId = "pool-freizeit", CategoryId = "cat-miete" },
            ]);
        spec.Validate(catalog, original);

        var updated = spec.BuildUpdatedBooking(original, catalog);

        var s1 = updated.Splits.Single(s => s.Id == "s1");
        Assert.NotNull(s1.Tax);
        Assert.False(s1.Tax!.Relevant);
        Assert.Null(s1.Tax.TaxCategoryId);
        Assert.Null(s1.Tax.MarkedAmount);
        Assert.Null(s1.Tax.Note);
    }
}
