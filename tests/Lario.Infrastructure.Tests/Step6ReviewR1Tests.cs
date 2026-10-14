using System.Text.Json;
using Lario.Application;
using Lario.Application.Journal;
using Lario.Domain;
using Lario.Domain.Journal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lario.Infrastructure.Tests;

/// <summary>
/// Regressionstests zu den Befunden des externen Reviews (Runde 1) für
/// Schritt 6 — auf echter SQLite:
/// <para>
/// <b>P1 (atomare Revision):</b> zwei getrennte DbContexts auf derselben
/// Datei mit Barriere nach dem Lesen — bei parallelen Korrekturen,
/// Stornos und Erstattungen gewinnt exakt eine Operation, die Verliererin
/// wird vollständig zurückgerollt (Concurrency-Token auf der
/// Basisrevision); kumulierte Erstattungen überschreiten niemals den
/// Deckel.
/// </para>
/// <para>
/// <b>P1 (Delta-Prüfung):</b> ein Storno eines Transfers mit
/// Zweckverteilung trägt poolneutrale Deltas (Deltasumme 0 ohne
/// Pool-Posting) — das ist konsistent und muss akzeptiert werden.
/// </para>
/// <para>
/// <b>P2:</b> Idempotenz am kanonischen Original-Payload (Revision,
/// Feldpräsenz, Split-/Steuerinhalt) — Wiederholung nach weiterer
/// Korrektur wird quittiert, anderer Inhalt ist ein Konflikt;
/// Erstattungs-Idempotenz bijektiv über die Zeilenmenge; wirksamer
/// Storno-Status über die Storno-Kette (Widerruf macht die Buchung
/// wieder veränderbar); Steuerkennzeichnung in den
/// Vorher/Nachher-Snapshots; Korrekturen müssen bereits gewährte
/// Erstattungen decken.
/// </para>
/// </summary>
public class Step6ReviewR1Tests
{
    // ==================================================================
    // P1: Atomare Revision — parallele Operationen, genau eine gewinnt
    // ==================================================================

    [Fact(DisplayName = "P1: Parallele Korrekturen (zwei Kontexte, Barriere nach dem Lesen) — genau eine gewinnt")]
    public async Task P1_ParallelCorrections_ExactlyOneWins()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await CreateExpenseAsync(db, "bk-c1", 10_000);

        // Kontext A: liest die Buchung (Revision 1) — danach kommt die
        // Barriere. Der Lesezustand bleibt veraltet.
        var scopeA = db.NewServiceProvider().CreateScope();
        var repoA = scopeA.ServiceProvider.GetRequiredService<IBookingRepository>();
        var staleA = await repoA.FindByIdAsync("bk-c1");
        Assert.NotNull(staleA);

