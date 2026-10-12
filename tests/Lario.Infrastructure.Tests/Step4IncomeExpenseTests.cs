using Lario.Application;
using Lario.Application.Journal;
using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;
using Lario.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lario.Infrastructure.Tests;

/// <summary>
/// Einnahmen und Ausgaben auf echter SQLite (Schritt 4): Migration mit
/// ExpenseSplits/SplitTaxMarkings/FundingMonth, atomare Speicherung,
/// Idempotenz, Konflikt, Rechenvertrag-Referenzfälle.
/// </summary>
public class IncomeExpenseMigrationTests
{
    [Fact(DisplayName = "Schritt-4-Migration legt ExpenseSplits und SplitTaxMarkings an")]
    public async Task Migration_CreatesStep4Tables()
    {
        await using var db = new TestDatabase();
        var tables = (await db.TableNamesAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("ExpenseSplits", tables, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("SplitTaxMarkings", tables, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Bookings", tables, StringComparer.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Bookings trägt die neue FundingMonth-Spalte")]
    public async Task Migration_AddsFundingMonthColumn()
    {
        await using var db = new TestDatabase();

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db.DatabaseFile}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(Bookings);";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Contains("FundingMonth", columns);
    }

    [Fact(DisplayName = "SplitTaxMarkings trägt die neue InheritedFromPreset-Spalte (Review P2b)")]
    public async Task Migration_AddsInheritedFromPresetColumn()
    {
        await using var db = new TestDatabase();

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db.DatabaseFile}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(SplitTaxMarkings);";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Contains("InheritedFromPreset", columns);
    }
}

/// <summary>
/// Einnahme auf echter SQLite (Schritt 4): Referenzfall 1 (1.100 €),
/// materieller Finanzierungsmonat, Idempotenz und Konflikte.
/// </summary>
public class IncomePersistenceTests
{
    private static async Task<IncomeService> SeedAsync(TestDatabase db)
    {
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.CreateAccountAsync("acc-haupt", "Hauptkonto", AccountType.Bank);
        await admin.CreateAccountAsync("acc-wohnung", "Wohnungskonto", AccountType.Bank);
        await admin.CreatePoolAsync("pool-flex", "Flex");
        await admin.CreatePoolAsync("pool-freizeit", "Freizeit");
        return db.Resolve<IncomeService>();
    }

    private static IncomeSpec Gehalt(
        string id = "geh-2026-10",
        long cents = 110_000,
        string accountId = "acc-wohnung",
        DateOnly? date = null,
        string? note = null,
        YearMonth? fundingMonth = null,
        (string pool, long cents)[]? allocations = null) =>
        new()
        {
            Id = id,
            Date = date ?? new DateOnly(2026, 10, 31),
            AccountId = accountId,
            Amount = new Money(cents),
            Note = note,
            FundingMonth = fundingMonth,
            Allocations = (allocations ?? [("pool-flex", 70_000), ("pool-freizeit", 40_000)])
                .Select(a => new IncomeAllocationSpec(a.pool, new Money(a.cents)))
                .ToList(),
        };

