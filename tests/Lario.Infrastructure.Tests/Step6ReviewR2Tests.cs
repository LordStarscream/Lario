using Lario.Application;
using Lario.Application.Journal;
using Lario.Domain;
using Lario.Domain.Journal;
using Microsoft.Extensions.DependencyInjection;

namespace Lario.Infrastructure.Tests;

/// <summary>
/// Regressionstests zu den Befunden des externen Reviews (Runde 2) für
/// Schritt 6 — auf echter SQLite:
/// <para>
/// <b>B1 (Storno-Kette bei Widerruf):</b> ein Storno eines Stornos
/// (Widerruf) reaktiviert den Ast darüber. Davon darf kein zweites
/// wirksames Storno derselben Buchung entstehen (Saldo-Verschiebung) und
/// kein veralteter Ast reaktiviert werden, der eine In-place-Korrektur
/// des Wurzeloriginals nicht abbildet (Geld aus dem Nichts). Die
/// Storno-Kette bleibt linear (keine Gabeln).
/// </para>
/// <para>
/// <b>B2 (Ketten-Revision):</b> ein Storno erhöht die Basisrevision der
/// gesamten Storno-Kette über seinem Original (Original, dessen Original,
/// … bis zum Wurzeloriginal) — jedes Kettenglied mit eigenem
/// Concurrency-Token. Ein Kontext, der ein Kettenglied (und damit dessen
/// wirksamen Status) vor dem Commit eines parallelen Stornos in der
/// Kette gelesen hat, erhält beim eigenen Commit einen Revisionskonflikt
/// und wird vollständig zurückgerollt.
/// </para>
/// </summary>
public class Step6ReviewR2Tests
{
    // ==================================================================
    // B1: Storno-Kette — Frische, Parität, Linearität
    // ==================================================================

    [Fact(DisplayName = "B1: Veralteter Ast darf nicht reaktiviert werden (Korrektur nach Widerruf) — neues Storno des Wurzeloriginals ist der Weg")]
    public async Task B1_StaleBranch_CannotBeReactivated()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        // Ausgabe 100 € (Revision 1).
        await CreateExpenseAsync(db, "bk-r2a", 10_000);

        // R: Storno der Ausgabe (E 1 → 2).
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-r2a-r", "bk-r2a", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 25)))).Created);

        // W: Widerruf von R (R 1 → 2, E 2 → 3 — Kette, Review R2 B2).
        // Die Ausgabe wirkt wieder.
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-r2a-w", "bk-r2a-r", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 26)))).Created);

