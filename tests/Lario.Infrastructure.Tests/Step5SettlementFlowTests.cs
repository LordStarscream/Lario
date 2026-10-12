using Lario.Application;
using Lario.Application.Journal;
using Lario.Domain;
using Lario.Domain.Journal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lario.Infrastructure.Tests;

/// <summary>
/// Offene interne Ausgleiche auf echter SQLite (Schritt 5):
/// Referenzfall 4 (Vollbegleichung), Teilbegleichung 5 € → 15 € offen,
/// Begleichung ohne Referenz (Journal bleibt Wahrheit) und Neustart mit
/// offener Position.
/// </summary>
public class SettlementFlowPersistenceTests
{
    [Fact(DisplayName = "Referenzfall 4: 20-€-Rückfluss begleicht die Position vollständig")]
    public async Task ReferenceCase4_FullSettlement()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await Step5Scenario.AddMixedPurchaseAsync(db);

        // Vor dem Rückfluss: 20 € offen (bk-2026-1202 begründet).
        var open = Assert.Single(await db.Resolve<SettlementQueryService>().GetOpenAsync());
        Assert.Equal(Step5Scenario.Haupt, open.SourceAccountId);
        Assert.Equal(Step5Scenario.PoolFreizeit, open.TargetPoolId);
        Assert.Equal("bk-2026-1202", open.CreatedByBookingId);
        Assert.Equal(new Money(2_000), open.RestAmount);

        var result = await db.Resolve<TransferService>().CreateAsync(new TransferSpec
        {
            Id = "bk-2026-1203",
            Date = new DateOnly(2026, 11, 6),
            FromAccountId = Step5Scenario.Freizeit,
            ToAccountId = Step5Scenario.Haupt,
            Amount = new Money(2_000),
            Purposes = [new PoolAllocation(Step5Scenario.PoolFreizeit, new Money(2_000))],
            RelatedSettlementId = open.Id,
            Note = "Ausgleich 20 € (Rückfluss der Vorfinanzierung)",
        });
        Assert.True(result.Created);

