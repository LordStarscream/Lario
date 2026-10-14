using Lario.Application;
using Lario.Application.Journal;
using Lario.Domain;
using Lario.Domain.Journal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lario.Infrastructure.Tests;

/// <summary>
/// Schritt 6 auf echter SQLite: Korrekturen (In-place, Revision + Historie),
/// Storno (Gegenbewegung + Revision++), Erstattung (Rückfluss, Deckelung)
/// und Kontostandskorrektur (dokumentierte Differenz). Abnahmekriterien
/// A1–A4 und Pflichtfälle B1–B6. Referenzzahlen: 85 €-Ausgabe (65/20),
/// 10 €-Erstattung, 5 €-Abgleichdifferenz, 20-€-Ausgleich.
/// </summary>
public class Step6CorrectionsReversalsTests
{
    // ==================================================================
    // A1: Korrektur 85 € → 80 € (In-place, keine zweite Ausgabe)
    // ==================================================================

    [Fact(DisplayName = "A1: Ausgabe 85 € auf 80 € korrigieren — keine zweite Ausgabe, Revision 1→2, Historie")]
    public async Task A1_CorrectExpense_InPlace_RevisionAndHistory()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = "bk-a1",
            Date = new DateOnly(2026, 11, 5),
            AccountId = Step5Scenario.Haupt,
            TotalAmount = new Money(8_500),
            Splits =
            [
                new ExpenseSplitSpec { Id = "s1", Amount = new Money(6_500), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
                new ExpenseSplitSpec { Id = "s2", Amount = new Money(2_000), PoolId = Step5Scenario.PoolFreizeit, CategoryId = "cat-freizeit" },
            ],
        });

        var result = await db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
            "bk-a1", "op-a1", ExpectedBaseRevision: 1,
            TotalAmount: new Money(8_000),
            Splits:
            [
                new ExpenseSplitSpec { Id = "s1", Amount = new Money(6_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
                new ExpenseSplitSpec { Id = "s2", Amount = new Money(2_000), PoolId = Step5Scenario.PoolFreizeit, CategoryId = "cat-freizeit" },
            ]));

        // In-place: dieselbe Buchung (keine zweite Ausgabe), Revision 1→2.
        Assert.True(result.Created);
        Assert.Equal("bk-a1", result.Booking.Id);
        Assert.Equal(2, result.Booking.BaseRevision);

        // Neue Split-Summe exakt 80 €.
        Assert.Equal(new Money(8_000),
            result.Booking.Splits.Aggregate(Money.Zero, (s, x) => s.Add(x.Amount)));

        // Nur eine Ausgabe existiert (keine zweite).
        var journal = await Step5Scenario.JournalAsync(db);
        Assert.Single(journal.Bookings, b => b.Type == BookingType.Expense);

        // Salden: Haupt 920, Flex 940, Freizeit 280.
        Assert.Equal(92_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
        Assert.Equal(94_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.Flex));
        Assert.Equal(28_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.PoolFreizeit));

        // Historie: Vorher/Nachher-Snapshot, nachvollziehbar.
        var correction = await db.Resolve<IBookingRepository>().FindCorrectionAsync("op-a1");
        Assert.NotNull(correction);
        Assert.Equal("bk-a1", correction!.BookingId);

        // Vorher-Snapshot: alter Inhalt (85 €), Nachher: neuer Inhalt (80 €).
        using var oldSnap = System.Text.Json.JsonDocument.Parse(correction.OldSnapshot);
        using var newSnap = System.Text.Json.JsonDocument.Parse(correction.NewSnapshot);
        Assert.Equal(85.00m, oldSnap.RootElement.GetProperty("Amount").GetDecimal());
        Assert.Equal(80.00m, newSnap.RootElement.GetProperty("Amount").GetDecimal());

        // Journal bleibt konsistent.
        Assert.Empty(journal.AllocationInvariantViolations());
    }

    // ==================================================================
    // A2: Teilrückzahlung 10 €
    // ==================================================================

    [Fact(DisplayName = "A2: Teilrückzahlung 10 € — Konto +10 €, Ursprungspool +10 €, verknüpft, kein Einkommen")]
    public async Task A2_Refund_CreditsAccountAndOriginPool()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = "bk-a2",
            Date = new DateOnly(2026, 11, 5),
            AccountId = Step5Scenario.Haupt,
            TotalAmount = new Money(10_000),
            Splits =
            [
                new ExpenseSplitSpec { Id = "s1", Amount = new Money(10_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
            ],
        });

        var result = await db.Resolve<RefundService>().RegisterAsync(new RefundSpec(
            "bk-a2-ref", "bk-a2", Step5Scenario.Haupt,
            new DateOnly(2026, 11, 20),
            [new RefundLineSpec(Step5Scenario.Flex, new Money(1_000))],
            "Teilrückzahlung Händler"));

        Assert.True(result.Created);
        Assert.Equal(BookingType.Refund, result.Booking.Type);
        Assert.Equal("bk-a2", result.Booking.RefundsExpenseId);

        // Erstattungstag als Tatsachdatum.
        Assert.Equal(new DateOnly(2026, 11, 20), result.Booking.Date);

        // Konto +10 € (900 → 910), Ursprungspool +10 € (900 → 910).
        Assert.Equal(91_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
        Assert.Equal(91_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.Flex));

        // Die Ursprungs-Ausgabe bleibt unangetastet (kein Revisionseffekt).
        var original = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-a2");
        Assert.Equal(1, original!.BaseRevision);

        // Keine Einnahme: TotalIncome bleibt 0.
        var balances = await db.Resolve<BalanceQueryService>().GetBalancesAsync();
        Assert.Equal(Money.Zero, balances.TotalIncome);

        // Journal konsistent.
        var journal = await Step5Scenario.JournalAsync(db);
        Assert.Empty(journal.AllocationInvariantViolations());
    }

    // ==================================================================
    // A3: Korrektur einer beglichenen Ausgabe → nachvollziehbarer neuer
    //     Ausgleichsbedarf
    // ==================================================================

    [Fact(DisplayName = "A3: Korrektur einer beglichenen Ausgabe erzeugt einen nachvollziehbaren neuen Ausgleichsbedarf")]
    public async Task A3_CorrectSettledExpense_CreatesTraceableNewSettlement()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        // Ausgabe 20 € (Freizeit-Pool) vom Hauptkonto → 20 € Vorfinanzierung
        // (offene Position).
        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = "bk-a3",
            Date = new DateOnly(2026, 11, 5),
            AccountId = Step5Scenario.Haupt,
            TotalAmount = new Money(2_000),
            Splits =
            [
                new ExpenseSplitSpec { Id = "s1", Amount = new Money(2_000), PoolId = Step5Scenario.PoolFreizeit, CategoryId = "cat-freizeit" },
            ],
        });

        var open = Assert.Single(await db.Resolve<SettlementQueryService>().GetOpenAsync());
        Assert.Equal(new Money(2_000), open.RestAmount);

        // Begleichung: 20 € Rückfluss (Freizeit → Haupt).
        await db.Resolve<TransferService>().CreateAsync(new TransferSpec
        {
            Id = "bk-a3-set",
            Date = new DateOnly(2026, 11, 6),
            FromAccountId = Step5Scenario.Freizeit,
            ToAccountId = Step5Scenario.Haupt,
            Amount = new Money(2_000),
            Purposes = [new PoolAllocation(Step5Scenario.PoolFreizeit, new Money(2_000))],
            RelatedSettlementId = open.Id,
        });
        Assert.Empty(await db.Resolve<SettlementQueryService>().GetOpenAsync());

        // Korrektur: 20 € → 25 € (Freizeit-Anteil steigt um 5 €).
        var result = await db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
            "bk-a3", "op-a3", ExpectedBaseRevision: 1,
            TotalAmount: new Money(2_500),
            Splits:
            [
                new ExpenseSplitSpec { Id = "s1", Amount = new Money(2_500), PoolId = Step5Scenario.PoolFreizeit, CategoryId = "cat-freizeit" },
            ]));
        Assert.True(result.Created);

        // Nachvollziehbarer neuer Ausgleichsbedarf: 5 € offen, begründet von
        // der korrigierten Ausgabe (keine stille Änderung).
        var reopened = Assert.Single(await db.Resolve<SettlementQueryService>().GetOpenAsync());
        Assert.Equal(Step5Scenario.Haupt, reopened.SourceAccountId);
        Assert.Equal(Step5Scenario.PoolFreizeit, reopened.TargetPoolId);
        Assert.Equal(new Money(500), reopened.RestAmount);
        Assert.Equal("bk-a3", reopened.CreatedByBookingId);

        // Die Korrektur ist in der Historie dokumentiert.
        Assert.NotNull(await db.Resolve<IBookingRepository>().FindCorrectionAsync("op-a3"));

        // Salden: Haupt 995, Freizeit-Pool 275, Freizeit-Konto 280.
        Assert.Equal(99_500, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
        Assert.Equal(27_500, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.PoolFreizeit));
        Assert.Equal(28_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Freizeit));

        var journal = await Step5Scenario.JournalAsync(db);
        Assert.Empty(journal.AllocationInvariantViolations());
    }

    // ==================================================================
    // A4: Abgleichdifferenz per Kontostandskorrektur
    // ==================================================================

    [Fact(DisplayName = "A4: Kontostandskorrektur 5 € — Konto korrigiert, kein Einkommen, Differenz sichtbar")]
    public async Task A4_BalanceCorrection_DocumentedDifference_NotIncome()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        var result = await db.Resolve<BalanceCorrectionService>().CorrectBalanceAsync(
            new BalanceCorrectionSpec(
                "bk-a4", Step5Scenario.Haupt, new Money(500),
                new DateOnly(2026, 11, 10),
                "Bankdifferenz 14.11.: 5 € Guthaben auf der Bank"));

        Assert.True(result.Created);
        Assert.Equal(BookingType.BalanceCorrection, result.Booking.Type);

        // Konto auf tatsächlichen Stand korrigiert (1.000 → 1.005).
        Assert.Equal(100_500, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));

        // Pools unverändert (KEIN Pool-Posting).
        Assert.Equal(100_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.Flex));
        Assert.Equal(30_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.PoolFreizeit));

        // Nicht als Einkommen gezählt; die Differenz bleibt sichtbar als
        // Konto-Pool-Lücke (5 €), nicht still einem Pool zugeordnet.
        var balances = await db.Resolve<BalanceQueryService>().GetBalancesAsync();
        Assert.Equal(Money.Zero, balances.TotalIncome);
        Assert.Equal(new Money(500), balances.AccountPoolGap);

        // Dokumentation: Notiz ist gesetzt.
        Assert.NotNull(result.Booking.Note);

        var journal = await Step5Scenario.JournalAsync(db);
        Assert.Empty(journal.AllocationInvariantViolations());
    }

    // ==================================================================
    // B1: Storno — Gegenbewegung, Verknüpfung, Doppelstorno abgelehnt
    // ==================================================================

    [Fact(DisplayName = "B1: Storno einer Ausgabe = Gegenbewegung, verknüpft; Doppelstorno abgelehnt")]
    public async Task B1_Reversal_CounterEntry_DoubleReversalRejected()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = "bk-b1",
            Date = new DateOnly(2026, 11, 5),
            AccountId = Step5Scenario.Haupt,
            TotalAmount = new Money(5_000),
            Splits =
            [
                new ExpenseSplitSpec { Id = "s1", Amount = new Money(5_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
            ],
        });
        Assert.Equal(95_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));

        // Storno: Gegenbewegung auf Konto und Pool.
        var result = await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-b1-st", "bk-b1", ExpectedBaseRevision: 1,
            new DateOnly(2026, 11, 6), "Storno"));

        Assert.True(result.Created);
        Assert.Equal(BookingType.Reversal, result.Booking.Type);
        Assert.Equal("bk-b1", result.Booking.ReversesBookingId);

        // Salden zurück auf Start (Gegenbewegung).
        Assert.Equal(100_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
        Assert.Equal(100_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.Flex));

        // Original trägt Revision 2 (Revision++ atomar mit dem Storno).
        var original = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-b1");
        Assert.Equal(2, original!.BaseRevision);

        // Storno-Deltas == Negation der Original-Deltas.
        var originalDeltas = original.AllocationDelta();
        Assert.Equal(
            originalDeltas.Select(d => (d.AccountId, d.PoolId, d.Amount.Negate()))
                .OrderBy(d => d.PoolId),
            result.Booking.ReversalDeltas.Select(d => (d.AccountId, d.PoolId, d.Amount))
                .OrderBy(d => d.PoolId));

        // Doppelstorno (veraltete Erwartungsrevision 1 ≠ Ist 2):
        // Revisionskonflikt, kein Partial State.
        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() =>
            db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
                "bk-b1-st2", "bk-b1", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 6))));
        Assert.Equal(1, conflict.ExpectedBaseRevision);
        Assert.Equal(2, conflict.ActualBaseRevision);

        // Doppelstorno (aktuelle Revision 2): ebenfalls abgelehnt.
        await Assert.ThrowsAsync<DomainConflictException>(() =>
            db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
                "bk-b1-st3", "bk-b1", ExpectedBaseRevision: 2, new DateOnly(2026, 11, 6))));

        // Kein Partial State: weiterhin genau ein Storno, Original Rev 2.
        var journal = await Step5Scenario.JournalAsync(db);
        Assert.Single(journal.Bookings, b => b.Type == BookingType.Reversal);
        var unchanged = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-b1");
        Assert.Equal(2, unchanged!.BaseRevision);
        Assert.Empty(journal.AllocationInvariantViolations());
    }

    [Fact(DisplayName = "B1: Widerruf = Storno des Stornos (revisionsgerecht, erlaubt)")]
    public async Task B1_ReversalOfReversal_Revokes()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = "bk-b1b",
            Date = new DateOnly(2026, 11, 5),
            AccountId = Step5Scenario.Haupt,
            TotalAmount = new Money(5_000),
            Splits =
            [
                new ExpenseSplitSpec { Id = "s1", Amount = new Money(5_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
            ],
        });

        var first = await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-b1b-st", "bk-b1b", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 6)));
        Assert.True(first.Created);

        // Widerruf: Storno auf die Storno-Buchung (ihre Revision ist 1).
        var revoke = await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-b1b-st2", "bk-b1b-st", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 7), "Widerruf"));
        Assert.True(revoke.Created);
        Assert.Equal("bk-b1b-st", revoke.Booking.ReversesBookingId);

        // Die ursprüngliche Ausgabe wirkt wieder (Salden zurück auf 95 €).
        Assert.Equal(95_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
        Assert.Equal(95_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.Flex));

        var journal = await Step5Scenario.JournalAsync(db);
        Assert.Empty(journal.AllocationInvariantViolations());
    }

    // ==================================================================
    // B2: Erstattung auf stornierte Ausgabe abgelehnt
    // ==================================================================

    [Fact(DisplayName = "B2: Erstattung auf stornierte Ausgabe abgelehnt (Storno ≠ Erstattung)")]
    public async Task B2_RefundOnReversedExpense_Rejected()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = "bk-b2",
            Date = new DateOnly(2026, 11, 5),
            AccountId = Step5Scenario.Haupt,
            TotalAmount = new Money(5_000),
            Splits =
            [
                new ExpenseSplitSpec { Id = "s1", Amount = new Money(5_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
            ],
        });

        await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-b2-st", "bk-b2", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 6)));

        // Erstattung auf die stornierte Ausgabe: abgelehnt (nichts zu erstatten).
        await Assert.ThrowsAsync<LarioDomainException>(() =>
            db.Resolve<RefundService>().RegisterAsync(new RefundSpec(
                "bk-b2-ref", "bk-b2", Step5Scenario.Haupt,
                new DateOnly(2026, 11, 20),
                [new RefundLineSpec(Step5Scenario.Flex, new Money(1_000))])));

        // Keine Erstattungs-Buchung angelegt.
        var journal = await Step5Scenario.JournalAsync(db);
        Assert.DoesNotContain(journal.Bookings, b => b.Type == BookingType.Refund);
    }

    // ==================================================================
    // B3: Erstattung übersteigt Splitbetrag → abgelehnt (Deckelung je Pool)
    // ==================================================================

    [Fact(DisplayName = "B3: Erstattung über den Splitbetrag (50 €) abgelehnt")]
    public async Task B3_RefundExceedingSplit_Rejected()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = "bk-b3",
            Date = new DateOnly(2026, 11, 5),
            AccountId = Step5Scenario.Haupt,
            TotalAmount = new Money(5_000),
            Splits =
            [
                new ExpenseSplitSpec { Id = "s1", Amount = new Money(5_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
            ],
        });

        // 60 € > Splitbetrag 50 € → abgelehnt.
        await Assert.ThrowsAsync<LarioDomainException>(() =>
            db.Resolve<RefundService>().RegisterAsync(new RefundSpec(
                "bk-b3-ref", "bk-b3", Step5Scenario.Haupt,
                new DateOnly(2026, 11, 20),
                [new RefundLineSpec(Step5Scenario.Flex, new Money(6_000))])));

        // Keine Erstattungs-Buchung.
        var journal = await Step5Scenario.JournalAsync(db);
        Assert.DoesNotContain(journal.Bookings, b => b.Type == BookingType.Refund);
    }

    [Fact(DisplayName = "B3: Teilrückzahlungen akkumulieren — zweite über Deckelung abgelehnt")]
    public async Task B3_RefundAccumulation_SecondExceedingCap_Rejected()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = "bk-b3b",
            Date = new DateOnly(2026, 11, 5),
            AccountId = Step5Scenario.Haupt,
            TotalAmount = new Money(5_000),
            Splits =
            [
                new ExpenseSplitSpec { Id = "s1", Amount = new Money(5_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
            ],
        });

        // Erste Rückzahlung 30 €: erlaubt (30 ≤ 50).
        var first = await db.Resolve<RefundService>().RegisterAsync(new RefundSpec(
            "bk-b3b-ref1", "bk-b3b", Step5Scenario.Haupt,
            new DateOnly(2026, 11, 15),
            [new RefundLineSpec(Step5Scenario.Flex, new Money(3_000))]));
        Assert.True(first.Created);

        // Zweite Rückzahlung 25 €: 30 + 25 = 55 > 50 → abgelehnt.
        await Assert.ThrowsAsync<LarioDomainException>(() =>
            db.Resolve<RefundService>().RegisterAsync(new RefundSpec(
                "bk-b3b-ref2", "bk-b3b", Step5Scenario.Haupt,
                new DateOnly(2026, 11, 20),
                [new RefundLineSpec(Step5Scenario.Flex, new Money(2_500))])));

        // Nur die erste Erstattung existiert.
        var journal = await Step5Scenario.JournalAsync(db);
        Assert.Single(journal.Bookings, b => b.Type == BookingType.Refund);
        Assert.Equal(98_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
    }

    // ==================================================================
    // B4: Revisionskonflikt — Fehler, kein Partial State, keine Revisionserhöhung
    // ==================================================================

    [Fact(DisplayName = "B4: Revisionskonflikt bei Korrektur — Fehler, kein Partial State, Revision bleibt")]
    public async Task B4_RevisionConflict_Correction_NoPartialState()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = "bk-b4",
            Date = new DateOnly(2026, 11, 5),
            AccountId = Step5Scenario.Haupt,
            TotalAmount = new Money(5_000),
            Splits =
            [
                new ExpenseSplitSpec { Id = "s1", Amount = new Money(5_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
            ],
        });

        // Korrektur mit abweichender Erwartungsrevision (99 ≠ 1).
        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() =>
            db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
                "bk-b4", "op-b4", ExpectedBaseRevision: 99,
                TotalAmount: new Money(4_000),
                Splits:
                [
                    new ExpenseSplitSpec { Id = "s1", Amount = new Money(4_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
                ])));

        Assert.Equal(99, conflict.ExpectedBaseRevision);
        Assert.Equal(1, conflict.ActualBaseRevision);

        // Kein Partial State: keine Historie, Original unverändert (Rev 1,
        // Betrag 50 €), Salden unverändert.
        Assert.Null(await db.Resolve<IBookingRepository>().FindCorrectionAsync("op-b4"));
        var unchanged = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-b4");
        Assert.Equal(1, unchanged!.BaseRevision);
        Assert.Equal(new Money(-5_000), unchanged.AccountPostings.Single().Amount);
        Assert.Equal(95_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
    }

    // ==================================================================
    // B5: Atomarität — Abbruch hinterlässt keine halbe Buchung
    // ==================================================================

    [Fact(DisplayName = "B5: Atomarität — abgelehnte Operation verändert nichts, Salden bleiben intakt")]
    public async Task B5_Atomicity_RejectedOperationLeavesNoPartialState()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await Step5Scenario.AddMixedPurchaseAsync(db);

        var before = await db.Resolve<BalanceQueryService>().GetBalancesAsync();

        // Abgelehnte Korrektur (Splitregel verletzt: Splits addieren nicht
        // zum Gesamtbetrag).
        await Assert.ThrowsAsync<LarioDomainException>(() =>
            db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
                "bk-2026-1202", "op-b5", ExpectedBaseRevision: 1,
                TotalAmount: new Money(8_000),
                Splits:
                [
                    new ExpenseSplitSpec { Id = "s1", Amount = new Money(6_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
                    new ExpenseSplitSpec { Id = "s2", Amount = new Money(1_000), PoolId = Step5Scenario.PoolFreizeit, CategoryId = "cat-freizeit" },
                ])));

        // Salden unverändert, kein Historieneintrag, Zeilen-/Spaltensummen
        // intakt.
        var after = await db.Resolve<BalanceQueryService>().GetBalancesAsync();
        Assert.Equal(before.TotalAccountBalance, after.TotalAccountBalance);
        Assert.Equal(before.TotalPoolBalance, after.TotalPoolBalance);
        Assert.Null(await db.Resolve<IBookingRepository>().FindCorrectionAsync("op-b5"));

        var journal = await Step5Scenario.JournalAsync(db);
        Assert.Empty(journal.AllocationInvariantViolations());
        var booking = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-2026-1202");
        Assert.Equal(1, booking!.BaseRevision);
    }

    // ==================================================================
    // B6: Korrektur ändert keine historischen Steuerkennzeichnungen anderer
    //     Buchungen; die der korrigierten Buchung bleibt nachvollziehbar
    // ==================================================================

    [Fact(DisplayName = "B6: Korrektur — Steuerkennzeichnung bleibt an den Splits, andere Buchungen unangetastet")]
    public async Task B6_Correction_KeepsTaxMarking_OtherBookingsUntouched()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        // Ausgabe 100 € mit steuerlich relevantem Teilbetrag (40 € markiert).
        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = "bk-b6",
            Date = new DateOnly(2026, 11, 5),
            AccountId = Step5Scenario.Haupt,
            TotalAmount = new Money(10_000),
            Splits =
            [
                new ExpenseSplitSpec
                {
                    Id = "s1",
                    Amount = new Money(10_000),
                    PoolId = Step5Scenario.Flex,
                    CategoryId = "cat-lebensmittel",
                    Tax = new TaxMarking { Relevant = true, MarkedAmount = new Money(4_000) },
                },
            ],
        });

        // Unabhängige zweite Ausgabe mit eigener Steuerkennzeichnung.
        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = "bk-b6-other",
            Date = new DateOnly(2026, 11, 6),
            AccountId = Step5Scenario.Haupt,
            TotalAmount = new Money(3_000),
            Splits =
            [
                new ExpenseSplitSpec
                {
                    Id = "s1",
                    Amount = new Money(3_000),
                    PoolId = Step5Scenario.Flex,
                    CategoryId = "cat-freizeit",
                    Tax = new TaxMarking { Relevant = true, MarkedAmount = new Money(1_000) },
                },
            ],
        });

        // Korrektur von bk-b6: 100 € → 80 €, Teilbetrag bleibt 40 € markiert.
        var result = await db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
            "bk-b6", "op-b6", ExpectedBaseRevision: 1,
            TotalAmount: new Money(8_000),
            Splits:
            [
                new ExpenseSplitSpec
                {
                    Id = "s1",
                    Amount = new Money(8_000),
                    PoolId = Step5Scenario.Flex,
                    CategoryId = "cat-lebensmittel",
                    Tax = new TaxMarking { Relevant = true, MarkedAmount = new Money(4_000) },
                },
            ]));
        Assert.True(result.Created);

        // Steuerkennzeichnung bleibt an der korrigierten Buchung:
        // markierter Teilbetrag 40 € ≤ Splitbetrag 80 €.
        var corrected = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-b6");
        var split = Assert.Single(corrected!.Splits);
        Assert.Equal(new Money(4_000), split.Tax!.MarkedAmount);
        Assert.True(split.Tax!.MarkedAmount <= split.Amount);

        // Die andere Buchung ist unangetastet (eigene Kennzeichnung 10 €).
        var other = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-b6-other");
        var otherSplit = Assert.Single(other!.Splits);
        Assert.Equal(new Money(1_000), otherSplit.Tax!.MarkedAmount);
        Assert.Equal(1, other.BaseRevision);
    }

    // ==================================================================
    // Idempotenz (Muster der Schritt-4/5-Services)
    // ==================================================================

    [Fact(DisplayName = "Idempotenz: Storno-/Erstattungs-/Korrektur-Wiederholung wird nur quittiert")]
    public async Task Idempotency_RepeatedOperations_AcknowledgedNotDuplicated()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = "bk-idem",
            Date = new DateOnly(2026, 11, 5),
            AccountId = Step5Scenario.Haupt,
            TotalAmount = new Money(5_000),
            Splits =
            [
                new ExpenseSplitSpec { Id = "s1", Amount = new Money(5_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
            ],
        });

        // Storno: erste Ausführung erstellt, zweite quittiert (gleicher Inhalt).
        var rev1 = await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-idem-st", "bk-idem", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 6)));
        Assert.True(rev1.Created);
        var rev2 = await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-idem-st", "bk-idem", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 6)));
        Assert.False(rev2.Created);
        Assert.Equal("bk-idem-st", rev2.Booking.Id);
        Assert.Equal("bk-idem", rev2.Booking.ReversesBookingId);

        // Korrektur: gleiche Operation-Id mit gleichem Inhalt wird quittiert.
        // (bk-idem ist storniert — auf der Storno-Buchung ist keine
        // Korrektur möglich; daher separate Ausgabe.)
        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = "bk-idem2",
            Date = new DateOnly(2026, 11, 5),
            AccountId = Step5Scenario.Haupt,
            TotalAmount = new Money(5_000),
            Splits =
            [
                new ExpenseSplitSpec { Id = "s1", Amount = new Money(5_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
            ],
        });
        var corr1 = await db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
            "bk-idem2", "op-idem", ExpectedBaseRevision: 1,
            TotalAmount: new Money(4_000),
            Splits:
            [
                new ExpenseSplitSpec { Id = "s1", Amount = new Money(4_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
            ]));
        Assert.True(corr1.Created);
        var corr2 = await db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
            "bk-idem2", "op-idem", ExpectedBaseRevision: 2,
            TotalAmount: new Money(4_000),
            Splits:
            [
                new ExpenseSplitSpec { Id = "s1", Amount = new Money(4_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
            ]));
        Assert.False(corr2.Created);
        Assert.Equal(2, corr2.Booking.BaseRevision); // Revision nicht erneut erhöht.

        var journal = await Step5Scenario.JournalAsync(db);
        Assert.Single(journal.Bookings, b => b.Type == BookingType.Reversal);
        Assert.Empty(journal.AllocationInvariantViolations());
    }
}
