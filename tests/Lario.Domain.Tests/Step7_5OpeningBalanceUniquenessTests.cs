using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;

namespace Lario.Domain.Tests;

/// <summary>
/// Zwischenstep 7.5 (Einmaligkeit und Korrekturweg der Anfangsbestände):
/// Domain-Invarianten des gesicherten Anfangsbestandskontos
/// (<see cref="Booking.OpeningBalanceAccountId"/>, partieller eindeutiger
/// Index auf DB-Ebene) und die Erweiterung der In-place-Korrektur
/// (CorrectionSpec/CorrectionService) um den Buchungstyp
/// OpeningBalance (Rechenvertrag §3.7: nicht stornierbar → Korrektur).
/// Referenzzahlen: 100-€-Anfangsbestand auf dem Hauptkonto, davon 60 €
/// nach Flex verteilt, 40 € unverteilte Systemposition.
/// </summary>
public class Step7_5OpeningBalanceUniquenessTests
{
    private static readonly DateOnly Stichtag = new(2026, 1, 1);

    /// <summary>Referenz-Anfangsbestand: 100 € Haupt, 60 € → Flex.</summary>
    private static Booking Opening100() => new(
        "init-1",
        BookingType.OpeningBalance,
        Stichtag,
        "Anfangsbestand",
        [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(100_000) }],
        [new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(60_000) }],
        openingBalanceAccountId: "acc-haupt");

    // ==================================================================
    // Invariante: gesichertes Anfangsbestandskonto (DB-Ebene)
    // ==================================================================

    [Fact(DisplayName = "A2: Anfangsbestand ohne gesichertes Konto verletzt die Invariante")]
    public void OpeningBalance_WithoutAccountId_Throws()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "init-x", BookingType.OpeningBalance, Stichtag, null,
            [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(100) }],
            []));
    }

    [Fact(DisplayName = "A2: Gesichertes Konto muss mit dem einzigen Konto-Posting übereinstimmen")]
    public void OpeningBalance_AccountMismatch_Throws()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "init-y", BookingType.OpeningBalance, Stichtag, null,
            [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(100) }],
            [],
            openingBalanceAccountId: "acc-andere"));
    }

    [Fact(DisplayName = "A2: Gesichertes Konto ist nur auf Anfangsbeständen zulässig")]
    public void NonOpeningBalance_WithAccountId_Throws()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "bk-exp-1", BookingType.Expense, Stichtag, null,
            [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(-100) }],
            [new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(-100) }],
            splits:
            [
                new ExpenseSplit { Id = "s1", Amount = Money.FromCents(100), PoolId = "pool-flex", CategoryId = "cat-1" }
            ],
            openingBalanceAccountId: "acc-haupt"));
    }

    [Fact(DisplayName = "A2: Korrektur-Fluss — BuildUpdatedBooking trägt das neue Konto in die Invariante")]
    public void OpeningBalance_Correction_BuildsConsistentBooking()
    {
        var original = Opening100();
        var spec = new CorrectionSpec(
            original.Id, "op-corr-1", original.BaseRevision,
            AccountId: "acc-freizeit",
            TotalAmount: Money.FromCents(80_000),
            Allocations: [new IncomeAllocationSpec("pool-flex", Money.FromCents(80_000))]);

        spec.Validate(TestCatalog(), original);

        var updated = spec.BuildUpdatedBooking(original);

        Assert.Equal(original.Id, updated.Id);
        Assert.Equal(BookingType.OpeningBalance, updated.Type);
        Assert.Equal(original.Date, updated.Date);
        Assert.Equal(original.BaseRevision, updated.BaseRevision);

        var account = Assert.Single(updated.AccountPostings);
        Assert.Equal("acc-freizeit", account.AccountId);
        Assert.Equal(Money.FromCents(80_000), account.Amount);
        Assert.Equal(
            [("pool-flex", 80_000L)],
            updated.PoolPostings.Select(p => (p.PoolId, p.Amount.Cents)));
        Assert.Equal("acc-freizeit", updated.OpeningBalanceAccountId);
    }

    // ==================================================================
    // Korrektur-Validierung: wirksame Fassung wie Neuanlage
    // ==================================================================

    [Fact(DisplayName = "A3: Korrektur mit Teilposten abgelehnt (Verteilung trägt die Pools)")]
    public void OpeningBalance_Correction_WithSplits_Throws()
    {
        var original = Opening100();

        Assert.Throws<LarioDomainException>(() => new CorrectionSpec(
            original.Id, "op-corr-2", original.BaseRevision,
            TotalAmount: Money.FromCents(90_000),
            Splits:
            [
                new ExpenseSplitSpec { Id = "s1", Amount = Money.FromCents(90_000), PoolId = "pool-flex", CategoryId = "cat-1" }
            ]).Validate(TestCatalog(), original));
    }

    [Fact(DisplayName = "A3: Verteilung über den Gesamtbetrag abgelehnt")]
    public void OpeningBalance_Correction_DistributionExceedsTotal_Throws()
    {
        var original = Opening100();

        Assert.Throws<LarioDomainException>(() => new CorrectionSpec(
            original.Id, "op-corr-3", original.BaseRevision,
            TotalAmount: Money.FromCents(50_000),
            Allocations: [new IncomeAllocationSpec("pool-flex", Money.FromCents(60_000))])
            .Validate(TestCatalog(), original));
    }

    [Fact(DisplayName = "A3: Verteilung auf archivierten Pool abgelehnt")]
    public void OpeningBalance_Correction_ArchivedPool_Throws()
    {
        var original = Opening100();
        var catalog = TestCatalog();
        catalog.ArchivePool("pool-freizeit");

        Assert.Throws<InvalidReferenceException>(() => new CorrectionSpec(
            original.Id, "op-corr-4", original.BaseRevision,
            Allocations: [new IncomeAllocationSpec("pool-freizeit", Money.FromCents(10_000))])
            .Validate(catalog, original));
    }

    [Fact(DisplayName = "A3: Konto-Wechsel auf archiviertes Konto abgelehnt")]
    public void OpeningBalance_Correction_ArchivedAccount_Throws()
    {
        var original = Opening100();
        var catalog = TestCatalog();
        catalog.ArchiveAccount("acc-freizeit");

        Assert.Throws<InvalidReferenceException>(() => new CorrectionSpec(
            original.Id, "op-corr-5", original.BaseRevision,
            AccountId: "acc-freizeit")
            .Validate(catalog, original));
    }

    [Fact(DisplayName = "A3: Negativer Gesamtbetrag abgelehnt")]
    public void OpeningBalance_Correction_NegativeTotal_Throws()
    {
        var original = Opening100();

        Assert.Throws<LarioDomainException>(() => new CorrectionSpec(
            original.Id, "op-corr-6", original.BaseRevision,
            TotalAmount: Money.FromCents(-100))
            .Validate(TestCatalog(), original));
    }

    [Fact(DisplayName = "A3: Korrektur ohne Änderungen abgelehnt (wie Ausgabe/Einnahme)")]
    public void OpeningBalance_Correction_Empty_Throws()
    {
        var original = Opening100();

        Assert.Throws<LarioDomainException>(() => new CorrectionSpec(
            original.Id, "op-corr-7", original.BaseRevision)
            .Validate(TestCatalog(), original));
    }

    [Fact(DisplayName = "A3: Reine Notiz-Korrektur ist zulässig und belässt Konto/Verteilung")]
    public void OpeningBalance_Correction_NoteOnly_KeepsContent()
    {
        var original = Opening100();
        var spec = new CorrectionSpec(
            original.Id, "op-corr-8", original.BaseRevision, Note: "Stichtag war der 1.");

        spec.Validate(TestCatalog(), original);
        var updated = spec.BuildUpdatedBooking(original);

        Assert.Equal("Stichtag war der 1.", updated.Note);
        Assert.Equal("acc-haupt", updated.OpeningBalanceAccountId);
        var account = Assert.Single(updated.AccountPostings);
        Assert.Equal(Money.FromCents(100_000), account.Amount);
        Assert.Equal(
            [("pool-flex", 60_000L)],
            updated.PoolPostings.Select(p => (p.PoolId, p.Amount.Cents)));
    }

    [Fact(DisplayName = "A3: Nicht korrigierbare Typen bleiben abgelehnt (Transfer)")]
    public void Transfer_Correction_StillRejected()
    {
        var transfer = new Booking(
            "bk-transfer-1", BookingType.AccountTransfer, Stichtag, null,
            [
                new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(-1_000) },
                new AccountPosting { AccountId = "acc-freizeit", Amount = Money.FromCents(1_000) },
            ],
            [],
            transferPurposes: [new PoolAllocation("pool-flex", Money.FromCents(1_000))]);

        Assert.Throws<LarioDomainException>(() => new CorrectionSpec(
            transfer.Id, "op-corr-9", 1, Note: "Notiz").Validate(TestCatalog(), transfer));
    }

    private static MasterDataCatalog TestCatalog()
    {
        var catalog = new MasterDataCatalog([], [], [], []);
        catalog.CreateAccount("acc-haupt", "Hauptkonto", AccountType.Bank);
        catalog.CreateAccount("acc-freizeit", "Freizeitkonto", AccountType.Bank);
        catalog.CreatePool("pool-flex", "Flex");
        catalog.CreatePool("pool-freizeit", "Freizeit");
        catalog.CreateCategory("cat-1", "Lebensmittel");
        return catalog;
    }
}
