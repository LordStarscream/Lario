using Lario.Application;
using Lario.Application.Journal;
using Lario.Domain;
using Lario.Domain.Journal;
using Microsoft.Extensions.DependencyInjection;

namespace Lario.Infrastructure.Tests;

/// <summary>
/// Regressionstests zu den Befunden des externen Reviews (Runde 3) für
/// Schritt 6 — auf echter SQLite:
/// <para>
/// <b>P1 (Snapshot-Revision als Concurrency-Basis):</b> der Repository
/// darf die erwarteten Revisionen der Original-Kette nicht durch neu
/// gelesene aktuelle Werte ersetzen. Zwischen der Prüfung im
/// Anwendungsfall (Journal-Snapshot) und dem atomaren Schreiben kann
/// eine parallele Operation eine Kettenglieder-Revision verschieben:
/// eine Korrektur des Wurzeloriginals (anderer Betrag) macht den
/// Inhalt des neuen Stornos finanziell inkonsistent; ein neues direktes
/// Storno der Wurzel verletzt die Invariante „höchstens ein wirksames
/// Storno". In beiden Fällen muss das Schreiben als Revisionskonflikt
/// enden und vollständig zurückgerollt werden — nicht committen, weil
/// die aktuellen Revisionen „zufällig" alle CAS-Prüfungen bestehen
/// würden.
/// </para>
/// </summary>
public class Step6ReviewR3Tests
{
    // ==================================================================
    // P1: Korrektur des Wurzeloriginals zwischen Prüfung und Schreiben
    // ==================================================================

    [Fact(DisplayName = "P1: Korrektur der Wurzel nach dem Journal-Snapshot — Storno auf veraltetem Stand wird abgelehnt, nichts persistiert")]
    public async Task P1_RootCorrectionBetweenCheckAndWrite_StaleReversalRejected()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        // Ausgabe 100 € (Revision 1).
        await CreateExpenseAsync(db, "bk-r3a", 10_000);

        // R: Storno der Ausgabe (E 1 → 2), W: Widerruf von R
        // (R 1 → 2, E 2 → 3). Die Ausgabe wirkt jetzt (100 €).
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-r3a-r", "bk-r3a", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 25)))).Created);
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-r3a-w", "bk-r3a-r", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 26)))).Created);

        // Kontext A: liest das Kettenkopf-Original W (Revision 1,
        // getrackt) und das Journal-Snapshot (W=1, R=2, E=3). Aus seiner
        // Sicht ist die Ausgabe wirksam und der Widerruf W widerrufbar —
        // alle Service-Prüfungen bestünden auf diesem Stand.
        var scopeA = db.NewServiceProvider().CreateScope();
        var repoA = scopeA.ServiceProvider.GetRequiredService<IBookingRepository>();
        var staleW = await repoA.FindByIdAsync("bk-r3a-w");
        Assert.NotNull(staleW);
        Assert.Equal(1, staleW!.BaseRevision);
        var journal = await repoA.GetAllAsync();
        var eSnapshot = journal.Single(b => b.Id == "bk-r3a");
        Assert.Equal(3, eSnapshot.BaseRevision);

