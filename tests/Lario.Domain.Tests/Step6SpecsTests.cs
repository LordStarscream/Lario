using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;

namespace Lario.Domain.Tests;

/// <summary>
/// Schritt 6 — Domain-Invarianten der neuen Buchungstypen und ihrer
/// Spezifikationen (Rechenvertrag §3.3/§4/§5.3/§5.4/§5.5, Design Schritt 6
/// §2.2/§2.3): Storno (Reversal), Erstattung (Refund),
/// Kontostandskorrektur (BalanceCorrection) und In-place-Korrektur
/// (CorrectionSpec). Referenzzahlen: 85-€-Ausgabe (65 Flex / 20 Freizeit),
/// 10-€-Erstattung, 5-€-Abgleichdifferenz.
/// </summary>
public class Step6SpecsTests
{
    private static readonly DateOnly Datum = new(2026, 10, 5);

    // ------------------------------------------------------------------
    // Referenz-Ausgabe 85 € (65 Flex / 20 Freizeit) vom Hauptkonto
    // ------------------------------------------------------------------

    private static Booking Expense85() => new(
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
            new ExpenseSplit { Id = "s1", Amount = Money.FromCents(6_500), PoolId = "pool-flex", CategoryId = "cat-1" },
            new ExpenseSplit { Id = "s2", Amount = Money.FromCents(2_000), PoolId = "pool-freizeit", CategoryId = "cat-2" },
        ]);

    // ==================================================================
    // Storno (Reversal)
    // ==================================================================

    [Fact(DisplayName = "A1/B1: Storno negiert Konto- und Pool-Postings und trägt die negierten Original-Deltas")]
    public void Reversal_Spec_ToBooking_NegatesPostingsAndDeltas()
    {
        var original = Expense85();
        var spec = new ReversalSpec(
            "bk-storno-1", original.Id, original.BaseRevision,
            new DateOnly(2026, 10, 6), "Storno Einkauf");

        var reversal = spec.ToBooking(original);

        // Typ und Verknüpfung.
        Assert.Equal(BookingType.Reversal, reversal.Type);
        Assert.Equal(original.Id, reversal.ReversesBookingId);
        Assert.Equal("bk-storno-1", reversal.Id);

        // Konto-Posting: exakte Negation (−85 € → +85 €).
        var account = Assert.Single(reversal.AccountPostings);
        Assert.Equal("acc-haupt", account.AccountId);
        Assert.Equal(Money.FromCents(8_500), account.Amount);

        // Pool-Postings: exakte Negation (−65/−20 → +65/+20).
        Assert.Equal(
            [
                ("pool-flex", 6_500L),
                ("pool-freizeit", 2_000L),
            ],
            reversal.PoolPostings.Select(p => (p.PoolId, p.Amount.Cents)));

        // Invariante: ReversalDeltas == Negation(Original.AllocationDelta()).
        var expectedDeltas = original.AllocationDelta()
            .Select(d => new AllocationDeltaEntry(d.AccountId, d.PoolId, d.Amount.Negate()));
        Assert.Equal(
            expectedDeltas.OrderBy(d => d.AccountId).ThenBy(d => d.PoolId),
            reversal.ReversalDeltas.OrderBy(d => d.AccountId).ThenBy(d => d.PoolId));
    }

    [Fact(DisplayName = "B1: Storno ohne Verknüpfung zum Original verletzt die Invariante")]
    public void Reversal_WithoutLink_Throws()
    {
        var original = Expense85();
        var spec = new ReversalSpec("bk-storno-2", "bk-andere", original.BaseRevision, Datum);
        var reversal = spec.ToBooking(original);

        // Der Konstrutor prüft ReversesBookingId (ToBooking setzt es aus
        // original.Id). Eine manuell entkoppelte Buchung muss scheitern.
        Assert.Throws<LarioDomainException>(() => new Booking(
            "bk-storno-3", BookingType.Reversal, Datum, null,
            [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(100) }],
            [new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(100) }],
            reversalDeltas: [new AllocationDeltaEntry("acc-haupt", "pool-flex", Money.FromCents(100))]));
    }

    [Fact(DisplayName = "B1: Storno ohne ReversalDeltas verletzt die Invariante")]
    public void Reversal_WithoutDeltas_Throws()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "bk-storno-4", BookingType.Reversal, Datum, null,
            [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(100) }],
            [new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(100) }],
            reversesBookingId: "bk-exp-85"));
    }

    [Fact(DisplayName = "B1: Storno-Deltas müssen gegen die eigenen Postings balancieren")]
    public void Reversal_DeltaBalance_Mismatch_Throws()
    {
        // Deltas referenzieren einen Pool, für den das Storno kein
        // ungleich-0-Posting trägt.
        Assert.Throws<LarioDomainException>(() => new Booking(
            "bk-storno-5", BookingType.Reversal, Datum, null,
            [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(100) }],
            [new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(100) }],
            reversesBookingId: "bk-exp-85",
            reversalDeltas:
            [
                new AllocationDeltaEntry("acc-haupt", "pool-flex", Money.FromCents(100)),
                new AllocationDeltaEntry("acc-haupt", "pool-fremd", Money.FromCents(50)),
            ]));
    }

    [Fact(DisplayName = "B1: ReversalSpec.Validate — Selbstverknüpfung und Revision<1 abgelehnt")]
    public void Reversal_Spec_Validate_SelfLinkAndRevision_Throws()
    {
        Assert.Throws<LarioDomainException>(() => new ReversalSpec(
            "bk-selbst", "bk-selbst", 1, Datum).Validate());

        Assert.Throws<LarioDomainException>(() => new ReversalSpec(
            "bk-storno-6", "bk-exp-85", 0, Datum).Validate());
    }

    [Fact(DisplayName = "Storno eines Stornos: Negation der Storno-Deltas ist die Original-Wirkung")]
    public void Reversal_OfReversal_RestoresOriginalEffect()
    {
        var original = Expense85();
        var first = new ReversalSpec("bk-st-a", original.Id, 1, Datum).ToBooking(original);

        // Widerruf: Storno auf das Storno — negiert die Storno-Deltas.
        var second = new ReversalSpec("bk-st-b", first.Id, 1, Datum).ToBooking(first);

        // Die zweiten Deltas == Negation(Storno.AllocationDelta()) ==
        // Original.AllocationDelta() (die ursprüngliche Wirkung).
        Assert.Equal(
            original.AllocationDelta().OrderBy(d => d.AccountId).ThenBy(d => d.PoolId),
            second.ReversalDeltas.OrderBy(d => d.AccountId).ThenBy(d => d.PoolId));
    }

    // ==================================================================
    // Erstattung (Refund)
    // ==================================================================

    [Fact(DisplayName = "A2: Erstattung 10 € — Konto +10 €, Ursprungspool +10 €, verknüpft mit Original")]
    public void Refund_Spec_ToBooking_CreditsAccountAndPool()
    {
        var original = Expense85();
        var spec = new RefundSpec(
            "bk-erstatt-1", original.Id, "acc-haupt",
            new DateOnly(2026, 10, 20),
            [new RefundLineSpec("pool-flex", Money.FromCents(1_000))],
            "Teilrückzahlung Händler");

        var refund = spec.ToBooking(original);

        Assert.Equal(BookingType.Refund, refund.Type);
        Assert.Equal(original.Id, refund.RefundsExpenseId);
        Assert.Null(refund.FundingMonth); // Keine Einnahme.
        Assert.Empty(refund.Splits);

        // Konto-Posting: +Gesamtbetrag (10 €) am Gutschriftskonto.
        var account = Assert.Single(refund.AccountPostings);
        Assert.Equal("acc-haupt", account.AccountId);
        Assert.Equal(Money.FromCents(1_000), account.Amount);

        // Pool-Posting: +Zeilenbetrag (10 €) an den Ursprungspool.
        var pool = Assert.Single(refund.PoolPostings);
        Assert.Equal("pool-flex", pool.PoolId);
        Assert.Equal(Money.FromCents(1_000), pool.Amount);

        // Erstattungs-Delta: (Konto, Pool) +Zeilenbetrag.
        Assert.Equal(
            [new AllocationDeltaEntry("acc-haupt", "pool-flex", Money.FromCents(1_000))],
            refund.AllocationDelta());
    }

    [Fact(DisplayName = "A2: Erstattung ist keine Einnahme — TotalIncome zählt sie nicht an")]
    public void Refund_IsNotIncome()
    {
        var original = Expense85();
        var refund = new RefundSpec(
            "bk-erstatt-2", original.Id, "acc-haupt", Datum,
            [new RefundLineSpec("pool-flex", Money.FromCents(1_000))]).ToBooking(original);

        // Ohne Finanzierungsmonat; TotalIncome zählt nur Einnahmen — die
        // Erstattung (10 €) zählt nicht an.
        Assert.Null(refund.FundingMonth);
        Assert.Equal(Money.Zero, new BookingJournal([original, refund]).TotalIncome());
    }

    [Fact(DisplayName = "B2: Erstattung ohne Ursprungs-Verknüpfung verletzt die Invariante")]
    public void Refund_WithoutLink_Throws()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "bk-erstatt-3", BookingType.Refund, Datum, null,
            [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(1_000) }],
            [new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(1_000) }]));
    }

    [Fact(DisplayName = "B3: Erstattung ohne Pool-Rückfluss verletzt die Invariante")]
    public void Refund_WithoutPoolPostings_Throws()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "bk-erstatt-4", BookingType.Refund, Datum, null,
            [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(1_000) }],
            [],
            refundsExpenseId: "bk-exp-85"));
    }

    [Fact(DisplayName = "B2: RefundSpec.Validate — Original muss Ausgabe sein, Pools ⊆ Ursprungs-Pools")]
    public void Refund_Spec_Validate_OriginalAndPools()
    {
        var catalog = TestCatalog();
        var original = Expense85();

        // Original ist keine Ausgabe (Einnahme) → abgelehnt.
        var income = new Booking(
            "bk-einn-1", BookingType.Income, Datum, null,
            [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(10_000) }],
            [new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(10_000) }],
            fundingMonth: YearMonth.Create(2026, 11));
        Assert.Throws<LarioDomainException>(() => new RefundSpec(
            "bk-erstatt-5", "bk-einn-1", "acc-haupt", Datum,
            [new RefundLineSpec("pool-flex", Money.FromCents(1_000))]).Validate(catalog, income));

        // Pool ist kein Ursprungs-Pool der Ausgabe → abgelehnt.
        Assert.Throws<LarioDomainException>(() => new RefundSpec(
            "bk-erstatt-6", original.Id, "acc-haupt", Datum,
            [new RefundLineSpec("pool-ruecklage", Money.FromCents(1_000))]).Validate(catalog, original));

        // Giltiger Fall: Ursprungspool, positiver Betrag.
        new RefundSpec(
            "bk-erstatt-7", original.Id, "acc-haupt", Datum,
            [new RefundLineSpec("pool-freizeit", Money.FromCents(1_000))]).Validate(catalog, original);
    }

    // ==================================================================
    // Kontostandskorrektur (BalanceCorrection)
    // ==================================================================

    [Fact(DisplayName = "A4: Kontostandskorrektur — genau ein signiertes Konto-Posting, keine Pools, Notiz Pflicht")]
    public void BalanceCorrection_Spec_ToBooking_SingleSignedPosting()
    {
        var spec = new BalanceCorrectionSpec(
            "bk-abgleich-1", "acc-haupt", Money.FromCents(500), Datum,
            "Bankdifferenz 14.10.: 5 € Guthaben auf der Bank");

        var booking = spec.ToBooking();

        Assert.Equal(BookingType.BalanceCorrection, booking.Type);
        var account = Assert.Single(booking.AccountPostings);
        Assert.Equal("acc-haupt", account.AccountId);
        Assert.Equal(Money.FromCents(500), account.Amount);
        Assert.Empty(booking.PoolPostings);
        Assert.NotNull(booking.Note);

        // Keine Konto-Pool-Zelle: AllocationDelta bleibt leer.
        Assert.Empty(booking.AllocationDelta());
    }

    [Fact(DisplayName = "A4: Kontostandskorrektur mit Pool-Posting verletzt die Invariante")]
    public void BalanceCorrection_WithPoolPosting_Throws()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "bk-abgleich-2", BookingType.BalanceCorrection, Datum, "Notiz",
            [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(500) }],
            [new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(500) }]));
    }

    [Fact(DisplayName = "A4: Kontostandskorrektur mit Betrag 0 verletzt die Invariante")]
    public void BalanceCorrection_ZeroAmount_Throws()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "bk-abgleich-3", BookingType.BalanceCorrection, Datum, "Notiz",
            [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(0) }],
            []));
    }

    [Fact(DisplayName = "A4: Kontostandskorrektur ohne Notiz verletzt die Invariante")]
    public void BalanceCorrection_WithoutNote_Throws()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "bk-abgleich-4", BookingType.BalanceCorrection, Datum, null,
            [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(500) }],
            []));
    }

    [Fact(DisplayName = "A4: BalanceCorrectionSpec.Validate — Betrag 0 und leere Notiz abgelehnt")]
    public void BalanceCorrection_Spec_Validate()
    {
        var catalog = TestCatalog();

        Assert.Throws<LarioDomainException>(() => new BalanceCorrectionSpec(
            "bk-abgleich-5", "acc-haupt", Money.FromCents(0), Datum, "Notiz").Validate(catalog));

        Assert.Throws<LarioDomainException>(() => new BalanceCorrectionSpec(
            "bk-abgleich-6", "acc-haupt", Money.FromCents(500), Datum, "   ").Validate(catalog));

        // Giltiger Fall.
        new BalanceCorrectionSpec(
            "bk-abgleich-7", "acc-haupt", Money.FromCents(500), Datum,
            "Bankdifferenz 14.10.").Validate(catalog);
    }

    // ==================================================================
    // Korrektur (CorrectionSpec, In-place)
    // ==================================================================

    [Fact(DisplayName = "A1: Korrektur 85 € → 80 € — gleiche Id/Typ/Datum, neue Splits addieren zu 80 €")]
    public void Correction_Spec_BuildUpdatedBooking_ReplacesContent()
    {
        var original = Expense85();
        var spec = new CorrectionSpec(
            original.Id, "op-1", original.BaseRevision,
            TotalAmount: Money.FromCents(8_000),
            Splits:
            [
                new ExpenseSplitSpec { Id = "s1", Amount = Money.FromCents(6_000), PoolId = "pool-flex", CategoryId = "cat-1" },
                new ExpenseSplitSpec { Id = "s2", Amount = Money.FromCents(2_000), PoolId = "pool-freizeit", CategoryId = "cat-2" },
            ]);

        var updated = spec.BuildUpdatedBooking(original);

        // Gleiche Identität und Basis (Revisionserhöhung macht der Service).
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal(BookingType.Expense, updated.Type);
        Assert.Equal(original.Date, updated.Date);
        Assert.Equal(original.BaseRevision, updated.BaseRevision);

        // Neues Konto-Posting: −80 €.
        var account = Assert.Single(updated.AccountPostings);
        Assert.Equal(Money.FromCents(-8_000), account.Amount);

        // Neue Pool-Postings: −60 € / −20 €.
        Assert.Equal(
            [
                ("pool-flex", -6_000L),
                ("pool-freizeit", -2_000L),
            ],
            updated.PoolPostings.Select(p => (p.PoolId, p.Amount.Cents)));

        // Neue Teilposten addieren exakt zum Gesamtbetrag (80 €).
        Assert.Equal(Money.FromCents(8_000), updated.Splits.Aggregate(Money.Zero, (s, x) => s.Add(x.Amount)));

        // Keine Storno-/Erstattungs-Verknüpfung.
        Assert.Null(updated.ReversesBookingId);
        Assert.Null(updated.RefundsExpenseId);
    }

    [Fact(DisplayName = "B4: Korrektur nur für Ausgabe/Einnahme — andere Typen abgelehnt")]
    public void Correction_Spec_Validate_OnlyExpenseIncome()
    {
        var catalog = TestCatalog();
        var transfer = new Booking(
            "bk-transfer-1", BookingType.AccountTransfer, Datum, null,
            [
                new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(-1_000) },
                new AccountPosting { AccountId = "acc-freizeit", Amount = Money.FromCents(1_000) },
            ],
            [],
            transferPurposes: [new PoolAllocation("pool-flex", Money.FromCents(1_000))]);

        Assert.Throws<LarioDomainException>(() => new CorrectionSpec(
            transfer.Id, "op-2", 1, Note: "Neue Notiz").Validate(catalog, transfer));
    }

    [Fact(DisplayName = "B4: Korrektur mit Teilposten ohne Gesamtbetrag abgelehnt (Splitregel)")]
    public void Correction_Spec_Validate_SplitsRequireTotal()
    {
        var catalog = TestCatalog();
        var original = Expense85();

        Assert.Throws<LarioDomainException>(() => new CorrectionSpec(
            original.Id, "op-3", 1,
            Splits:
            [
                new ExpenseSplitSpec { Id = "s1", Amount = Money.FromCents(6_000), PoolId = "pool-flex", CategoryId = "cat-1" },
            ]).Validate(catalog, original));
    }

    [Fact(DisplayName = "A1: Korrektur mit Splits+Verteilung gleichzeitig abgelehnt")]
    public void Correction_Spec_Validate_SplitsAndAllocationsMutuallyExclusive()
    {
        var catalog = TestCatalog();
        var original = Expense85();

        Assert.Throws<LarioDomainException>(() => new CorrectionSpec(
            original.Id, "op-4", 1,
            TotalAmount: Money.FromCents(8_000),
            Splits: [new ExpenseSplitSpec { Id = "s1", Amount = Money.FromCents(8_000), PoolId = "pool-flex", CategoryId = "cat-1" }],
            Allocations: [new IncomeAllocationSpec("pool-flex", Money.FromCents(8_000))]).Validate(catalog, original));
    }

    [Fact(DisplayName = "A1: Leere Korrektur (keine Felder) abgelehnt")]
    public void Correction_Spec_Validate_EmptyCorrection()
    {
        var catalog = TestCatalog();
        var original = Expense85();

        Assert.Throws<LarioDomainException>(() => new CorrectionSpec(
            original.Id, "op-5", 1).Validate(catalog, original));
    }

    [Fact(DisplayName = "B6: Korrektur ohne neue Splits — Steuerkennzeichnungen bleiben erhalten")]
    public void Correction_Spec_BuildUpdatedBooking_KeepsExistingSplitsWithoutNewSplits()
    {
        var original = new Booking(
            "bk-exp-tax",
            BookingType.Expense,
            Datum,
            null,
            [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(-10_000) }],
            [new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(-10_000) }],
            splits:
            [
                new ExpenseSplit
                {
                    Id = "s1",
                    Amount = Money.FromCents(10_000),
                    PoolId = "pool-flex",
                    CategoryId = "cat-1",
                    Tax = new TaxMarking { Relevant = true, MarkedAmount = Money.FromCents(4_000) },
                },
            ]);

        // Nur Datum ändern, Splits bleiben unverändert.
        var updated = new CorrectionSpec(
            original.Id, "op-6", 1,
            Date: new DateOnly(2026, 10, 7)).BuildUpdatedBooking(original);

        // Die historischen Steuerkennzeichnungen bleiben an den Splits.
        var split = Assert.Single(updated.Splits);
        Assert.Equal(Money.FromCents(4_000), split.Tax!.MarkedAmount);
        // Markierter Teilbetrag ≤ Splitbetrag.
        Assert.True(split.Tax!.MarkedAmount <= split.Amount);
    }

    // ------------------------------------------------------------------
    // Test-Katalog
    // ------------------------------------------------------------------

    private static MasterDataCatalog TestCatalog()
    {
        var catalog = new MasterDataCatalog([], [], [], []);
        catalog.CreateAccount("acc-haupt", "Hauptkonto", AccountType.Bank);
        catalog.CreateAccount("acc-freizeit", "Freizeitkonto", AccountType.Bank);
        catalog.CreatePool("pool-flex", "Flex");
        catalog.CreatePool("pool-freizeit", "Freizeit");
        catalog.CreatePool("pool-ruecklage", "Rücklage");
        catalog.CreateCategory("cat-1", "Lebensmittel");
        catalog.CreateCategory("cat-2", "Freizeit");
        return catalog;
    }
}