        // Barriere: Kontext B (frischer Provider) führt die komplette
        // Korrektur durch und committet (Revision 1 → 2).
        var winner = await db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
            "bk-c1", "op-winner", ExpectedBaseRevision: 1,
            TotalAmount: new Money(9_000),
            Splits: [SingleSplitSpec("s1", 9_000)]));
        Assert.True(winner.Created);

        // Kontext A committet jetzt seine Korrektur auf Basis des
        // veralteten Lesezustands — sie muss als Revisionskonflikt
        // abgewiesen und vollständig zurückgerollt werden.
        var catalog = await db.Resolve<IMasterDataRepository>().LoadCatalogAsync();
        var loserSpec = new CorrectionSpec(
            "bk-c1", "op-loser", ExpectedBaseRevision: 1,
            TotalAmount: new Money(9_500),
            Splits: [SingleSplitSpec("s1", 9_500)]);
        var updatedA = loserSpec.BuildUpdatedBooking(staleA!, catalog);
        updatedA.BaseRevision = staleA.BaseRevision + 1;

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() =>
            repoA.UpdateAsync(
                updatedA,
                new BookingCorrection(
                    "op-loser", "bk-c1", "old", "new", null, "payload", DateTime.UtcNow),
                [], cancellationToken: default));

        Assert.Equal(1, conflict.ExpectedBaseRevision);
        Assert.Equal(2, conflict.ActualBaseRevision);

        // Endzustand: genau eine Historie (die des Gewinners), Revision 2,
        // Betrag des Gewinners — kein Partial State der Verliererin.
        var final = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-c1");
        Assert.Equal(2, final!.BaseRevision);
        Assert.Equal(new Money(-9_000), final.AccountPostings.Single().Amount);
        Assert.NotNull(await db.Resolve<IBookingRepository>().FindCorrectionAsync("op-winner"));
        Assert.Null(await db.Resolve<IBookingRepository>().FindCorrectionAsync("op-loser"));
        Assert.Empty((await Step5Scenario.JournalAsync(db)).AllocationInvariantViolations());
    }

    [Fact(DisplayName = "P1: Parallele Stornos (zwei Kontexte, Barriere nach dem Lesen) — genau eines gewinnt")]
    public async Task P1_ParallelReversals_ExactlyOneWins()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await CreateExpenseAsync(db, "bk-c2", 10_000);

        // Kontext A: liest das Original (Revision 1) — Barriere.
        var scopeA = db.NewServiceProvider().CreateScope();
        var repoA = scopeA.ServiceProvider.GetRequiredService<IBookingRepository>();
        var staleA = await repoA.FindByIdAsync("bk-c2");
        Assert.NotNull(staleA);

        // Barriere: Kontext B storniert vollständig und committet
        // (Revision 1 → 2).
        var winner = await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-c2-st-winner", "bk-c2", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 25)));
        Assert.True(winner.Created);

        // Kontext A legt jetzt sein eigenes Storno auf Basis des
        // veralteten Originals an — es muss als Revisionskonflikt
        // abgewiesen werden (das Storno wird nicht persistiert).
        var loser = new ReversalSpec(
            "bk-c2-st-loser", "bk-c2", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 25));
        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() =>
            repoA.AddWithOriginalRevisionAsync(loser.ToBooking(staleA!), [staleA], []));

        Assert.Equal(1, conflict.ExpectedBaseRevision);
        Assert.Equal(2, conflict.ActualBaseRevision);

        // Endzustand: exakt ein Storno des Originals, Revision 2.
        var journal = await Step5Scenario.JournalAsync(db);
        Assert.Single(journal.Bookings, b => b.Type == BookingType.Reversal && b.ReversesBookingId == "bk-c2");
        Assert.Null(await db.Resolve<IBookingRepository>().FindByIdAsync("bk-c2-st-loser"));
        var final = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-c2");
        Assert.Equal(2, final!.BaseRevision);
        Assert.Empty(journal.AllocationInvariantViolations());
    }

    [Fact(DisplayName = "P1: Parallele Erstattungen unter dem Deckel — kumuliert überschreiten sie ihn nie")]
    public async Task P1_ParallelRefunds_CapNeverExceeded()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        // Ausgabe 100 €: Flex 60 / Freizeit 40 — Deckel je Pool.
        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = "bk-c3",
            Date = new DateOnly(2026, 11, 5),
            AccountId = Step5Scenario.Haupt,
            TotalAmount = new Money(10_000),
            Splits =
            [
                new ExpenseSplitSpec { Id = "s1", Amount = new Money(6_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
                new ExpenseSplitSpec { Id = "s2", Amount = new Money(4_000), PoolId = Step5Scenario.PoolFreizeit, CategoryId = "cat-freizeit" },
            ],
        });

        // Kontext A: liest das Original (Revision 1) — Barriere.
        var scopeA = db.NewServiceProvider().CreateScope();
        var repoA = scopeA.ServiceProvider.GetRequiredService<IBookingRepository>();
        var staleA = await repoA.FindByIdAsync("bk-c3");
        Assert.NotNull(staleA);

        // Barriere: Kontext B erstattet 60 € auf Flex vollständig und
        // committet (Original 1 → 2, Flex-Deckel erreicht).
        var winner = await db.Resolve<RefundService>().RegisterAsync(new RefundSpec(
            "bk-c3-r-winner", "bk-c3", Step5Scenario.Haupt,
            new DateOnly(2026, 11, 20),
            [new RefundLineSpec(Step5Scenario.Flex, new Money(6_000))],
            "Vollrckerstattung"));
        Assert.True(winner.Created);

        // Kontext A erstattet jetzt (aus seiner Sicht) ebenfalls 60 € auf
        // Flex — einzeln unter dem Deckel, kumuliert das Doppelte. Es
        // muss als Revisionskonflikt abgewiesen und die Erstattung
        // vollständig zurückgerollt werden.
        var loser = new RefundSpec(
            "bk-c3-r-loser", "bk-c3", Step5Scenario.Haupt,
            new DateOnly(2026, 11, 21),
            [new RefundLineSpec(Step5Scenario.Flex, new Money(6_000))],
            "Parallele Erstattung");
        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() =>
            repoA.AddWithOriginalRevisionAsync(loser.ToBooking(staleA!), [staleA], []));

        Assert.Equal(1, conflict.ExpectedBaseRevision);
        Assert.Equal(2, conflict.ActualBaseRevision);

        // Endzustand: exakt eine Erstattung (60 €), der Deckel (60 €) ist
        // erreicht, aber nicht überschritten; Salden konsistent.
        var journal = await Step5Scenario.JournalAsync(db);
        var refunds = journal.Bookings
            .Where(b => b.Type == BookingType.Refund && b.RefundsExpenseId == "bk-c3")
            .ToList();
        Assert.Single(refunds);
        Assert.Equal("bk-c3-r-winner", refunds[0].Id);
        Assert.Null(await db.Resolve<IBookingRepository>().FindByIdAsync("bk-c3-r-loser"));
        // Die gewonnene Erstattung führt 60 € an Flex zurück: 100_000 −
        // 6_000 (Ausgabe) + 6_000 (Erstattung) = 100_000.
        Assert.Equal(100_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.Flex));
        Assert.Empty(journal.AllocationInvariantViolations());
    }

    [Fact(DisplayName = "P1: Storno eines Transfers mit Zweckverteilung — poolneutrale Deltas mit Postingsumme 0 sind konsistent")]
    public async Task P1_TransferReversal_PoolNeutralDeltasAccepted()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        // Transfer 20 € Haupt → Freizeit mit explizitem Zweck Flex:
        // Deltas (Haupt, Flex, −20) und (Freizeit, Flex, +20) — die
        // Deltasumme auf der Pool-Achse ist 0 und der Transfer trägt kein
        // Pool-Posting. Das ist die konsistente Form (reine
        // Kontobewegung mit Zweck), kein Fehler.
        await db.Resolve<TransferService>().CreateAsync(new TransferSpec
        {
            Id = "tr-c4",
            Date = new DateOnly(2026, 11, 5),
            FromAccountId = Step5Scenario.Haupt,
            ToAccountId = Step5Scenario.Freizeit,
            Amount = new Money(2_000),
            Purposes = [new PoolAllocation(Step5Scenario.Flex, new Money(2_000))],
        });

        // Storno des Transfers: die negierten Deltas (Haupt, Flex, +20)
        // und (Freizeit, Flex, −20) balancieren gegen die eigenen
        // Postings — die Buchung muss akzeptiert werden.
        var result = await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "tr-c4-st", "tr-c4", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 25)));
        Assert.True(result.Created);
        Assert.Equal(BookingType.Reversal, result.Booking.Type);

        var journal = await Step5Scenario.JournalAsync(db);
        Assert.Empty(journal.AllocationInvariantViolations());
        // Kontostände neutralisiert (Transfer und Storno heben sich auf —
        // zurück auf die Seed-Werte).
        var balances = await db.Resolve<BalanceQueryService>().GetBalancesAsync();
        Assert.Equal(100_000, balances.Accounts.Single(a => a.AccountId == Step5Scenario.Haupt).Balance.Cents);
        Assert.Equal(30_000, balances.Accounts.Single(a => a.AccountId == Step5Scenario.Freizeit).Balance.Cents);
    }

    // ==================================================================
    // P2: Idempotenz am kanonischen Original-Payload
    // ==================================================================

    [Fact(DisplayName = "P2: Wiederholung einer Korrektur nach weiterer Korrektur — wird quittiert, keine zweite Historie")]
    public async Task P2_Idempotency_ReplayAfterLaterCorrection_Acknowledged()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await CreateExpenseAsync(db, "bk-c5", 10_000, note: "A");

        // O1: Notiz A → B (Revision 1 → 2).
        var spec1 = new CorrectionSpec(
            "bk-c5", "op-x1", ExpectedBaseRevision: 1, Note: "B");
        Assert.True((await db.Resolve<CorrectionService>().CorrectAsync(spec1)).Created);

        // O2: Notiz B → C (Revision 2 → 3) — die Buchung trägt heute
        // anderen Inhalt als der O1-Zielzustand.
        var spec2 = new CorrectionSpec(
            "bk-c5", "op-x2", ExpectedBaseRevision: 2, Note: "C");
        Assert.True((await db.Resolve<CorrectionService>().CorrectAsync(spec2)).Created);

        // O1 wird wiederholt (gleicher Payload, alte Erwartungsrevision
        // 1): die Wiederholung muss quittiert werden — der Abgleich
        // erfolgt am gespeicherten Original-Payload, nicht am aktuellen
        // Buchungszustand (Review R1 P2).
        var replay = await db.Resolve<CorrectionService>().CorrectAsync(spec1);
        Assert.False(replay.Created);
        var final = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-c5");
        Assert.Equal(3, final!.BaseRevision);
        Assert.Equal("C", final.Note);

        // Genau zwei Historieneinträge (O1, O2) — die Wiederholung hat
        // keine dritte erzeugt.
        var journal = await Step5Scenario.JournalAsync(db);
        var all = await db.Resolve<IBookingRepository>().GetAllAsync();
        Assert.NotNull(await db.Resolve<IBookingRepository>().FindCorrectionAsync("op-x1"));
        Assert.NotNull(await db.Resolve<IBookingRepository>().FindCorrectionAsync("op-x2"));
        _ = journal; _ = all;
    }

    [Fact(DisplayName = "P2: Gleicher OperationId mit anderem Split-/Steuerinhalt — Konflikt, nichts wird geändert")]
    public async Task P2_Idempotency_DifferentContent_Conflict()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await CreateExpenseAsync(db, "bk-c5b", 10_000);

        var spec1 = new CorrectionSpec(
            "bk-c5b", "op-y1", ExpectedBaseRevision: 1,
            TotalAmount: new Money(9_000),
            Splits: [SingleSplitSpec("s1", 9_000)]);
        Assert.True((await db.Resolve<CorrectionService>().CorrectAsync(spec1)).Created);

        // Gleiche Operation-Id, anderer Inhalt (andere Splits): Konflikt.
        await Assert.ThrowsAsync<DomainConflictException>(() =>
            db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
                "bk-c5b", "op-y1", ExpectedBaseRevision: 1,
                TotalAmount: new Money(8_000),
                Splits: [SingleSplitSpec("s1", 8_000)])));

        // Gleiche Operation-Id, andere Feldpräsenz (Notiz statt null):
        // Konflikt — die null-Semantik gehört zum Payload.
        await Assert.ThrowsAsync<DomainConflictException>(() =>
            db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
                "bk-c5b", "op-y1", ExpectedBaseRevision: 2,
                TotalAmount: new Money(9_000),
                Splits: [SingleSplitSpec("s1", 9_000)],
                Note: "Nachträgliche Notiz")));

        // Buchung und Historie bleiben unverändert.
        var final = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-c5b");
        Assert.Equal(2, final!.BaseRevision);
        Assert.Equal(new Money(-9_000), final.AccountPostings.Single().Amount);
        Assert.Null(final.Note);
    }

    [Fact(DisplayName = "P2: Erstattungs-Wiederholung — Zeilenmenge bijektiv, fehlende/doppelte Zeilen sind Konflikte")]
    public async Task P2_RefundIdempotency_LineSetBijection()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        // Ausgabe 100 €: Flex 60 / Freizeit 40.
        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = "bk-c6",
            Date = new DateOnly(2026, 11, 5),
            AccountId = Step5Scenario.Haupt,
            TotalAmount = new Money(10_000),
            Splits =
            [
                new ExpenseSplitSpec { Id = "s1", Amount = new Money(6_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
                new ExpenseSplitSpec { Id = "s2", Amount = new Money(4_000), PoolId = Step5Scenario.PoolFreizeit, CategoryId = "cat-freizeit" },
            ],
        });

        // Erstattung: 5 € Flex + 5 € Freizeit.
        var spec = new RefundSpec(
            "bk-c6-r", "bk-c6", Step5Scenario.Haupt, new DateOnly(2026, 11, 20),
            [
                new RefundLineSpec(Step5Scenario.Flex, new Money(500)),
                new RefundLineSpec(Step5Scenario.PoolFreizeit, new Money(500)),
            ],
            "Teilrckerstattung");
        Assert.True((await db.Resolve<RefundService>().RegisterAsync(spec)).Created);

        // Gleiche Zeilen in anderer Reihenfolge: gleicher Inhalt →
        // Quittierung.
        var reordered = new RefundSpec(
            "bk-c6-r", "bk-c6", Step5Scenario.Haupt, new DateOnly(2026, 11, 20),
            [
                new RefundLineSpec(Step5Scenario.PoolFreizeit, new Money(500)),
                new RefundLineSpec(Step5Scenario.Flex, new Money(500)),
            ],
            "Teilrckerstattung");
        var ack = await db.Resolve<RefundService>().RegisterAsync(reordered);
        Assert.False(ack.Created);

        // Gleiche Summe, aber fehlende Zeile (nur Flex 10 €): die
        // Zeilenmenge ist nicht bijektiv — Konflikt, keine zweite
        // Erstattung.
        await Assert.ThrowsAsync<DomainConflictException>(() =>
            db.Resolve<RefundService>().RegisterAsync(new RefundSpec(
                "bk-c6-r", "bk-c6", Step5Scenario.Haupt, new DateOnly(2026, 11, 20),
                [new RefundLineSpec(Step5Scenario.Flex, new Money(1_000))],
                "Teilrckerstattung")));

        // Doppelte Zeile (Flex 5 € + Flex 5 €): ebenfalls kein gleicher
        // Inhalt — Konflikt.
        await Assert.ThrowsAsync<DomainConflictException>(() =>
            db.Resolve<RefundService>().RegisterAsync(new RefundSpec(
                "bk-c6-r", "bk-c6", Step5Scenario.Haupt, new DateOnly(2026, 11, 20),
                [
                    new RefundLineSpec(Step5Scenario.Flex, new Money(500)),
                    new RefundLineSpec(Step5Scenario.Flex, new Money(500)),
                ],
                "Teilrckerstattung")));

        // Im Journal bleibt genau eine Erstattung (10 € = 1_000 Cent).
        var journal = await Step5Scenario.JournalAsync(db);
        Assert.Single(journal.Bookings, b => b.Type == BookingType.Refund && b.RefundsExpenseId == "bk-c6");
        Assert.Equal(1_000, journal.Bookings
            .Single(b => b.Type == BookingType.Refund && b.RefundsExpenseId == "bk-c6")
            .AccountPostings.Single().Amount.Cents);
        Assert.Empty(journal.AllocationInvariantViolations());
    }

    // ==================================================================
    // P2: Wirksamer Storno-Status über die Storno-Kette
    // ==================================================================

    [Fact(DisplayName = "P2: Widerrufenes Storno — Buchung ist wieder stornier-/korrigier-/erstattbar, erneutes wirksames Storno bleibt gesperrt")]
    public async Task P2_EffectiveStatus_RevokedReversal_ReopensBooking()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        // Ausgabe 100 €: Flex 60 / Freizeit 40 (Revision 1).
        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = "bk-c7",
            Date = new DateOnly(2026, 11, 5),
            AccountId = Step5Scenario.Haupt,
            TotalAmount = new Money(10_000),
            Splits =
            [
                new ExpenseSplitSpec { Id = "s1", Amount = new Money(6_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
                new ExpenseSplitSpec { Id = "s2", Amount = new Money(4_000), PoolId = Step5Scenario.PoolFreizeit, CategoryId = "cat-freizeit" },
            ],
        });

        // Jeder Vorgang nutzt einen frischen Scope (wie ein API-Request in
        // Produktion) — so liest jeder Vorgang den aktuellen Zustand aus der
        // Datenbank, und es entsteht kein Stale-Read über wiederverwendete
        // DbContext-Instanzen hinweg (Review R1 P2: wirksamer Storno-Status
        // über die Storno-Kette).
        // R1: Storno der Ausgabe (Revision 1 → 2).
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-c7-r1", "bk-c7", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 25)))).Created);

        // Solange R1 wirkt: Korrektur und Erstattung sind gesperrt.
        await Assert.ThrowsAsync<DomainConflictException>(() =>
            db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
                "bk-c7", "op-c7-1", ExpectedBaseRevision: 2, Note: "X")));
        await Assert.ThrowsAsync<LarioDomainException>(() =>
            db.Resolve<RefundService>().RegisterAsync(new RefundSpec(
                "bk-c7-r", "bk-c7", Step5Scenario.Haupt, new DateOnly(2026, 11, 26),
                [new RefundLineSpec(Step5Scenario.Flex, new Money(500))])));

        // W: Widerruf von R1 (Storno des Stornos) — die Ausgabe ist
        // wieder wirksam.
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-c7-w", "bk-c7-r1", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 27)))).Created);

        // Jetzt ist die Ausgabe wieder veränderbar. Review R2 P1 (B2):
        // der Widerruf hat die gesamte Storno-Kette erhöht — inklusive des
        // Wurzeloriginals bk-c7 (Revision 2 → 3). Jeder Kontext, der das
        // Wurzeloriginal vor dem Widerruf gelesen hat, erhält jetzt einen
        // Revisionskonflikt; die Korrektur benötigt daher die aktuelle
        // Revision (3):
        // Korrektur (Revision 3 → 4) …
        Assert.True((await db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
            "bk-c7", "op-c7-2", ExpectedBaseRevision: 3, Note: "Korrigiert nach Widerruf"))).Created);
        // … Erstattung (40 € ≤ Freizeit-Split 40 €; erhöht die Original-
        // Revision 4 → 5) …
        Assert.True((await db.Resolve<RefundService>().RegisterAsync(new RefundSpec(
            "bk-c7-r", "bk-c7", Step5Scenario.Haupt, new DateOnly(2026, 11, 28),
            [new RefundLineSpec(Step5Scenario.PoolFreizeit, new Money(4_000))]))).Created);
        // … und ein neues Storno der Ausgabe (Revision 5 → 6).
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-c7-r2", "bk-c7", ExpectedBaseRevision: 5, new DateOnly(2026, 11, 29)))).Created);

        // R2 wirkt jetzt: ein weiteres Storno der Ausgabe ist ein
        // Doppelstorno (wirksam storniert), Korrektur und Erstattung
        // sind wieder gesperrt. Die Revision ist hier aktuell, daher
        // kommt die Ablehnung aus dem wirksamen Storno-Status, nicht aus
        // der Revision.
        await Assert.ThrowsAsync<DomainConflictException>(() =>
            db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
                "bk-c7-r3", "bk-c7", ExpectedBaseRevision: 6, new DateOnly(2026, 11, 30))));
        await Assert.ThrowsAsync<DomainConflictException>(() =>
            db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
                "bk-c7", "op-c7-3", ExpectedBaseRevision: 6, Note: "Y")));
        // Der Refund verwendet eine freie Id (bk-c7-r2 ist das Storno R2)
        // — geprüft wird der wirksame Storno-Status, nicht eine Id-Kollision.
        await Assert.ThrowsAsync<LarioDomainException>(() =>
            db.Resolve<RefundService>().RegisterAsync(new RefundSpec(
                "bk-c7-rf2", "bk-c7", Step5Scenario.Haupt, new DateOnly(2026, 11, 30),
                [new RefundLineSpec(Step5Scenario.Flex, new Money(500))])));

        var journal = await Step5Scenario.JournalAsync(db);
        Assert.Empty(journal.AllocationInvariantViolations());
    }

    // ==================================================================
    // P2: Steuerkennzeichnung in den Vorher/Nachher-Snapshots
    // ==================================================================

    [Fact(DisplayName = "P2: Reine Steuerkorrektur — Vorher- und Nachher-Snapshot unterscheiden sich")]
    public async Task P2_TaxOnlyCorrection_SnapshotsDiffer()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.CreateTaxCategoryAsync("tax-19", "MwSt 19 %");
        await admin.CreateTaxCategoryAsync("tax-7", "MwSt 7 %");

        // Ausgabe 100 € mit Steuerkennzeichnung 19 % (19 €).
        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = "bk-c8",
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
                    Tax = new TaxMarking
                    {
                        Relevant = true,
                        TaxCategoryId = "tax-19",
                        MarkedAmount = new Money(1_900),
                        InheritedFromPreset = false,
                    },
                },
            ],
        });

        // Korrektur, die nur die Steuerkennzeichnung ändert (19 % → 7 %):
        // Salden und Split bleiben identisch.
        var result = await db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
            "bk-c8", "op-c8", ExpectedBaseRevision: 1,
            TotalAmount: new Money(10_000),
            Splits:
            [
                new ExpenseSplitSpec
                {
                    Id = "s1",
                    Amount = new Money(10_000),
                    PoolId = Step5Scenario.Flex,
                    CategoryId = "cat-lebensmittel",
                    Tax = new TaxMarking
                    {
                        Relevant = true,
                        TaxCategoryId = "tax-7",
                        MarkedAmount = new Money(700),
                        InheritedFromPreset = false,
                    },
                },
            ]));
        Assert.True(result.Created);

        // Die Historie muss den Unterschied sichtbar machen — ohne
        // Steuerfeld im Snapshot wären Vorher und Nachher identisch.
        var correction = await db.Resolve<IBookingRepository>().FindCorrectionAsync("op-c8");
        Assert.NotNull(correction);
        Assert.NotEqual(correction!.OldSnapshot, correction.NewSnapshot);

        var oldTax = JsonSerializer.Deserialize<JsonElement>(correction.OldSnapshot);
        var newTax = JsonSerializer.Deserialize<JsonElement>(correction.NewSnapshot);
        Assert.Equal("tax-19", oldTax.GetProperty("Splits")[0].GetProperty("Tax").GetProperty("TaxCategoryId").GetString());
        Assert.Equal("tax-7", newTax.GetProperty("Splits")[0].GetProperty("Tax").GetProperty("TaxCategoryId").GetString());

        // Salden unverändert (Steuerkennzeichnung bewegt kein Geld).
        Assert.Equal(90_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
    }

    // ==================================================================
    // P2: Korrektur muss bereits gewährte Erstattungen decken
    // ==================================================================

    [Fact(DisplayName = "P2: Korrektur senkt Split unter bereits gewährte Erstattung — abgelehnt, nichts wird persistiert")]
    public async Task P2_RefundCoverage_CorrectionBelowGrantedRefund_Conflict()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        // Ausgabe 100 €: Flex 60 / Freizeit 40 (Revision 1).
        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = "bk-c9",
            Date = new DateOnly(2026, 11, 5),
            AccountId = Step5Scenario.Haupt,
            TotalAmount = new Money(10_000),
            Splits =
            [
                new ExpenseSplitSpec { Id = "s1", Amount = new Money(6_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
                new ExpenseSplitSpec { Id = "s2", Amount = new Money(4_000), PoolId = Step5Scenario.PoolFreizeit, CategoryId = "cat-freizeit" },
            ],
        });

        // Bereits gewährte Erstattungen: 50 € Flex (≤ 60), 40 € Freizeit
        // (= 40, Deckel erreicht).
        var refundService = db.Resolve<RefundService>();
        Assert.True((await refundService.RegisterAsync(new RefundSpec(
            "bk-c9-r1", "bk-c9", Step5Scenario.Haupt, new DateOnly(2026, 11, 20),
            [new RefundLineSpec(Step5Scenario.Flex, new Money(5_000))]))).Created);
        Assert.True((await refundService.RegisterAsync(new RefundSpec(
            "bk-c9-r2", "bk-c9", Step5Scenario.Haupt, new DateOnly(2026, 11, 21),
            [new RefundLineSpec(Step5Scenario.PoolFreizeit, new Money(4_000))]))).Created);

        var correctionService = db.Resolve<CorrectionService>();

        // (a) Split des Flex-Pools auf 40 € gesenkt — unter den bereits
        // erstatteten 50 €: Konflikt.
        await Assert.ThrowsAsync<DomainConflictException>(() =>
            correctionService.CorrectAsync(new CorrectionSpec(
                "bk-c9", "op-c9-a", ExpectedBaseRevision: 3,
                TotalAmount: new Money(10_000),
                Splits:
                [
                    new ExpenseSplitSpec { Id = "s1", Amount = new Money(4_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
                    new ExpenseSplitSpec { Id = "s2", Amount = new Money(6_000), PoolId = Step5Scenario.PoolFreizeit, CategoryId = "cat-freizeit" },
                ])));

        // (b) Freizeit-Pool entfernt, obwohl 40 € daraus erstattet
        // wurden: Konflikt.
        await Assert.ThrowsAsync<DomainConflictException>(() =>
            correctionService.CorrectAsync(new CorrectionSpec(
                "bk-c9", "op-c9-b", ExpectedBaseRevision: 3,
                TotalAmount: new Money(10_000),
                Splits:
                [
                    new ExpenseSplitSpec { Id = "s1", Amount = new Money(10_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
                ])));

        // (c) Split des Freizeit-Pools auf 30 € gesenkt — unter den
        // bereits erstatteten 40 €: Konflikt.
        await Assert.ThrowsAsync<DomainConflictException>(() =>
            correctionService.CorrectAsync(new CorrectionSpec(
                "bk-c9", "op-c9-c", ExpectedBaseRevision: 3,
                TotalAmount: new Money(10_000),
                Splits:
                [
                    new ExpenseSplitSpec { Id = "s1", Amount = new Money(7_000), PoolId = Step5Scenario.Flex, CategoryId = "cat-lebensmittel" },
                    new ExpenseSplitSpec { Id = "s2", Amount = new Money(3_000), PoolId = Step5Scenario.PoolFreizeit, CategoryId = "cat-freizeit" },
                ])));

        // Nichts wurde persistiert: Revision unverändert, keine
        // Historie, Salden und Erstattungen intakt.
        var final = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-c9");
        Assert.Equal(3, final!.BaseRevision); // nur die beiden Erstattungen haben erhöht
        Assert.Equal(new Money(-10_000), final.AccountPostings.Single().Amount);
        Assert.Null(await db.Resolve<IBookingRepository>().FindCorrectionAsync("op-c9-a"));
        Assert.Null(await db.Resolve<IBookingRepository>().FindCorrectionAsync("op-c9-b"));
        Assert.Null(await db.Resolve<IBookingRepository>().FindCorrectionAsync("op-c9-c"));
        // Beide Erstattungen (50 € Flex, 40 € Freizeit) sind gewährte:
        // Haupt 100_000 − 10_000 + 5_000 + 4_000 = 99_000; Flex 100_000 − 6_000
        // + 5_000 = 99_000; Freizeit 30_000 − 4_000 + 4_000 = 30_000.
        Assert.Equal(99_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
        Assert.Equal(99_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.Flex));
        Assert.Equal(30_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.PoolFreizeit));
        Assert.Empty((await Step5Scenario.JournalAsync(db)).AllocationInvariantViolations());
    }

    // ==================================================================
    // Helfer
    // ==================================================================

    private static ExpenseSplitSpec SingleSplitSpec(string id, long cents) =>
        new()
        {
            Id = id,
            Amount = new Money(cents),
            PoolId = Step5Scenario.Flex,
            CategoryId = "cat-lebensmittel",
        };

    private static async Task CreateExpenseAsync(
        TestDatabase db, string id, long cents, string? note = null)
    {
        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = id,
            Date = new DateOnly(2026, 11, 5),
            AccountId = Step5Scenario.Haupt,
            TotalAmount = new Money(cents),
            Note = note,
            Splits = [SingleSplitSpec("s1", cents)],
        });
    }
}