    [Fact(DisplayName = "Plan-Kernfall 1: 1.100 € Einnahme auf dem Wohnungskonto, Default-Finanzierungsmonat 2026-11")]
    public async Task PlanCase_1100_Euro_Income()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);

        var result = await service.CreateAsync(Gehalt());
        Assert.True(result.Created);
        Assert.Equal(YearMonth.Create(2026, 11), result.Booking.FundingMonth);

        var balances = await db.Resolve<BalanceQueryService>().GetBalancesAsync();
        Assert.Equal(110_000, balances.Accounts.Single(a => a.AccountId == "acc-wohnung").Balance.Cents);
        Assert.Equal(0, balances.Accounts.Single(a => a.AccountId == "acc-haupt").Balance.Cents);
        Assert.Equal(70_000, balances.Pools.Single(p => p.PoolId == "pool-flex").Balance.Cents);
        Assert.Equal(40_000, balances.Pools.Single(p => p.PoolId == "pool-freizeit").Balance.Cents);
        Assert.Equal(110_000, balances.TotalAccountBalance.Cents);
        Assert.Equal(110_000, balances.TotalPoolBalance.Cents);
        Assert.Equal(110_000, balances.TotalIncome.Cents);
        Assert.Equal(0, balances.AccountPoolGap.Cents);
    }

    [Fact(DisplayName = "Default-Finanzierungsmonat wird materiell gespeichert (neuer Ladevorgang liest 2026-11)")]
    public async Task FundingMonth_IsMaterializedAndPersisted()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);
        await service.CreateAsync(Gehalt());

        // Neuer Ladevorgang (neuer Scope): der Monat kommt aus der
        // Datenbank, nicht aus einer erneuten Default-Ableitung.
        var loaded = await db.Resolve<IBookingRepository>().FindByIdAsync("geh-2026-10");
        Assert.NotNull(loaded);
        Assert.Equal(YearMonth.Create(2026, 11), loaded!.FundingMonth);
        Assert.Equal(new DateOnly(2026, 10, 31), loaded.Date);
    }

    [Fact(DisplayName = "Idempotenz: dieselbe Id mit gleichem Inhalt erzeugt keine zweite Buchung")]
    public async Task Repeat_IsIdempotent()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);

        var first = await service.CreateAsync(Gehalt());
        var second = await service.CreateAsync(Gehalt());

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Booking.Id, second.Booking.Id);
        Assert.Single(await db.Resolve<IBookingRepository>().GetAllAsync());
    }

    [Fact(DisplayName = "Review P2c: default(YearMonth) als Finanzierungsmonat wird verworfen, nichts gespeichert")]
    public async Task FundingMonth_DefaultYearMonth_IsRejected()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);

        // default(YearMonth) ist 0000-00 (Konstrutor umgangen) — nicht
        // null (das wäre die gültige Default-Regel). Der ungültige Wert
        // muss vor der Materialisierung abgelehnt werden.
        await Assert.ThrowsAsync<LarioDomainException>(
            () => service.CreateAsync(Gehalt(fundingMonth: default(YearMonth))));

        Assert.Empty(await db.Resolve<IBookingRepository>().GetAllAsync());
    }

    [Fact(DisplayName = "Gleiche Id mit anderem Inhalt ist ein Konflikt; ursprüngliche Buchung bleibt")]
    public async Task ContentConflict_KeepsExisting()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);

        await service.CreateAsync(Gehalt(note: "Oktober"));

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.CreateAsync(Gehalt(note: "Oktober (geändert)")));

        var existing = await db.Resolve<IBookingRepository>().FindByIdAsync("geh-2026-10");
        Assert.Equal("Oktober", existing!.Note);
        Assert.Single(await db.Resolve<IBookingRepository>().GetAllAsync());
    }

    [Fact(DisplayName = "Id einer anderen Buchungstyp-Belegung ist ein Konflikt")]
    public async Task IdUsedByOtherBookingType_IsConflict()
    {
        await using var db = new TestDatabase();
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.CreateAccountAsync("acc-haupt", "Hauptkonto", AccountType.Bank);
        await admin.CreatePoolAsync("pool-flex", "Flex");

        var openingService = db.Resolve<OpeningBalanceService>();
        await openingService.CreateAsync(new OpeningBalanceSpec(
            "dup-id",
            new DateOnly(2026, 1, 1),
            "acc-haupt",
            new Money(10_000),
            [new PoolAllocation("pool-flex", new Money(10_000))]));

        await admin.CreatePoolAsync("pool-freizeit", "Freizeit");
        var incomeService = db.Resolve<IncomeService>();
        await Assert.ThrowsAsync<DomainConflictException>(
            () => incomeService.CreateAsync(Gehalt(id: "dup-id", accountId: "acc-haupt")));
    }

    [Fact(DisplayName = "Wiederholung nach Archivierung der beteiligten Konten/Pools wird idempotent bestätigt")]
    public async Task Repeat_AfterArchivedMasterData_IsConfirmed()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);
        await service.CreateAsync(Gehalt());

        var admin = db.Resolve<MasterDataAdmin>();
        await admin.ArchiveAccountAsync("acc-wohnung");
        await admin.ArchivePoolAsync("pool-flex");

        var repeat = await service.CreateAsync(Gehalt());
        Assert.False(repeat.Created);
        Assert.Single(await db.Resolve<IBookingRepository>().GetAllAsync());
    }

    [Fact(DisplayName = "Ohne stabile Id entsteht keine Buchung")]
    public async Task EmptyId_LeavesNoBooking()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);

        await Assert.ThrowsAsync<LarioDomainException>(
            () => service.CreateAsync(Gehalt(id: "")));

        Assert.Empty(await db.Resolve<IBookingRepository>().GetAllAsync());
    }

    [Fact(DisplayName = "FK-Verletzung (Geisterkonto) verwirft Buchung und Postings komplett")]
    public async Task AtomicSave_NoPartialState_OnForeignKeyViolation()
    {
        await using var db = new TestDatabase();
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.CreateAccountAsync("acc-haupt", "Hauptkonto", AccountType.Bank);
        await admin.CreatePoolAsync("pool-flex", "Flex");

        var repo = db.Resolve<IBookingRepository>();
        var invalid = new Booking(
            "ein-fk",
            BookingType.Income,
            new DateOnly(2026, 10, 5),
            null,
            [new AccountPosting { AccountId = "acc-geister", Amount = new Money(100) }],
            [new PoolPosting { PoolId = "pool-flex", Amount = new Money(100) }],
            fundingMonth: YearMonth.Create(2026, 11));

        await Assert.ThrowsAsync<DbUpdateException>(() => repo.AddAsync(invalid));

        Assert.Empty(await repo.GetAllAsync());
        Assert.Null(await repo.FindByIdAsync("ein-fk"));
    }
}