        // Konten: 935 € / 280 € / 0 € (Start 1.000/300, −85 Einkauf,
        // +20 Rückfluss). Pools: Flex 935 € (1.000 − 65 Einkauf) und
        // Freizeit 280 € (300 − 20 Einkauf); der Transfer selbst ändert
        // die Pools nicht („dieselben Pools“, Plan-Prüfung 2; Referenz-
        // fall 04: pool-flex 93500, pool-freizeit 28000).
        Assert.Equal(93_500, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
        Assert.Equal(28_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Freizeit));
        Assert.Equal(0, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Wohnung));
        Assert.Equal(93_500, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.Flex));
        Assert.Equal(28_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.PoolFreizeit));

        // Die Position ist beglichen und verknüpft mit der begleichenden Buchung.
        Assert.Empty(await db.Resolve<SettlementQueryService>().GetOpenAsync());
        var settled = Assert.Single(await db.Resolve<SettlementQueryService>().GetAllAsync());
        Assert.Equal(SettlementStatus.Beglichen, settled.Status);
        Assert.Equal("bk-2026-1203", settled.SettledByBookingId);
        Assert.Equal(new Money(2_000), settled.Amount);
        Assert.Equal(Money.Zero, settled.RestAmount);
    }

    [Fact(DisplayName = "Teilbegleichung 5 €: Rest 15 € bleibt offen, Journal bleibt konsistent")]
    public async Task PartialSettlement_KeepsRestOpen()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await Step5Scenario.AddMixedPurchaseAsync(db);
        var open = Assert.Single(await db.Resolve<SettlementQueryService>().GetOpenAsync());

        await db.Resolve<TransferService>().CreateAsync(new TransferSpec
        {
            Id = "tr-teil",
            Date = new DateOnly(2026, 11, 7),
            FromAccountId = Step5Scenario.Freizeit,
            ToAccountId = Step5Scenario.Haupt,
            Amount = new Money(500),
            Purposes = [new PoolAllocation(Step5Scenario.PoolFreizeit, new Money(500))],
            RelatedSettlementId = open.Id,
        });

        // 15 € offen, Rest wird nicht doppelt abgezogen.
        var rest = Assert.Single(await db.Resolve<SettlementQueryService>().GetOpenAsync());
        Assert.Equal(open.Id, rest.Id);
        Assert.Equal(new Money(1_500), rest.RestAmount);

        // Konten: 920 € (915 € nach Einkauf + 5 € Teilbegleichung) /
        // 295 €; Pools 935 € / 280 € (durch den Einkauf gesenkt, der
        // Transfer ändert sie nicht) — der Rest bleibt offen und wird
        // nicht doppelt abgezogen.
        var balances = await db.Resolve<BalanceQueryService>().GetBalancesAsync();
        Assert.Equal(92_000, balances.Accounts.Single(a => a.AccountId == Step5Scenario.Haupt).Balance.Cents);
        Assert.Equal(29_500, balances.Accounts.Single(a => a.AccountId == Step5Scenario.Freizeit).Balance.Cents);
        Assert.Equal(93_500, balances.Pools.Single(p => p.PoolId == Step5Scenario.Flex).Balance.Cents);
        Assert.Equal(28_000, balances.Pools.Single(p => p.PoolId == Step5Scenario.PoolFreizeit).Balance.Cents);

        // Das Journal bleibt in sich konsistent (Zeilensummen = Kontostände,
        // Spaltensummen = Poolbestände).
        var journal = await Step5Scenario.JournalAsync(db);
        Assert.Empty(journal.AllocationInvariantViolations());
    }

    [Fact(DisplayName = "Transfer ohne Referenz begleicht trotzdem (Journal ist die Wahrheit)")]
    public async Task UnlinkedTransferStillSettles()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await Step5Scenario.AddMixedPurchaseAsync(db);

        // Der Rückfluss wird ohne Begleichungsreferenz erfasst.
        await db.Resolve<TransferService>().CreateAsync(new TransferSpec
        {
            Id = "tr-ohne-ref",
            Date = new DateOnly(2026, 11, 7),
            FromAccountId = Step5Scenario.Freizeit,
            ToAccountId = Step5Scenario.Haupt,
            Amount = new Money(2_000),
            Purposes = [new PoolAllocation(Step5Scenario.PoolFreizeit, new Money(2_000))],
        });

        // Der Ausgleich wird rechnerisch abgeschlossen, auch ohne
        // explizite Referenz: die Journalableitung ist die Wahrheit — die
        // Zeile trägt die Buchung, die die Zelle geschlossen hat.
        Assert.Empty(await db.Resolve<SettlementQueryService>().GetOpenAsync());
        var row = Assert.Single(await db.Resolve<ISettlementPositionRepository>().GetAllAsync());
        Assert.Equal(SettlementStatus.Beglichen, row.Status);
        Assert.Equal("tr-ohne-ref", row.SettledByBookingId);
        Assert.Equal(new DateOnly(2026, 11, 7), row.SettledDate);
        var loaded = await db.Resolve<IBookingRepository>().FindByIdAsync("tr-ohne-ref");
        Assert.Null(loaded!.RelatedSettlementId);
    }

    [Fact(DisplayName = "Neustart mit offener Position: Zustand bleibt erhalten und ist begleichbar")]
    public async Task Restart_OpenPositionSurvivesAndCanBeSettled()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await Step5Scenario.AddMixedPurchaseAsync(db);
        var position = Assert.Single(
            await db.Resolve<SettlementQueryService>().GetOpenAsync());

        // Neustart simulieren: neuer Provider auf derselben Datei (wie
        // beim Host-Start) — die offene Position bleibt erhalten.
        using var services = db.NewServiceProvider();
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;
        var query = provider.GetRequiredService<SettlementQueryService>();

        var restored = Assert.Single(await query.GetOpenAsync());
        Assert.Equal(position.Id, restored.Id);
        Assert.Equal(new Money(2_000), restored.RestAmount);

        // Begleichung über die zweite Instanz schließt die Position.
        await provider.GetRequiredService<TransferService>().CreateAsync(new TransferSpec
        {
            Id = "tr-neustart",
            Date = new DateOnly(2026, 11, 8),
            FromAccountId = Step5Scenario.Freizeit,
            ToAccountId = Step5Scenario.Haupt,
            Amount = new Money(2_000),
            Purposes = [new PoolAllocation(Step5Scenario.PoolFreizeit, new Money(2_000))],
            RelatedSettlementId = restored.Id,
        });

        Assert.Empty(await query.GetOpenAsync());
        var row = Assert.Single(
            await provider.GetRequiredService<ISettlementPositionRepository>().GetAllAsync());
        Assert.Equal(SettlementStatus.Beglichen, row.Status);
        Assert.Equal("tr-neustart", row.SettledByBookingId);
    }
}