        // Barriere: Kontext B korrigiert die Wurzel in-place —
        // 100 € → 80 € (E 3 → 4). Der Inhalt des Stornos R (und damit
        // des geplanten Widerrufs X) bildet jetzt den alten Betrag ab.
        Assert.True((await db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
            "bk-r3a", "op-r3a", ExpectedBaseRevision: 3,
            TotalAmount: new Money(8_000),
            Splits: [SingleSplitSpec("s1", 8_000)]))).Created);

        // Kontext A committet jetzt seinen Widerruf X auf Basis des
        // Snapshot-Stands. Die Kette (wie der Service sie aus dem
        // geprüften Snapshot baut): W (getrackt, 1), R und E aus dem
        // Journal-Snapshot.
        var x = new ReversalSpec(
            "bk-r3a-x", "bk-r3a-w", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 27));
        var chain = BuildChainFromSnapshot(staleW, journal);

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() =>
            repoA.AddWithOriginalRevisionAsync(x.ToBooking(staleW), chain, []));

        // Der Konflikt liegt an der Wurzel: Snapshot-Revision 3,
        // Ist-Revision 4 (Korrektur).
        Assert.Equal(3, conflict.ExpectedBaseRevision);
        Assert.Equal(4, conflict.ActualBaseRevision);
        Assert.Contains("bk-r3a", conflict.Message);

        // Vollständiger Rollback: keine X-Buchung, Revisionen von W und
        // R intakt, E auf dem korrigierten Stand (4).
        Assert.Null(await db.Resolve<IBookingRepository>().FindByIdAsync("bk-r3a-x"));
        var w = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-r3a-w");
        var r = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-r3a-r");
        var e = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-r3a");
        Assert.Equal(1, w!.BaseRevision);
        Assert.Equal(2, r!.BaseRevision);
        Assert.Equal(4, e!.BaseRevision);

        // Salden auf dem korrigierten Stand: 100_000 − 8_000 = 92_000 —
        // kein Geld aus dem Nichts (X hätte R mit 100 € reaktiviert).
        Assert.Equal(92_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
        Assert.Equal(92_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.Flex));
        Assert.Empty((await Step5Scenario.JournalAsync(db)).AllocationInvariantViolations());
    }

    // ==================================================================
    // P1: Neues direktes Storno der Wurzel zwischen Prüfung und Schreiben
    // ==================================================================

    [Fact(DisplayName = "P1: Neues direktes Storno der Wurzel nach dem Journal-Snapshot — zweites wirksames Storno wird abgelehnt")]
    public async Task P1_DirectReversalOfRootBetweenCheckAndWrite_DoubleReversalRejected()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await CreateExpenseAsync(db, "bk-r3b", 10_000);

        // R: Storno (E 1 → 2), W: Widerruf (R 1 → 2, E 2 → 3).
        // Die Ausgabe wirkt jetzt.
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-r3b-r", "bk-r3b", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 25)))).Created);
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-r3b-w", "bk-r3b-r", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 26)))).Created);

        // Kontext A: liest W (Revision 1, getrackt) und das
        // Journal-Snapshot (W=1, R=2, E=3).
        var scopeA = db.NewServiceProvider().CreateScope();
        var repoA = scopeA.ServiceProvider.GetRequiredService<IBookingRepository>();
        var staleW = await repoA.FindByIdAsync("bk-r3b-w");
        Assert.NotNull(staleW);
        var journal = await repoA.GetAllAsync();

        // Barriere: Kontext B legt ein direktes Storno der Wurzel an
        // (E 3 → 4). Damit ist die Ausgabe jetzt wirksam storniert —
        // aus A's Snapshot-Sicht war sie wirksam.
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-r3b-r2", "bk-r3b", ExpectedBaseRevision: 3, new DateOnly(2026, 11, 27)))).Created);

        // Kontext A committet jetzt seinen Widerruf X (würde R
        // reaktivieren → zwei wirksame Stornos: R und R2).
        var x = new ReversalSpec(
            "bk-r3b-x", "bk-r3b-w", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 28));
        var chain = BuildChainFromSnapshot(staleW, journal);

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() =>
            repoA.AddWithOriginalRevisionAsync(x.ToBooking(staleW), chain, []));

        Assert.Equal(3, conflict.ExpectedBaseRevision);
        Assert.Equal(4, conflict.ActualBaseRevision);
        Assert.Contains("bk-r3b", conflict.Message);

        // Vollständiger Rollback: keine X-Buchung, exakt ein wirksames
        // Storno der Ausgabe (R2), Salden konsistent (netto 0 — E
        // storniert).
        Assert.Null(await db.Resolve<IBookingRepository>().FindByIdAsync("bk-r3b-x"));
        var journalAfter = await Step5Scenario.JournalAsync(db);
        var effectiveReversals = journalAfter.ReversalsOf("bk-r3b")
            .Where(r => !journalAfter.IsEffectivelyReversed(r.Id))
            .ToList();
        Assert.Single(effectiveReversals);
        Assert.Equal("bk-r3b-r2", effectiveReversals[0].Id);
        Assert.True(journalAfter.IsEffectivelyReversed("bk-r3b"));
        Assert.Empty(journalAfter.AllocationInvariantViolations());
        Assert.Equal(100_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
        Assert.Equal(100_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.Flex));

        var w = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-r3b-w");
        Assert.Equal(1, w!.BaseRevision); // Rollback: W unverändert
    }

    // ==================================================================
    // P1: Positive Kontrolle — ohne parallele Mutation committet das
    // Storno mit der Snapshot-Kette
    // ==================================================================

    [Fact(DisplayName = "P1: Ohne parallele Mutation committet das Storno mit der Snapshot-Kette (W, R, E jeweils +1)")]
    public async Task P1_NoConcurrentCommit_SnapshotChainCommits()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await CreateExpenseAsync(db, "bk-r3c", 10_000);

        // R: Storno (E 1 → 2), W: Widerruf (R 1 → 2, E 2 → 3).
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-r3c-r", "bk-r3c", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 25)))).Created);
        Assert.True((await db.Resolve<ReversalService>().ReverseAsync(new ReversalSpec(
            "bk-r3c-w", "bk-r3c-r", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 26)))).Created);

        // Kontext A: liest W (getrackt) und das Journal-Snapshot — und
        // committet unmittelbar (keine parallele Operation dazwischen).
        var scopeA = db.NewServiceProvider().CreateScope();
        var repoA = scopeA.ServiceProvider.GetRequiredService<IBookingRepository>();
        var staleW = await repoA.FindByIdAsync("bk-r3c-w");
        Assert.NotNull(staleW);
        var journal = await repoA.GetAllAsync();

        var x = new ReversalSpec(
            "bk-r3c-x", "bk-r3c-w", ExpectedBaseRevision: 1, new DateOnly(2026, 11, 27));
        var stored = await repoA.AddWithOriginalRevisionAsync(
            x.ToBooking(staleW), BuildChainFromSnapshot(staleW, journal), []);

        // X ist angelegt; die gesamte Kette wurde um 1 erhöht.
        Assert.Equal(BookingType.Reversal, stored.Type);
        var w = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-r3c-w");
        var r = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-r3c-r");
        var e = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-r3c");
        Assert.Equal(2, w!.BaseRevision);
        Assert.Equal(3, r!.BaseRevision);
        Assert.Equal(4, e!.BaseRevision);

        // R wirkt jetzt wieder (Widerruf widerrufen): die Ausgabe ist
        // wirksam storniert, Salden auf Startniveau.
        var journalAfter = await Step5Scenario.JournalAsync(db);
        Assert.True(journalAfter.IsEffectivelyReversed("bk-r3c"));
        Assert.True(journalAfter.IsEffectivelyReversed("bk-r3c-w"));
        Assert.False(journalAfter.IsEffectivelyReversed("bk-r3c-r"));
        Assert.Empty(journalAfter.AllocationInvariantViolations());
        Assert.Equal(100_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
        Assert.Equal(100_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.Flex));
    }

    // ==================================================================
    // Helfer
    // ==================================================================

    /// <summary>
    /// Baut die Original-Kette exakt wie <c>ReversalService</c> aus dem
    /// geprüften Snapshot: zuerst das getrackte direkte Original, dann
    /// die Vorfahren aus dem (AsNoTracking) Journal — jedes Kettenglied
    /// mit seiner Snapshot-Revision.
    /// </summary>
    private static List<Booking> BuildChainFromSnapshot(Booking directOriginal, IReadOnlyList<Booking> journal)
    {
        var byId = journal.ToDictionary(b => b.Id);
        var chain = new List<Booking> { directOriginal };
        var current = directOriginal;
        while (current.Type == BookingType.Reversal && current.ReversesBookingId is { } parentId)
        {
            chain.Add(byId[parentId]);
            current = byId[parentId];
        }

        return chain;
    }

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