/// <summary>
/// Ausgabe auf echter SQLite (Schritt 4): Referenzfälle 2/4 (85 €,
/// 84,99 €), Split-Persistenz, Steuerkennzeichnung als Snapshot,
/// Idempotenz und Konflikte.
/// </summary>
public class ExpensePersistenceTests
{
    private static async Task<ExpenseService> SeedAsync(TestDatabase db)
    {
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.CreateAccountAsync("acc-haupt", "Hauptkonto", AccountType.Bank);
        await admin.CreatePoolAsync("pool-flex", "Flex");
        await admin.CreatePoolAsync("pool-freizeit", "Freizeit");
        await admin.CreateCategoryAsync("cat-wohnen", "Wohnen");
        await admin.CreateCategoryAsync("cat-miete", "Miete", parentId: "cat-wohnen");
        await admin.CreateCategoryAsync("cat-freizeit", "Freizeit");
        await admin.CreateTaxCategoryAsync("tax-1", "Steuern");
        return db.Resolve<ExpenseService>();
    }

    private static ExpenseSpec Kauf(
        string id = "eink-2026-10",
        long totalCents = 8_500,
        (string splitId, long cents, string pool, string category)[]? splits = null,
        string? note = null,
        DateOnly? date = null) =>
        new()
        {
            Id = id,
            Date = date ?? new DateOnly(2026, 10, 12),
            AccountId = "acc-haupt",
            TotalAmount = new Money(totalCents),
            Note = note,
            Splits = (splits ??
                [("sp-1", 6_500, "pool-flex", "cat-miete"), ("sp-2", 2_000, "pool-freizeit", "cat-freizeit")])
                .Select(s => new ExpenseSplitSpec
                {
                    Id = s.splitId,
                    Amount = new Money(s.cents),
                    PoolId = s.pool,
                    CategoryId = s.category,
                })
                .ToList(),
        };