/// <summary>
/// Umwidmung auf echter SQLite (Schritt 5): Referenzfall 6 (500 €
/// Flex → Rücklage), implizite Kontouflösung, Kontostände bleiben
/// unverändert, idempotente Wiederholung, Konflikte, Schuldbildung,
/// FK-Atomarität.
/// </summary>
public class ReassignmentPersistenceTests
{
    private static ReassignmentSpec Reassignment(
        string id = "bk-2026-1205",
        string fromPool = Step5Scenario.Flex,
        string toPool = Step5Scenario.Ruecklage,
        long cents = 50_000,
        IReadOnlyList<AccountAllocation>? accounts = null,
        DateOnly? date = null) =>
        new()
        {
            Id = id,
            Date = date ?? new DateOnly(2026, 11, 8),
            FromPoolId = fromPool,
            ToPoolId = toPool,
            Amount = new Money(cents),
            // null = implizite Kontoverteilung (Auflösung aus dem Journal).
            Accounts = accounts,
            Note = null,
        };

    [Fact(DisplayName = "Referenzfall 6: 500 € Flex → Rücklage, Kontostände bleiben unverändert")]
    public async Task ReferenceCase6_ReassignmentKeepsAccountBalances()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        var result = await db.Resolve<ReassignmentService>().CreateAsync(
            Reassignment(accounts:
            [
                new AccountAllocation(Step5Scenario.Haupt, new Money(50_000)),
            ]));
        Assert.True(result.Created);

        var balances = await db.Resolve<BalanceQueryService>().GetBalancesAsync();
        // Keine Kontobewegung.
        Assert.Equal(100_000, balances.Accounts.Single(a => a.AccountId == Step5Scenario.Haupt).Balance.Cents);
        Assert.Equal(30_000, balances.Accounts.Single(a => a.AccountId == Step5Scenario.Freizeit).Balance.Cents);
        Assert.Equal(0, balances.Accounts.Single(a => a.AccountId == Step5Scenario.Wohnung).Balance.Cents);
        // Pools wandern.
        Assert.Equal(50_000, balances.Pools.Single(p => p.PoolId == Step5Scenario.Flex).Balance.Cents);
        Assert.Equal(50_000, balances.Pools.Single(p => p.PoolId == Step5Scenario.Ruecklage).Balance.Cents);
        Assert.Equal(30_000, balances.Pools.Single(p => p.PoolId == Step5Scenario.PoolFreizeit).Balance.Cents);
        Assert.Equal(130_000, balances.TotalAccountBalance.Cents);
        Assert.Equal(130_000, balances.TotalPoolBalance.Cents);
        Assert.Equal(Money.Zero, balances.AccountPoolGap);

        // Kontoverteilung ist materiell in der Buchung gespeichert.
        var loaded = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-2026-1205");
        Assert.NotNull(loaded);
        Assert.Equal(
            [(Step5Scenario.Haupt, 50_000L)],
            loaded!.ReassignmentAccounts.Select(a => (a.AccountId, a.Amount.Cents)));
        Assert.Empty(loaded.AccountPostings);
        Assert.Empty(loaded.TransferPurposes);