        // E wird in-place korrigiert: 100 € → 80 € (E 3 → 4). Das Storno R
        // bildet damit den alten Inhalt ab (100 €) und ist veraltet.
        Assert.True((await db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
            "bk-r2a", "op-r2a", ExpectedBaseRevision: 3,
            TotalAmount: new Money(8_000),
            Splits: [SingleSplitSpec("s1", 8_000)]))).Created);

        // X: Widerruf von W (aktuelle Revision 1) würde R reaktivieren.
        // Ohne den Frische-Check entstünde Geld aus dem Nichts:
        // E(−80) + R(+100) + W(−100) + X(+100) = +20 €. Abgelehnt, weil
        // R im künftigen Zustand wirksam würde, aber nicht mehr den
        // aktuellen Inhalt von E (80 €) abbildet.
        var xConflict = await Assert.ThrowsAsync<DomainConflictException>(() =>
            db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
                "bk-r2a-x", "bk-r2a-w", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 27))));
        Assert.Contains("in-place", xConflict.Message);

        // Nichts wurde persistiert: Salden auf dem korrigierten Stand
        // (100_000 − 8_000 = 92_000), keine X-Buchung, Revisionen intakt.
        Assert.Equal(92_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
        Assert.Equal(92_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.Flex));
        Assert.Null(await db.Resolve<IBookingRepository>().FindByIdAsync("bk-r2a-x"));
        var eAfter = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-r2a");
        Assert.Equal(4, eAfter!.BaseRevision);

        // Der vorgeschlagene Weg: ein neues Storno des Wurzeloriginals —
        // es übernimmt den aktuellen Inhalt (80 €) (E 4 → 5).
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-r2a-r2", "bk-r2a", ExpectedBaseRevision: 4, new DateOnly(2026, 11, 28)))).Created);

        // Jetzt wirkt das neue Storno: Salden zurück auf Start, E ist
        // wirksam storniert (R ist weiterhin nur historisch).
        Assert.Equal(100_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
        Assert.Equal(100_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.Flex));

        var journal = await Step5Scenario.JournalAsync(db);
        // E ist wirksam storniert — und zwar nur durch das neue Storno
        // R2: das alte Storno R ist selbst storniert (Widerruf W) und
        // zählt daher nicht.
        Assert.True(journal.IsEffectivelyReversed("bk-r2a"));
        Assert.True(journal.IsEffectivelyReversed("bk-r2a-r"));
        var effectiveReversals = journal.ReversalsOf("bk-r2a")
            .Where(r => !journal.IsEffectivelyReversed(r.Id))
            .ToList();
        Assert.Single(effectiveReversals);
        Assert.Equal("bk-r2a-r2", effectiveReversals[0].Id);
        Assert.Empty(journal.AllocationInvariantViolations());
    }

    [Fact(DisplayName = "B1: Zweites wirksames Storno derselben Buchung wird abgelehnt (Widerruf neben neuem Storno)")]
    public async Task B1_SecondEffectiveReversal_Rejected()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        // Ausgabe 100 € (Revision 1).
        await CreateExpenseAsync(db, "bk-r2b", 10_000);

        // R: Storno (E 1 → 2), W: Widerruf (R 1 → 2, E 2 → 3) — die
        // Ausgabe wirkt wieder.
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-r2b-r", "bk-r2b", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 25)))).Created);
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-r2b-w", "bk-r2b-r", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 26)))).Created);

        // R2: neues Storno der wirksamen Ausgabe (E 3 → 4) — zulässig.
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-r2b-r2", "bk-r2b", ExpectedBaseRevision: 3, new DateOnly(2026, 11, 27)))).Created);

        // X: Widerruf von W (aktuelle Revision 1) würde R zusätzlich zu
        // R2 wirksam — zwei wirksame Stornos derselben Buchung. Im Status
        // pariert sich das zu „nicht storniert“, im Saldo verschob es
        // +100 €. Abgelehnt (Höchstens-ein-wirksames-Storno-Check).
        var xConflict = await Assert.ThrowsAsync<DomainConflictException>(() =>
            db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
                "bk-r2b-x", "bk-r2b-w", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 28))));
        Assert.Contains("wirksame Stornos", xConflict.Message);

        // Nichts wurde persistiert: E wirkt storniert (nur R2), Salden auf
        // Start, Revisionen intakt.
        Assert.Equal(100_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
        Assert.Equal(100_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.Flex));
        Assert.Null(await db.Resolve<IBookingRepository>().FindByIdAsync("bk-r2b-x"));

        var journal = await Step5Scenario.JournalAsync(db);
        Assert.True(journal.IsEffectivelyReversed("bk-r2b"));
        var effectiveReversals = journal.ReversalsOf("bk-r2b")
            .Where(r => !journal.IsEffectivelyReversed(r.Id))
            .ToList();
        Assert.Single(effectiveReversals);
        Assert.Equal("bk-r2b-r2", effectiveReversals[0].Id);
        Assert.Empty(journal.AllocationInvariantViolations());
    }

    [Fact(DisplayName = "B1: Storno-Kette ist linear — zweites Storno desselben Stornos ist ein Konflikt (kein Gabeln)")]
    public async Task B1_ReversalChain_IsLinear()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        // Ausgabe 100 € (Revision 1).
        await CreateExpenseAsync(db, "bk-r2c", 10_000);

        // R: Storno (E 1 → 2).
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-r2c-r", "bk-r2c", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 25)))).Created);

        // W: Widerruf von R (R 1 → 2, E 2 → 3).
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-r2c-w", "bk-r2c-r", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 26)))).Created);

        // X1: Widerruf von W (Kette W, R, E: W 1 → 2, R 2 → 3, E 3 → 4)
        // — R wirkt wieder, E ist storniert.
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-r2c-x1", "bk-r2c-w", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 27)))).Created);

        // Y1: Widerruf von X1 (Kette X1, W, R, E: X1 1 → 2, W 2 → 3,
        // R 3 → 4, E 4 → 5) — W wirkt wieder, R ist storniert, E wirkt.
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-r2c-y1", "bk-r2c-x1", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 28)))).Created);

        // Z: zweites Storno von W (aktuelle Revision 3) — W trägt bereits
        // das (historische) Storno X1. Ein zweites würde die Kette gabeln
        // und den wirksamen Status mehrdeutig machen. Abgelehnt.
        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() =>
            db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
                "bk-r2c-z", "bk-r2c-w", ExpectedBaseRevision: 3, new DateOnly(2026, 11, 29))));
        Assert.Contains("line", conflict.Message);

        // Nichts wurde persistiert: E wirkt (−100 €), genau die fünf
        // Buchungen existieren.
        Assert.Equal(90_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
        Assert.Equal(90_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.Flex));
        Assert.Null(await db.Resolve<IBookingRepository>().FindByIdAsync("bk-r2c-z"));

        var journal = await Step5Scenario.JournalAsync(db);
        // E wirkt (R ist storniert), W wirkt (X1 ist storniert), R ist
        // storniert (W wirkt).
        Assert.False(journal.IsEffectivelyReversed("bk-r2c"));
        Assert.True(journal.IsEffectivelyReversed("bk-r2c-r"));
        Assert.False(journal.IsEffectivelyReversed("bk-r2c-w"));
        Assert.Empty(journal.AllocationInvariantViolations());
    }

    // ==================================================================
    // B2: Ketten-Revision — paralleler Storno in der Kette
    // ==================================================================

    [Fact(DisplayName = "B2: Paralleler Widerruf in der Storno-Kette (zwei Kontexte, Barriere nach dem Lesen) — veralteter wirksamer Status wird abgelehnt")]
    public async Task B2_ParallelReversalInChain_StaleEffectiveStatusRejected()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        // Ausgabe 100 € (Revision 1).
        await CreateExpenseAsync(db, "bk-r2d", 10_000);

        // R: Storno (E 1 → 2), W: Widerruf (R 1 → 2, E 2 → 3).
        // Die Ausgabe wirkt jetzt; E trägt Revision 3.
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-r2d-r", "bk-r2d", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 25)))).Created);
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-r2d-w", "bk-r2d-r", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 26)))).Created);

        // Kontext A: liest das Wurzeloriginal (Revision 3) und das Journal
        // — aus seiner Sicht ist E wirksam. Danach kommt die Barriere.
        var scopeA = db.NewServiceProvider().CreateScope();
        var repoA = scopeA.ServiceProvider.GetRequiredService<IBookingRepository>();
        var staleA = await repoA.FindByIdAsync("bk-r2d");
        Assert.NotNull(staleA);
        Assert.Equal(3, staleA.BaseRevision);

        // Barriere: Kontext B widerruft W (Kette W, R, E: W 1 → 2,
        // R 2 → 3, E 3 → 4). Damit wirkt R wieder — E ist storniert.
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-r2d-w2", "bk-r2d-w", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 27)))).Created);

        // Kontext A committet jetzt sein Storno der „wirksamen“ Ausgabe auf
        // Basis des veralteten Lesezustands. Ohne die Ketten-Revision
        // würde E hier seine Revision behalten und das Storno committen —
        // zwei wirksame Stornos (R und das neue) bei Status „nicht
        // storniert“. Jetzt: Revisionskonflikt, vollständiger
        // Rollback.
        var loser = new ReversalSpec(
            "bk-r2d-st-loser", "bk-r2d", ExpectedBaseRevision: 3, new DateOnly(2026, 11, 28));
        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() =>
            repoA.AddWithOriginalRevisionAsync(loser.ToBooking(staleA!), [staleA], []));

        Assert.Equal(3, conflict.ExpectedBaseRevision);
        Assert.Equal(4, conflict.ActualBaseRevision);

        // Endzustand: kein zweites Storno, exakt ein wirksames Storno der
        // Ausgabe (R), Salden konsistent (netto 0 — E storniert).
        Assert.Equal(100_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
        Assert.Equal(100_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.Flex));
        Assert.Null(await db.Resolve<IBookingRepository>().FindByIdAsync("bk-r2d-st-loser"));

        var journal = await Step5Scenario.JournalAsync(db);
        var effectiveReversals = journal.ReversalsOf("bk-r2d")
            .Where(r => !journal.IsEffectivelyReversed(r.Id))
            .ToList();
        Assert.Single(effectiveReversals);
        Assert.Equal("bk-r2d-r", effectiveReversals[0].Id);
        Assert.True(journal.IsEffectivelyReversed("bk-r2d"));
        Assert.Empty(journal.AllocationInvariantViolations());

        // Die Ketten-Revisionen belegen, dass jedes Kettenglied den
        // parallelen Storno „gesehen“ hat:
        var e = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-r2d");
        var r = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-r2d-r");
        var w = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-r2d-w");
        Assert.Equal(4, e!.BaseRevision); // 1 (Anlage) + R + W + W2
        Assert.Equal(3, r!.BaseRevision); // 1 (Anlage) + W + W2
        Assert.Equal(2, w!.BaseRevision); // 1 (Anlage) + W2
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