    [Fact(DisplayName = "Plan-Kernfall 2: 85 € = 65 € Flex + 20 € Freizeit, beide Pools auf dem Zahlungskonto")]
    public async Task PlanCase_85_Euro_Expense()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);

        var result = await service.CreateAsync(Kauf());
        Assert.True(result.Created);

        var balances = await db.Resolve<BalanceQueryService>().GetBalancesAsync();
        Assert.Equal(-8_500, balances.Accounts.Single().Balance.Cents);
        Assert.Equal(-6_500, balances.Pools.Single(p => p.PoolId == "pool-flex").Balance.Cents);
        Assert.Equal(-2_000, balances.Pools.Single(p => p.PoolId == "pool-freizeit").Balance.Cents);
        Assert.Equal(0, balances.TotalIncome.Cents);
        Assert.Equal(0, balances.AccountPoolGap.Cents);
    }

    [Fact(DisplayName = "Plan-Referenz 4: 84,99 € Splits zu 85 € Gesamtbetrag — abgelehnt, keine Buchung")]
    public async Task PlanCase_8499_SplitSum_IsRejected()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);

        await Assert.ThrowsAsync<LarioDomainException>(
            () => service.CreateAsync(Kauf(
                totalCents: 8_500,
                splits: [("sp-1", 6_500, "pool-flex", "cat-miete"), ("sp-2", 1_999, "pool-freizeit", "cat-freizeit")])));

        Assert.Empty(await db.Resolve<IBookingRepository>().GetAllAsync());
    }

    [Fact(DisplayName = "Roundtrip: Split, Betrag, Pool, Kategorie und Notiz überleben Neuladen")]
    public async Task Expense_WithSplits_Roundtrips()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);
        await service.CreateAsync(Kauf(note: "Wochenendeinkauf"));

        var loaded = await db.Resolve<IBookingRepository>().FindByIdAsync("eink-2026-10");
        Assert.NotNull(loaded);
        Assert.Equal(BookingType.Expense, loaded!.Type);
        Assert.Single(loaded.AccountPostings);
        Assert.Equal(-8_500, loaded.AccountPostings[0].Amount.Cents);

        var s1 = loaded.Splits.Single(s => s.Id == "sp-1");
        Assert.Equal(6_500, s1.Amount.Cents);
        Assert.Equal("pool-flex", s1.PoolId);
        Assert.Equal("cat-miete", s1.CategoryId);
        Assert.Null(s1.Tax);
        Assert.Equal(2_000, loaded.Splits.Single(s => s.Id == "sp-2").Amount.Cents);
    }

    [Fact(DisplayName = "Roundtrip: Steuerkennzeichnung mit Teilbetrag überlebt Neuladen und ändert keine Salden")]
    public async Task TaxMarking_Roundtrips_WithoutAffectingBalances()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);

        var spec = new ExpenseSpec
        {
            Id = "eink-2026-10",
            Date = new DateOnly(2026, 10, 12),
            AccountId = "acc-haupt",
            TotalAmount = new Money(10_000),
            Splits =
            [
                new ExpenseSplitSpec
                {
                    Id = "sp-1",
                    Amount = new Money(10_000),
                    PoolId = "pool-flex",
                    CategoryId = "cat-miete",
                    Tax = new TaxMarking
                    {
                        Relevant = true,
                        TaxCategoryId = "tax-1",
                        MarkedAmount = new Money(1_900),
                        Note = "Vorsteuer",
                    },
                },
            ],
        };
        await service.CreateAsync(spec);

        var loaded = await db.Resolve<IBookingRepository>().FindByIdAsync("eink-2026-10");
        var marking = loaded!.Splits.Single().Tax!;
        Assert.True(marking.Relevant);
        Assert.Equal("tax-1", marking.TaxCategoryId);
        Assert.Equal(1_900, marking.MarkedAmount!.Value.Cents);
        Assert.Equal("Vorsteuer", marking.Note);

        // Die Kennzeichnung ist reines Fachdaten: Salden bleiben,
        // wie ohne Kennzeichnung.
        var balances = await db.Resolve<BalanceQueryService>().GetBalancesAsync();
        Assert.Equal(-10_000, balances.Accounts.Single().Balance.Cents);
        Assert.Equal(-10_000, balances.Pools.Single(p => p.PoolId == "pool-flex").Balance.Cents);
        Assert.Equal(0, balances.AccountPoolGap.Cents);
    }

    [Fact(DisplayName = "Voreinstellung wird bei Erstellung materialisiert; spätere Änderung schreibt nicht um")]
    public async Task Preset_MaterializedAsSnapshot_NotReDerived()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);
        var admin = db.Resolve<MasterDataAdmin>();

        // Voreinstellung 'Miete' = steuerlich relevant, Kategorie tax-1.
        await admin.SetTaxPresetAsync("cat-miete", true, "tax-1", inheritToChildren: false);
        await service.CreateAsync(Kauf());

        // Später wird die Voreinstellung geändert (und entfernt).
        await admin.SetTaxPresetAsync("cat-miete", false, null, inheritToChildren: false);
        await admin.RemoveTaxPresetAsync("cat-miete");

        // Die gespeicherte Kennzeichnung ist ein Snapshot: unverändert.
        var loaded = await db.Resolve<IBookingRepository>().FindByIdAsync("eink-2026-10");
        var marking = loaded!.Splits.Single(s => s.Id == "sp-1").Tax!;
        Assert.True(marking.Relevant);
        Assert.Equal("tax-1", marking.TaxCategoryId);

        // Und die Idempotenz bestätigt dieselbe Vorgabe — die
        // (mittlerweile geänderte) Voreinstellung wird nicht neu abgeleitet.
        var repeat = await service.CreateAsync(Kauf());
        Assert.False(repeat.Created);
        Assert.Single(await db.Resolve<IBookingRepository>().GetAllAsync());
    }

    [Fact(DisplayName = "Review P1: doppelte Split-Ids in der Wiederholung sind ein Konflikt, keine stille Idempotenz")]
    public async Task Repeat_WithDuplicateSplitIds_IsConflict()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);

        await service.CreateAsync(Kauf());

        // Dieselbe Id und dieselbe Gesamtmenge wie das Original, aber
        // beide Teilposten tragen die Id 'sp-1' statt [sp-1, sp-2]: die
        // Vorgabe ist kein 1:1-Abbild des Originals. Vor der Korrektur
        // passte jede Zeile einzeln an dieselbe gespeicherte Zeile und
        // die Wiederholung wurde still als identisch bestätigt.
        var duplicate = new ExpenseSpec
        {
            Id = "eink-2026-10",
            Date = new DateOnly(2026, 10, 12),
            AccountId = "acc-haupt",
            TotalAmount = new Money(8_500),
            Splits =
            [
                new ExpenseSplitSpec { Id = "sp-1", Amount = new Money(6_500), PoolId = "pool-flex", CategoryId = "cat-miete" },
                new ExpenseSplitSpec { Id = "sp-1", Amount = new Money(2_000), PoolId = "pool-freizeit", CategoryId = "cat-freizeit" },
            ],
        };

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.CreateAsync(duplicate));

        // Das Original bleibt unverändert (zwei Teilposten).
        var existing = await db.Resolve<IBookingRepository>().FindByIdAsync("eink-2026-10");
        Assert.NotNull(existing);
        Assert.Equal(2, existing!.Splits.Count);
        Assert.Single(await db.Resolve<IBookingRepository>().GetAllAsync());
    }

    [Fact(DisplayName = "Review P2: Umordnung der Split-Ids in der Wiederholung bleibt idempotent (1:1-Abbildung, nicht Reihenfolge)")]
    public async Task Repeat_WithReorderedSplits_IsIdempotent()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);

        await service.CreateAsync(Kauf());

        // Dieselben zwei Teilposten in umgekehrter Reihenfolge: zulässige
        // Wiederholung (die Zuordnung ist 1:1 nach Teilposten-Id, die
        // Reihenfolge trägt keinen Inhalt).
        var reordered = new ExpenseSpec
        {
            Id = "eink-2026-10",
            Date = new DateOnly(2026, 10, 12),
            AccountId = "acc-haupt",
            TotalAmount = new Money(8_500),
            Splits =
            [
                new ExpenseSplitSpec { Id = "sp-2", Amount = new Money(2_000), PoolId = "pool-freizeit", CategoryId = "cat-freizeit" },
                new ExpenseSplitSpec { Id = "sp-1", Amount = new Money(6_500), PoolId = "pool-flex", CategoryId = "cat-miete" },
            ],
        };

        var repeat = await service.CreateAsync(reordered);
        Assert.False(repeat.Created);
        Assert.Single(await db.Resolve<IBookingRepository>().GetAllAsync());
    }

    [Fact(DisplayName = "Review P2b: Snapshot aus Voreinstellung — Wiederholung mit expliziter Kennzeichnung ist ein Konflikt")]
    public async Task Repeat_WithExplicitTax_OverPresetSnapshot_IsConflict()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);
        var admin = db.Resolve<MasterDataAdmin>();

        // Voreinstellung 'Miete' = steuerlich relevant, tax-1: die Anlage
        // ohne explizite Kennzeichnung übernimmt sie als Snapshot.
        await admin.SetTaxPresetAsync("cat-miete", true, "tax-1", inheritToChildren: false);
        await service.CreateAsync(Kauf());

        var stored = (await db.Resolve<IBookingRepository>().FindByIdAsync("eink-2026-10"))!
            .Splits.Single(s => s.Id == "sp-1").Tax!;
        Assert.True(stored.Relevant);
        Assert.Equal("tax-1", stored.TaxCategoryId);
        Assert.True(stored.InheritedFromPreset);

        // Die Wiederholung trägt dieselben Kennzeichnungswerte, aber
        // EXPLIZIT — gegenüber dem Original (keine Kennzeichnung) ist das
        // ein Inhaltswechsel und kein Idempotenz-Treffer. Vor der
        // Korrektur wurde die Wiederholung still bestätigt.
        var explicitRepeat = new ExpenseSpec
        {
            Id = "eink-2026-10",
            Date = new DateOnly(2026, 10, 12),
            AccountId = "acc-haupt",
            TotalAmount = new Money(8_500),
            Splits =
            [
                new ExpenseSplitSpec
                {
                    Id = "sp-1",
                    Amount = new Money(6_500),
                    PoolId = "pool-flex",
                    CategoryId = "cat-miete",
                    Tax = new TaxMarking { Relevant = true, TaxCategoryId = "tax-1" },
                },
                new ExpenseSplitSpec { Id = "sp-2", Amount = new Money(2_000), PoolId = "pool-freizeit", CategoryId = "cat-freizeit" },
            ],
        };

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.CreateAsync(explicitRepeat));

        // Die Wiederholung OHNE Kennzeichnung bestätigt dagegen
        // idempotent (die Voreinstellung kann sich inzwischen geändert
        // haben — das ist kein Konflikt, sondern derselbe Inhalt).
        var noTaxRepeat = await service.CreateAsync(Kauf());
        Assert.False(noTaxRepeat.Created);
        Assert.Single(await db.Resolve<IBookingRepository>().GetAllAsync());
    }

    [Fact(DisplayName = "Review P2b: explizit gespeicherte Kennzeichnung — gleiche Wiederholung idempotent, abweichende ist Konflikt")]
    public async Task Repeat_WithSameExplicitTax_IsIdempotent()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);

        ExpenseSpec Make(Money markedAmount) => new()
        {
            Id = "eink-tax",
            Date = new DateOnly(2026, 10, 12),
            AccountId = "acc-haupt",
            TotalAmount = new Money(10_000),
            Splits =
            [
                new ExpenseSplitSpec
                {
                    Id = "sp-1",
                    Amount = new Money(10_000),
                    PoolId = "pool-flex",
                    CategoryId = "cat-miete",
                    Tax = new TaxMarking
                    {
                        Relevant = true,
                        TaxCategoryId = "tax-1",
                        MarkedAmount = markedAmount,
                    },
                },
            ],
        };

        var first = await service.CreateAsync(Make(new Money(1_900)));
        Assert.True(first.Created);

        var stored = (await db.Resolve<IBookingRepository>().FindByIdAsync("eink-tax"))!
            .Splits.Single().Tax!;
        Assert.False(stored.InheritedFromPreset);

        // Exakt dieselbe explizite Kennzeichnung → Idempotenz-Treffer.
        var second = await service.CreateAsync(Make(new Money(1_900)));
        Assert.False(second.Created);

        // Abweichende explizite Kennzeichnung → Konflikt.
        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.CreateAsync(Make(new Money(2_000))));

        // Explizit → null ist ebenfalls ein Konflikt: null bedeutet
        // „wirksame Voreinstellung übernehmen" (hier: keine Markierung),
        // nicht „die gespeicherte explizite Kennzeichnung".
        var explicitToNull = new ExpenseSpec
        {
            Id = "eink-tax",
            Date = new DateOnly(2026, 10, 12),
            AccountId = "acc-haupt",
            TotalAmount = new Money(10_000),
            Splits =
            [
                new ExpenseSplitSpec
                {
                    Id = "sp-1",
                    Amount = new Money(10_000),
                    PoolId = "pool-flex",
                    CategoryId = "cat-miete",
                },
            ],
        };
        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.CreateAsync(explicitToNull));
        Assert.Single(await db.Resolve<IBookingRepository>().GetAllAsync());
    }

    [Fact(DisplayName = "Idempotenz: dieselbe Id mit gleichem Inhalt erzeugt keine zweite Buchung")]
    public async Task Repeat_IsIdempotent()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);

        var first = await service.CreateAsync(Kauf());
        var second = await service.CreateAsync(Kauf());

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Single(await db.Resolve<IBookingRepository>().GetAllAsync());
    }

    [Fact(DisplayName = "Gleiche Id mit anderem Inhalt ist ein Konflikt; ursprüngliche Buchung bleibt")]
    public async Task ContentConflict_KeepsExisting()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);

        await service.CreateAsync(Kauf(note: "Original"));

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.CreateAsync(Kauf(note: "Geändert")));

        var existing = await db.Resolve<IBookingRepository>().FindByIdAsync("eink-2026-10");
        Assert.Equal("Original", existing!.Note);
        Assert.Single(await db.Resolve<IBookingRepository>().GetAllAsync());
    }

    [Fact(DisplayName = "FK-Verletzung (Geisterkategorie im Split) verwirft Buchung, Postings und Splits komplett")]
    public async Task AtomicSave_NoPartialState_OnSplitForeignKeyViolation()
    {
        await using var db = new TestDatabase();
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.CreateAccountAsync("acc-haupt", "Hauptkonto", AccountType.Bank);
        await admin.CreatePoolAsync("pool-flex", "Flex");
        await admin.CreateCategoryAsync("cat-1", "Wohnen");

        var repo = db.Resolve<IBookingRepository>();
        var invalid = new Booking(
            "aus-fk",
            BookingType.Expense,
            new DateOnly(2026, 10, 12),
            null,
            [new AccountPosting { AccountId = "acc-haupt", Amount = new Money(-100) }],
            [new PoolPosting { PoolId = "pool-flex", Amount = new Money(-100) }],
            splits: [new ExpenseSplit
            {
                Id = "sp-1",
                Amount = new Money(100),
                PoolId = "pool-flex",
                CategoryId = "cat-geister",
            }]);

        await Assert.ThrowsAsync<DbUpdateException>(() => repo.AddAsync(invalid));

        // Weder Buchung noch Postings noch Split durften bleiben.
        Assert.Empty(await repo.GetAllAsync());
        Assert.Null(await repo.FindByIdAsync("aus-fk"));

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db.DatabaseFile}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ExpenseSplits;";
        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact(DisplayName = "Unterdeckung bleibt sichtbar: Poolbestand wird negativ, Lücke bleibt 0")]
    public async Task UnderfundedPool_StaysVisible()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);

        // Kein Anfangsbestand: der Pool hat 0 €, die Ausgabe überzieht ihn.
        await service.CreateAsync(Kauf(totalCents: 8_500,
            splits: [("sp-1", 8_500, "pool-flex", "cat-miete")]));

        var balances = await db.Resolve<BalanceQueryService>().GetBalancesAsync();
        Assert.Equal(-8_500, balances.Pools.Single(p => p.PoolId == "pool-flex").Balance.Cents);
        Assert.Equal(-8_500, balances.Accounts.Single().Balance.Cents);
        // Konto und Pool sinken gleich weit: die signierte Zuordnung bleibt,
        // die Unterdeckung ist am negativen Poolbestand sichtbar.
        Assert.Equal(0, balances.AccountPoolGap.Cents);
    }

    [Fact(DisplayName = "Kernfall Schritt 4: mehrere gekennzeichnete Splits derselben Buchung speichern und laden")]
    public async Task MultipleMarkedSplits_SaveAndLoad()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);

        // Zwei Teilposten, beide steuerlich gekennzeichnet (einer mit
        // Teilbetrag, einer ohne). Vor der Korrektur des SplitTaxMarkings-
        // Mappings (Komposit-PK) scheiterte genau dieser Fall mit einem
        // Tracking-Konflikt, weil der Primärschlüssel effektiv auf der
        // Buchung lag.
        var spec = new ExpenseSpec
        {
            Id = "eink-2mark",
            Date = new DateOnly(2026, 10, 12),
            AccountId = "acc-haupt",
            TotalAmount = new Money(8_500),
            Splits =
            [
                new ExpenseSplitSpec
                {
                    Id = "sp-1",
                    Amount = new Money(6_500),
                    PoolId = "pool-flex",
                    CategoryId = "cat-miete",
                    Tax = new TaxMarking { Relevant = true, TaxCategoryId = "tax-1", MarkedAmount = new Money(1_000) },
                },
                new ExpenseSplitSpec
                {
                    Id = "sp-2",
                    Amount = new Money(2_000),
                    PoolId = "pool-freizeit",
                    CategoryId = "cat-freizeit",
                    Tax = new TaxMarking { Relevant = true, TaxCategoryId = "tax-1" },
                },
            ],
        };

        var result = await service.CreateAsync(spec);
        Assert.True(result.Created);

        var loaded = await db.Resolve<IBookingRepository>().FindByIdAsync("eink-2mark");
        Assert.NotNull(loaded);
        var marked = loaded!.Splits.Where(s => s.Tax is not null).ToList();
        Assert.Equal(2, marked.Count);

        var m1 = loaded.Splits.Single(s => s.Id == "sp-1").Tax!;
        Assert.True(m1.Relevant);
        Assert.Equal("tax-1", m1.TaxCategoryId);
        Assert.Equal(1_000, m1.MarkedAmount!.Value.Cents);

        var m2 = loaded.Splits.Single(s => s.Id == "sp-2").Tax!;
        Assert.True(m2.Relevant);
        Assert.Equal("tax-1", m2.TaxCategoryId);
        Assert.Null(m2.MarkedAmount);
    }

    [Fact(DisplayName = "Neustart: zweiter Provider findet Buchung, Splits und Kennzeichnung")]
    public async Task Restart_FindsSameBookings()
    {
        await using var db = new TestDatabase();
        var service = await SeedAsync(db);
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.SetTaxPresetAsync("cat-miete", true, "tax-1", inheritToChildren: false);
        await service.CreateAsync(Kauf());
        await db.Resolve<IncomeService>().CreateAsync(GehaltSpec());

        using var services = db.NewServiceProvider();
        using var scope = services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
        var all = await repo.GetAllAsync();

        Assert.Equal(2, all.Count);

        var expense = all.Single(b => b.Id == "eink-2026-10");
        Assert.Equal(2, expense.Splits.Count);
        Assert.NotNull(expense.Splits.Single(s => s.Id == "sp-1").Tax);

        var income = all.Single(b => b.Id == "geh-2026-10");
        Assert.Equal(YearMonth.Create(2026, 11), income.FundingMonth);
    }

    private static IncomeSpec GehaltSpec() => new()
    {
        Id = "geh-2026-10",
        Date = new DateOnly(2026, 10, 31),
        AccountId = "acc-haupt",
        Amount = new Money(110_000),
        Allocations =
        [
            new IncomeAllocationSpec("pool-flex", new Money(70_000)),
            new IncomeAllocationSpec("pool-freizeit", new Money(40_000)),
        ],
    };
}