        var journal = await Step5Scenario.JournalAsync(db);
        Assert.Equal(
            [
                new AccountPoolCell(Step5Scenario.Freizeit, Step5Scenario.PoolFreizeit, new Money(30_000)),
                new AccountPoolCell(Step5Scenario.Haupt, Step5Scenario.Flex, new Money(50_000)),
                new AccountPoolCell(Step5Scenario.Haupt, Step5Scenario.Ruecklage, new Money(50_000)),
            ],
            journal.Allocations());
        Assert.Empty(journal.AllocationInvariantViolations());
        Assert.Empty(await db.Resolve<SettlementQueryService>().GetAllAsync());
    }

    [Fact(DisplayName = "Implizite Kontouflösung: Pool wird nur von einem Konto getragen")]
    public async Task ImplicitAccount_IsResolvedAndMaterialized()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        // pool-flex liegt nur am Hauptkonto → Kontoverteilung eindeutig.
        var result = await db.Resolve<ReassignmentService>().CreateAsync(
            Reassignment(id: "rw-implizit", toPool: Step5Scenario.PoolFreizeit, cents: 10_000));
        Assert.True(result.Created);
        Assert.Equal(
            [(Step5Scenario.Haupt, 10_000L)],
            result.Booking.ReassignmentAccounts.Select(a => (a.AccountId, a.Amount.Cents)));

        var loaded = await db.Resolve<IBookingRepository>().FindByIdAsync("rw-implizit");
        Assert.Equal(
            [(Step5Scenario.Haupt, 10_000L)],
            loaded!.ReassignmentAccounts.Select(a => (a.AccountId, a.Amount.Cents)));
    }

    [Fact(DisplayName = "Umwidmung ohne verfügbaren Kontostand begründet einen internen Ausgleich")]
    public async Task Reassignment_CanCreateDebt()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        // 800 € werden dem Freizeitkonto zugeschrieben, das aber nur 300 €
        // hat — die Zelle (Freizeit, flex) wird negativ → Vorfinanzierung.
        await db.Resolve<ReassignmentService>().CreateAsync(
            Reassignment(id: "rw-schuld", cents: 80_000,
                accounts: [new AccountAllocation(Step5Scenario.Freizeit, new Money(80_000))]));

        // Kontostände bleiben unverändert; Pools wandern.
        var balances = await db.Resolve<BalanceQueryService>().GetBalancesAsync();
        Assert.Equal(100_000, balances.Accounts.Single(a => a.AccountId == Step5Scenario.Haupt).Balance.Cents);
        Assert.Equal(30_000, balances.Accounts.Single(a => a.AccountId == Step5Scenario.Freizeit).Balance.Cents);
        Assert.Equal(20_000, balances.Pools.Single(p => p.PoolId == Step5Scenario.Flex).Balance.Cents);
        Assert.Equal(80_000, balances.Pools.Single(p => p.PoolId == Step5Scenario.Ruecklage).Balance.Cents);

        var position = Assert.Single(await db.Resolve<SettlementQueryService>().GetOpenAsync());
        Assert.Equal(Step5Scenario.Freizeit, position.SourceAccountId);
        Assert.Equal(Step5Scenario.Flex, position.TargetPoolId);
        Assert.Equal(new Money(80_000), position.RestAmount);
    }

    [Fact(DisplayName = "Idempotenz: gleiche Umwidmung wird nicht erneut gespeichert")]
    public async Task Repeat_IsIdempotent()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        var service = db.Resolve<ReassignmentService>();
        var spec = Reassignment(accounts:
            [new AccountAllocation(Step5Scenario.Haupt, new Money(50_000))]);

        var first = await service.CreateAsync(spec);
        var second = await service.CreateAsync(spec);

        Assert.True(first.Created);
        Assert.False(second.Created);
        var reassignments = (await db.Resolve<IBookingRepository>().GetAllAsync())
            .Where(b => b.Type == BookingType.Reassignment)
            .ToList();
        Assert.Single(reassignments);
    }

    [Fact(DisplayName = "Gleiche Id mit anderem Betrag ist ein Konflikt")]
    public async Task ContentConflict_IsRejected()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        var service = db.Resolve<ReassignmentService>();

        await service.CreateAsync(Reassignment(accounts:
            [new AccountAllocation(Step5Scenario.Haupt, new Money(50_000))]));

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.CreateAsync(Reassignment(cents: 60_000, accounts:
                [new AccountAllocation(Step5Scenario.Haupt, new Money(60_000))])));

        Assert.Single(await db.Resolve<IBookingRepository>().GetAllAsync(),
            b => b.Type == BookingType.Reassignment);
    }

    [Fact(DisplayName = "FK-Verletzung (Geisterkonto in Kontoverteilung) verwirft alles komplett")]
    public async Task AtomicSave_NoPartialState_OnForeignKeyViolation()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        var repo = db.Resolve<IBookingRepository>();
        var invalid = new Booking(
            "rw-fk",
            BookingType.Reassignment,
            new DateOnly(2026, 11, 8),
            null,
            [],
            [
                new PoolPosting { PoolId = Step5Scenario.Flex, Amount = new Money(-5_000) },
                new PoolPosting { PoolId = Step5Scenario.Ruecklage, Amount = new Money(5_000) },
            ],
            reassignmentAccounts:
            [
                new AccountAllocation("acc-geister", new Money(5_000)),
            ]);

        await Assert.ThrowsAsync<DbUpdateException>(() => repo.AddAsync(invalid));

        Assert.Null(await repo.FindByIdAsync("rw-fk"));
        Assert.DoesNotContain(await repo.GetAllAsync(), b => b.Type == BookingType.Reassignment);
        Assert.Empty(await db.Resolve<ISettlementPositionRepository>().GetAllAsync());
    }
}

/// <summary>
/// Verbund-Operation (Transfer + Umwidmung in einer Buchung, Schritt 5):
/// zwei Kontobewegungen, zwei Zweckverteilungen, keine Umwidmungsbuchung;
/// Referenzzahlen und Idempotenz.
/// </summary>
public class TransferReassignmentPersistenceTests
{
    private static TransferReassignmentSpec Operation(
        string id = "bk-2026-1206",
        string from = Step5Scenario.Haupt,
        string to = Step5Scenario.Wohnung,
        string fromPool = Step5Scenario.Flex,
        string toPool = Step5Scenario.PoolFreizeit,
        long cents = 25_000,
        DateOnly? date = null) =>
        new()
        {
            Id = id,
            Date = date ?? new DateOnly(2026, 11, 9),
            FromAccountId = from,
            ToAccountId = to,
            Amount = new Money(cents),
            FromPoolId = fromPool,
            ToPoolId = toPool,
            Note = null,
        };

    [Fact(DisplayName = "Verbund: 250 € Haupt→Wohnung, Flex→Freizeit — keine Umwidmungsbuchung")]
    public async Task CompositeOperation_SingleBooking()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        var result = await db.Resolve<TransferReassignmentService>().CreateAsync(Operation());
        Assert.True(result.Created);

        var balances = await db.Resolve<BalanceQueryService>().GetBalancesAsync();
        Assert.Equal(75_000, balances.Accounts.Single(a => a.AccountId == Step5Scenario.Haupt).Balance.Cents);
        Assert.Equal(25_000, balances.Accounts.Single(a => a.AccountId == Step5Scenario.Wohnung).Balance.Cents);
        Assert.Equal(30_000, balances.Accounts.Single(a => a.AccountId == Step5Scenario.Freizeit).Balance.Cents);
        Assert.Equal(75_000, balances.Pools.Single(p => p.PoolId == Step5Scenario.Flex).Balance.Cents);
        Assert.Equal(55_000, balances.Pools.Single(p => p.PoolId == Step5Scenario.PoolFreizeit).Balance.Cents);
        Assert.Equal(130_000, balances.TotalAccountBalance.Cents);
        Assert.Equal(130_000, balances.TotalPoolBalance.Cents);

        var loaded = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-2026-1206");
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.AccountPostings.Count);
        Assert.Equal(2, loaded.PoolPostings.Count);
        // Die Kombination trägt keine Zusatzverteilungen — sie ist aus
        // ihren Postings vollständig ableitbar.
        Assert.Empty(loaded.TransferPurposes);
        Assert.Empty(loaded.ReassignmentAccounts);
        // Ein Verbund ist eine Buchung, keine Umwidmungsbuchung.
        Assert.DoesNotContain(await db.Resolve<IBookingRepository>().GetAllAsync(),
            b => b.Type == BookingType.Reassignment);
        Assert.Empty(await db.Resolve<SettlementQueryService>().GetAllAsync());

        // Zelle (Haupt, Flex) −250 € (Transfer-Zweck), Zelle (Wohnung,
        // pool-freizeit) +250 € (Umwidmungsteil).
        var journal = await Step5Scenario.JournalAsync(db);
        Assert.Equal(
            [
                new AccountPoolCell(Step5Scenario.Freizeit, Step5Scenario.PoolFreizeit, new Money(30_000)),
                new AccountPoolCell(Step5Scenario.Haupt, Step5Scenario.Flex, new Money(75_000)),
                new AccountPoolCell(Step5Scenario.Wohnung, Step5Scenario.PoolFreizeit, new Money(25_000)),
            ],
            journal.Allocations());
        Assert.Empty(journal.AllocationInvariantViolations());
    }

    [Fact(DisplayName = "Verbund kann einen internen Ausgleich begründen (Umwidmungsteil ohne Kontostand)")]
    public async Task CompositeOperation_CanCreateDebt()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        // 300 € fließen aus dem Freizeitkonto, das nur 300 € hat; die
        // Umwidmung Flex → Rücklage wird dem Freizeitkonto zugeschrieben.
        await db.Resolve<TransferReassignmentService>().CreateAsync(
            Operation(id: "verb-schuld",
                from: Step5Scenario.Freizeit, to: Step5Scenario.Haupt,
                toPool: Step5Scenario.Ruecklage, cents: 30_000));

        var balances = await db.Resolve<BalanceQueryService>().GetBalancesAsync();
        Assert.Equal(130_000, balances.Accounts.Single(a => a.AccountId == Step5Scenario.Haupt).Balance.Cents);
        Assert.Equal(0, balances.Accounts.Single(a => a.AccountId == Step5Scenario.Freizeit).Balance.Cents);
        Assert.Equal(70_000, balances.Pools.Single(p => p.PoolId == Step5Scenario.Flex).Balance.Cents);
        Assert.Equal(30_000, balances.Pools.Single(p => p.PoolId == Step5Scenario.PoolFreizeit).Balance.Cents);
        Assert.Equal(30_000, balances.Pools.Single(p => p.PoolId == Step5Scenario.Ruecklage).Balance.Cents);

        var position = Assert.Single(await db.Resolve<SettlementQueryService>().GetOpenAsync());
        Assert.Equal(Step5Scenario.Freizeit, position.SourceAccountId);
        Assert.Equal(Step5Scenario.Flex, position.TargetPoolId);
        Assert.Equal(new Money(30_000), position.RestAmount);
    }

    [Fact(DisplayName = "Idempotenz: gleiche Verbund-Operation wird nicht erneut gespeichert")]
    public async Task Repeat_IsIdempotent()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        var service = db.Resolve<TransferReassignmentService>();
        var spec = Operation();

        var first = await service.CreateAsync(spec);
        var second = await service.CreateAsync(spec);

        Assert.True(first.Created);
        Assert.False(second.Created);
        var composites = (await db.Resolve<IBookingRepository>().GetAllAsync())
            .Where(b => b.Type == BookingType.TransferReassignment)
            .ToList();
        Assert.Single(composites);
    }


    [Fact(DisplayName = "Gleiche Id mit anderem Betrag ist ein Konflikt")]
    public async Task ContentConflict_IsRejected()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        var service = db.Resolve<TransferReassignmentService>();

        await service.CreateAsync(Operation());

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.CreateAsync(Operation(cents: 30_000)));

        Assert.Single((await db.Resolve<IBookingRepository>().GetAllAsync())
            .Where(b => b.Type == BookingType.TransferReassignment)
            .ToList());
    }

    [Fact(DisplayName = "FK-Verletzung (Geisterpool in Zweckzeile) verwirft Verbund und Ausgleichszeilen komplett")]
    public async Task AtomicSave_NoPartialState_OnForeignKeyViolation()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        var repo = db.Resolve<IBookingRepository>();
        // Gültige Verbundform (gleiche Beträge auf Konto- und Poolseite),
        // aber das Zielkonto existiert nicht → FK-Verletzung.
        var invalid = new Booking(
            "verb-fk",
            BookingType.TransferReassignment,
            new DateOnly(2026, 11, 9),
            null,
            [
                new AccountPosting { AccountId = Step5Scenario.Haupt, Amount = new Money(-5_000) },
                new AccountPosting { AccountId = "acc-geister", Amount = new Money(5_000) },
            ],
            [
                new PoolPosting { PoolId = Step5Scenario.Flex, Amount = new Money(-5_000) },
                new PoolPosting { PoolId = Step5Scenario.Ruecklage, Amount = new Money(5_000) },
            ]);

        await Assert.ThrowsAsync<DbUpdateException>(() => repo.AddAsync(invalid));

        Assert.Null(await repo.FindByIdAsync("verb-fk"));
        Assert.DoesNotContain(await repo.GetAllAsync(), b => b.Type == BookingType.TransferReassignment);
        Assert.Empty(await db.Resolve<ISettlementPositionRepository>().GetAllAsync());
    }
}
