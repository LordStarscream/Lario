using Lario.Application;
using Lario.Application.Journal;
using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lario.Infrastructure.Tests;

/// <summary>
/// Schritt 5 (Transfers, Umwidmung, interne Ausgleiche) auf echter
/// SQLite: Migration (TransferPurposes/ReassignmentAccounts/
/// SettlementPositions), atomare Speicherung, Idempotenz, Konflikte
/// und die Begleichungsreferenz (Rechenvertrag §3.5).
/// </summary>
public class TransferSettlementMigrationTests
{
    [Fact(DisplayName = "Schritt-5-Migration legt TransferPurposes, ReassignmentAccounts und SettlementPositions an")]
    public async Task Migration_CreatesStep5Tables()
    {
        await using var db = new TestDatabase();
        var tables = (await db.TableNamesAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("TransferPurposes", tables, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ReassignmentAccounts", tables, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("SettlementPositions", tables, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Bookings", tables, StringComparer.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Bookings trägt die neue RelatedSettlementId-Spalte")]
    public async Task Migration_AddsRelatedSettlementIdColumn()
    {
        await using var db = new TestDatabase();
        Assert.Contains(
            "RelatedSettlementId",
            await ColumnNamesAsync(db, "Bookings"));
    }

    [Fact(DisplayName = "SettlementPositions: eindeutige Episode (Konto, Pool, erstellende Buchung)")]
    public async Task Migration_SettlementPositionsHasUniqueEpisodeIndex()
    {
        await using var db = new TestDatabase();

        await using var connection =
            new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db.DatabaseFile}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA index_list('SettlementPositions');";
        await using var reader = await command.ExecuteReaderAsync();

        string? uniqueIndex = null;
        while (await reader.ReadAsync())
        {
            // Spalten: seq, name, unique, origin, partial.
            // Die PK ist ebenfalls unique (als sqlite_autoindex_… mit
            // origin 'pk' gelistet) — sie ist nicht die Episoden-
            // Eindeutigkeit und wird über den Origin ausgeschlossen.
            var name = reader.GetString(1);
            var origin = reader.GetString(3);
            if (reader.GetInt32(2) == 1 && origin != "pk")
            {
                uniqueIndex = name;
            }
        }

        Assert.NotNull(uniqueIndex);
        reader.Dispose();

        await using var info = connection.CreateCommand();
        info.CommandText = $"PRAGMA index_info('{uniqueIndex}');";
        await using var infoReader = await info.ExecuteReaderAsync();

        var columns = new List<string>();
        while (await infoReader.ReadAsync())
        {
            // Spalten: seqno, cid, name
            var name = infoReader.IsDBNull(2) ? null : infoReader.GetString(2);
            if (name is not null)
            {
                columns.Add(name);
            }
        }

        Assert.Equal(
            ["SourceAccountId", "TargetPoolId", "CreatedByBookingId"],
            columns);
    }

    private static async Task<IReadOnlyList<string>> ColumnNamesAsync(
        TestDatabase db, string table)
    {
        await using var connection =
            new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db.DatabaseFile}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }
}

/// <summary>
/// Startzustand der Referenzfälle 04/05/06 (Plan: 1.000 € Hauptkonto
/// als Flex, 300 € Freizeitkonto als Freizeit) plus optional der
/// 85-€-Misch-Einkauf aus Referenzfall 3 (begründet den offenen
/// internen Ausgleich von 20 €).
/// </summary>
internal static class Step5Scenario
{
    public const string Haupt = "acc-haupt";
    public const string Freizeit = "acc-freizeit";
    public const string Wohnung = "acc-wohnung";
    public const string Flex = "pool-flex";
    public const string PoolFreizeit = "pool-freizeit";
    public const string Ruecklage = "pool-ruecklage";

    /// <summary>Stammdaten + Anfangsbestände (1.000 € Flex, 300 € Freizeit).</summary>
    public static async Task SeedAsync(TestDatabase db)
    {
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.CreateAccountAsync(Haupt, "Hauptkonto", AccountType.Bank);
        await admin.CreateAccountAsync(Freizeit, "Freizeitkonto", AccountType.Bank);
        await admin.CreateAccountAsync(Wohnung, "Wohnungskonto", AccountType.Bank);
        await admin.CreatePoolAsync(Flex, "Flex");
        await admin.CreatePoolAsync(PoolFreizeit, "Freizeit");
        await admin.CreatePoolAsync(Ruecklage, "Rücklage");
        await admin.CreateCategoryAsync("cat-lebensmittel", "Lebensmittel");
        await admin.CreateCategoryAsync("cat-freizeit", "Freizeit");

        var opening = db.Resolve<OpeningBalanceService>();
        var stichtag = new DateOnly(2026, 1, 1);
        await opening.CreateAsync(new OpeningBalanceSpec(
            "init-2026-01-01", stichtag, Haupt, new Money(100_000),
            [new PoolAllocation(Flex, new Money(100_000))]));
        await opening.CreateAsync(new OpeningBalanceSpec(
            "init-2026-01-01-freizeit", stichtag, Freizeit, new Money(30_000),
            [new PoolAllocation(PoolFreizeit, new Money(30_000))]));
    }

    /// <summary>
    /// Gemischter Einkauf 85,00 € vom Hauptkonto (Referenzfall 3,
    /// bk-2026-1202): 65 € Flex + 20 € Freizeit — der Freizeit-Anteil
    /// wird über das Hauptkonto bezahlt → Vorfinanzierung (20 € offen).
    /// </summary>
    public static async Task<Booking> AddMixedPurchaseAsync(TestDatabase db)
    {
        var service = db.Resolve<ExpenseService>();
        var result = await service.CreateAsync(new ExpenseSpec
        {
            Id = "bk-2026-1202",
            Date = new DateOnly(2026, 11, 5),
            AccountId = Haupt,
            TotalAmount = new Money(8_500),
            Note = "Gemischter Einkauf",
            Splits =
            [
                new ExpenseSplitSpec
                {
                    Id = "sp-1",
                    Amount = new Money(6_500),
                    PoolId = Flex,
                    CategoryId = "cat-lebensmittel",
                },
                new ExpenseSplitSpec
                {
                    Id = "sp-2",
                    Amount = new Money(2_000),
                    PoolId = PoolFreizeit,
                    CategoryId = "cat-freizeit",
                    Tax = new TaxMarking { Relevant = false },
                },
            ],
        });
        Assert.True(result.Created);
        return result.Booking;
    }

    /// <summary>Alle Buchungen als Journal (Zuordnungen/Invarianten aus der DB).</summary>
    public static async Task<BookingJournal> JournalAsync(TestDatabase db) =>
        new(await db.Resolve<IBookingRepository>().GetAllAsync());

    /// <summary>Kontostand eines Kontos aus der persistierten Bilanzansicht.</summary>
    public static async Task<long> AccountBalanceAsync(TestDatabase db, string accountId) =>
        (await db.Resolve<BalanceQueryService>().GetBalancesAsync())
            .Accounts.Single(a => a.AccountId == accountId).Balance.Cents;

    /// <summary>Poolbestand aus der persistierten Bilanzansicht.</summary>
    public static async Task<long> PoolBalanceAsync(TestDatabase db, string poolId) =>
        (await db.Resolve<BalanceQueryService>().GetBalancesAsync())
            .Pools.Single(p => p.PoolId == poolId).Balance.Cents;
}

/// <summary>
/// Reiner Kontotransfer auf echter SQLite (Schritt 5): Referenzfall 5
/// (800 € zweckerhaltend), implizite Zweckauflösung, Idempotenz,
/// Konflikte, Quellkontostandsprüfung, FK-Atomarität.
/// </summary>
public class TransferPersistenceTests
{
    private static TransferSpec Transfer(
        string id = "bk-2026-1204",
        string from = Step5Scenario.Haupt,
        string to = Step5Scenario.Wohnung,
        long cents = 80_000,
        IReadOnlyList<PoolAllocation>? purposes = null,
        string? relatedSettlementId = null,
        DateOnly? date = null,
        string? note = null) =>
        new()
        {
            Id = id,
            Date = date ?? new DateOnly(2026, 11, 7),
            FromAccountId = from,
            ToAccountId = to,
            Amount = new Money(cents),
            Purposes = purposes,
            RelatedSettlementId = relatedSettlementId,
            Note = note,
        };

    [Fact(DisplayName = "Referenzfall 5: 800 € Transfer, Pools unverändert, Zuordnung wandert mit")]
    public async Task ReferenceCase5_TransferMovesAllocation()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        var result = await db.Resolve<TransferService>().CreateAsync(
            Transfer(purposes: [new PoolAllocation(Step5Scenario.Flex, new Money(80_000))]));
        Assert.True(result.Created);

        var balances = await db.Resolve<BalanceQueryService>().GetBalancesAsync();
        Assert.Equal(20_000, balances.Accounts.Single(a => a.AccountId == Step5Scenario.Haupt).Balance.Cents);
        Assert.Equal(30_000, balances.Accounts.Single(a => a.AccountId == Step5Scenario.Freizeit).Balance.Cents);
        Assert.Equal(80_000, balances.Accounts.Single(a => a.AccountId == Step5Scenario.Wohnung).Balance.Cents);
        // Reiner Transfer: keine Poolbewegung.
        Assert.Equal(100_000, balances.Pools.Single(p => p.PoolId == Step5Scenario.Flex).Balance.Cents);
        Assert.Equal(30_000, balances.Pools.Single(p => p.PoolId == Step5Scenario.PoolFreizeit).Balance.Cents);
        Assert.Equal(130_000, balances.TotalAccountBalance.Cents);
        Assert.Equal(130_000, balances.TotalPoolBalance.Cents);
        Assert.Equal(Money.Zero, balances.AccountPoolGap);

        // Zweckverteilung ist materiell in der Buchung gespeichert.
        var loaded = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-2026-1204");
        Assert.NotNull(loaded);
        Assert.Equal(
            [(Step5Scenario.Flex, 80_000L)],
            loaded!.TransferPurposes.Select(p => (p.PoolId, p.Amount.Cents)));
        Assert.Null(loaded.FundingMonth);
        Assert.Empty(loaded.PoolPostings);

        // Die Zuordnung aus dem persistierten Journal stimmt mit dem
        // Referenzfall überein: Zeilensummen = Kontostände, Spaltensummen
        // = Poolbestände, keine Invariantenverletzung.
        var journal = await Step5Scenario.JournalAsync(db);
        Assert.Equal(
            [
                new AccountPoolCell(Step5Scenario.Freizeit, Step5Scenario.PoolFreizeit, new Money(30_000)),
                new AccountPoolCell(Step5Scenario.Haupt, Step5Scenario.Flex, new Money(20_000)),
                new AccountPoolCell(Step5Scenario.Wohnung, Step5Scenario.Flex, new Money(80_000)),
            ],
            journal.Allocations());
        Assert.Empty(journal.AllocationInvariantViolations());
        Assert.Empty(await db.Resolve<SettlementQueryService>().GetAllAsync());
    }

    [Fact(DisplayName = "Implizite Zweckauflösung: eindeutige positive Zelle des Quellkontos")]
    public async Task ImplicitPurpose_IsResolvedAndMaterialized()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        // Das Hauptkonto trägt nur pool-flex → Zweck ist eindeutig.
        var result = await db.Resolve<TransferService>().CreateAsync(
            Transfer(id: "tr-implizit", cents: 10_000, purposes: null));
        Assert.True(result.Created);
        // Die Auflösung wird materiell in der Buchung gespeichert.
        Assert.Equal(
            [(Step5Scenario.Flex, 10_000L)],
            result.Booking.TransferPurposes.Select(p => (p.PoolId, p.Amount.Cents)));

        var loaded = await db.Resolve<IBookingRepository>().FindByIdAsync("tr-implizit");
        Assert.Equal(
            [(Step5Scenario.Flex, 10_000L)],
            loaded!.TransferPurposes.Select(p => (p.PoolId, p.Amount.Cents)));
    }

    [Fact(DisplayName = "Mehrdeutiger impliziter Zweck (zwei positive Zellen) wird verworfen")]
    public async Task AmbiguousImplicitPurpose_IsRejected()
    {
        await using var db = new TestDatabase();
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.CreateAccountAsync(Step5Scenario.Haupt, "Hauptkonto", AccountType.Bank);
        await admin.CreateAccountAsync(Step5Scenario.Wohnung, "Wohnungskonto", AccountType.Bank);
        await admin.CreatePoolAsync(Step5Scenario.Flex, "Flex");
        await admin.CreatePoolAsync(Step5Scenario.PoolFreizeit, "Freizeit");

        var opening = db.Resolve<OpeningBalanceService>();
        await opening.CreateAsync(new OpeningBalanceSpec(
            "init-a", new DateOnly(2026, 1, 1), Step5Scenario.Haupt, new Money(100_000),
            [new PoolAllocation(Step5Scenario.Flex, new Money(60_000))]));
        await opening.CreateAsync(new OpeningBalanceSpec(
            "init-b", new DateOnly(2026, 1, 1), Step5Scenario.Haupt, new Money(40_000),
            [new PoolAllocation(Step5Scenario.PoolFreizeit, new Money(40_000))]));

        await Assert.ThrowsAsync<LarioDomainException>(
            () => db.Resolve<TransferService>().CreateAsync(
                Transfer(id: "tr-ambigu", cents: 10_000, purposes: null)));

        Assert.DoesNotContain(await db.Resolve<IBookingRepository>().GetAllAsync(),
            b => b.Type == BookingType.AccountTransfer);
    }

    [Fact(DisplayName = "Idempotenz: gleicher Transfer (explizit) wird nicht erneut gespeichert")]
    public async Task Repeat_Explicit_IsIdempotent()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        var service = db.Resolve<TransferService>();
        var spec = Transfer(purposes: [new PoolAllocation(Step5Scenario.Flex, new Money(80_000))]);

        var first = await service.CreateAsync(spec);
        var second = await service.CreateAsync(spec);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal("bk-2026-1204", second.Booking.Id);
        var transfers = (await db.Resolve<IBookingRepository>().GetAllAsync())
            .Where(b => b.Type == BookingType.AccountTransfer)
            .ToList();
        Assert.Single(transfers);
    }

    [Fact(DisplayName = "Idempotenz: implizite Wiederholung akzeptiert die gespeicherte Auflösung")]
    public async Task Repeat_Implicit_IsIdempotent()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        var service = db.Resolve<TransferService>();
        var spec = Transfer(id: "tr-implizit", cents: 10_000, purposes: null);

        var first = await service.CreateAsync(spec);
        var second = await service.CreateAsync(spec);

        Assert.True(first.Created);
        Assert.False(second.Created);
        var transfers = (await db.Resolve<IBookingRepository>().GetAllAsync())
            .Where(b => b.Type == BookingType.AccountTransfer)
            .ToList();
        Assert.Single(transfers);
    }

    [Fact(DisplayName = "Gleiche Id mit anderem Betrag ist ein Konflikt; ursprüngliche Buchung bleibt")]
    public async Task ContentConflict_KeepsExisting()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        var service = db.Resolve<TransferService>();

        await service.CreateAsync(
            Transfer(purposes: [new PoolAllocation(Step5Scenario.Flex, new Money(80_000))]));

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.CreateAsync(
                Transfer(cents: 90_000,
                    purposes: [new PoolAllocation(Step5Scenario.Flex, new Money(90_000))])));

        var loaded = await db.Resolve<IBookingRepository>().FindByIdAsync("bk-2026-1204");
        Assert.Equal(new Money(80_000),
            loaded!.AccountPostings.First(p => p.Amount.Cents > 0).Amount);
        Assert.Single(await db.Resolve<IBookingRepository>().GetAllAsync(),
            b => b.Type == BookingType.AccountTransfer);
    }

    [Fact(DisplayName = "Id eines Anfangsbestands ist für einen Transfer ein Konflikt")]
    public async Task IdUsedByOtherBookingType_IsConflict()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        await Assert.ThrowsAsync<DomainConflictException>(
            () => db.Resolve<TransferService>().CreateAsync(
                Transfer(id: "init-2026-01-01",
                    purposes: [new PoolAllocation(Step5Scenario.Flex, new Money(1_000))])));

        Assert.DoesNotContain(await db.Resolve<IBookingRepository>().GetAllAsync(),
            b => b.Type == BookingType.AccountTransfer);
    }

    [Fact(DisplayName = "Quellkontostand trägt den Betrag nicht → keine Buchung")]
    public async Task SourceBalanceTooLow_IsRejected()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        await Assert.ThrowsAsync<LarioDomainException>(
            () => db.Resolve<TransferService>().CreateAsync(
                Transfer(cents: 200_000,
                    purposes: [new PoolAllocation(Step5Scenario.Flex, new Money(200_000))])));

        Assert.DoesNotContain(await db.Resolve<IBookingRepository>().GetAllAsync(),
            b => b.Type == BookingType.AccountTransfer);
    }

    [Fact(DisplayName = "FK-Verletzung (Geisterpool in Zweckzeile) verwirft Buchung und Zweckzeilen komplett")]
    public async Task AtomicSave_NoPartialState_OnForeignKeyViolation()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        var repo = db.Resolve<IBookingRepository>();
        var invalid = new Booking(
            "tr-fk",
            BookingType.AccountTransfer,
            new DateOnly(2026, 11, 7),
            null,
            [
                new AccountPosting { AccountId = Step5Scenario.Haupt, Amount = new Money(-1_000) },
                new AccountPosting { AccountId = Step5Scenario.Wohnung, Amount = new Money(1_000) },
            ],
            [],
            transferPurposes: [new PoolAllocation("pool-geister", new Money(1_000))]);

        await Assert.ThrowsAsync<DbUpdateException>(() => repo.AddAsync(invalid));

        Assert.Null(await repo.FindByIdAsync("tr-fk"));
        Assert.DoesNotContain(await repo.GetAllAsync(), b => b.Type == BookingType.AccountTransfer);
        Assert.Empty(await db.Resolve<ISettlementPositionRepository>().GetAllAsync());
    }
}

/// <summary>
/// Begleichungsreferenz eines Transfers (Rechenvertrag §3.5) auf echter
/// SQLite: die Position muss offen sein, das Geld muss in das Konto der
/// Position fließen und die Zweckzuordnung muss ihren Pool enthalten.
/// </summary>
public class SettlementLinkTests
{
    /// <summary>
    /// Start: 1.000 €/300 € + 85-€-Einkauf → offene Position 20 €
    /// (Hauptkonto → pool-freizeit). Liefert die persistierte Position.
    /// </summary>
    private static async Task<SettlementQueryService.SettlementView> SeedWithDebtAsync(
        TestDatabase db)
    {
        await Step5Scenario.SeedAsync(db);
        await Step5Scenario.AddMixedPurchaseAsync(db);
        return Assert.Single(
            await db.Resolve<SettlementQueryService>().GetOpenAsync());
    }

    [Fact(DisplayName = "Richtiger Transfer (Zufluss ins Konto der Position, Zweck = Pool) wird angenommen")]
    public async Task ValidLink_IsAccepted()
    {
        await using var db = new TestDatabase();
        var position = await SeedWithDebtAsync(db);

        var result = await db.Resolve<TransferService>().CreateAsync(new TransferSpec
        {
            Id = "bk-2026-1203",
            Date = new DateOnly(2026, 11, 6),
            FromAccountId = Step5Scenario.Freizeit,
            ToAccountId = Step5Scenario.Haupt,
            Amount = new Money(2_000),
            Purposes = [new PoolAllocation(Step5Scenario.PoolFreizeit, new Money(2_000))],
            RelatedSettlementId = position.Id,
            Note = "Ausgleich 20 € (Rückfluss der Vorfinanzierung)",
        });

        Assert.True(result.Created);
        Assert.Empty(await db.Resolve<SettlementQueryService>().GetOpenAsync());
    }

    [Fact(DisplayName = "Geld aus dem Konto der Position heraus (falsche Richtung) wird verworfen")]
    public async Task WrongDirection_IsRejected()
    {
        await using var db = new TestDatabase();
        var position = await SeedWithDebtAsync(db);

        await Assert.ThrowsAsync<LarioDomainException>(
            () => db.Resolve<TransferService>().CreateAsync(new TransferSpec
            {
                Id = "tr-falsch-richtung",
                Date = new DateOnly(2026, 11, 6),
                // Geld geht aus dem Hauptkonto heraus — vertieft nur die
                // Schuld, kann die Position nicht begleichen.
                FromAccountId = Step5Scenario.Haupt,
                ToAccountId = Step5Scenario.Freizeit,
                Amount = new Money(2_000),
                Purposes = [new PoolAllocation(Step5Scenario.Flex, new Money(2_000))],
                RelatedSettlementId = position.Id,
            }));

        // Position bleibt offen, keine neue Buchung.
        var open = Assert.Single(await db.Resolve<SettlementQueryService>().GetOpenAsync());
        Assert.Equal(new Money(2_000), open.RestAmount);
        Assert.DoesNotContain(await db.Resolve<IBookingRepository>().GetAllAsync(),
            b => b.Type == BookingType.AccountTransfer);
    }

    [Fact(DisplayName = "Zweckzuordnung ohne den Pool der Position wird verworfen")]
    public async Task PurposeWithoutPositionPool_IsRejected()
    {
        await using var db = new TestDatabase();
        var position = await SeedWithDebtAsync(db);

        await Assert.ThrowsAsync<LarioDomainException>(
            () => db.Resolve<TransferService>().CreateAsync(new TransferSpec
            {
                Id = "tr-falscher-zweck",
                Date = new DateOnly(2026, 11, 6),
                FromAccountId = Step5Scenario.Freizeit,
                ToAccountId = Step5Scenario.Haupt,
                Amount = new Money(2_000),
                // Zielkonto stimmt, aber der Zweck trägt das Geld nicht
                // dem pool-freizeit zu — die Schuldzelle steigt nicht.
                Purposes = [new PoolAllocation(Step5Scenario.Flex, new Money(2_000))],
                RelatedSettlementId = position.Id,
            }));

        var open = Assert.Single(await db.Resolve<SettlementQueryService>().GetOpenAsync());
        Assert.Equal(new Money(2_000), open.RestAmount);
    }

    [Fact(DisplayName = "Unbekannte Ausgleichsposition wird verworfen")]
    public async Task UnknownPosition_IsRejected()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        await Assert.ThrowsAsync<InvalidReferenceException>(
            () => db.Resolve<TransferService>().CreateAsync(new TransferSpec
            {
                Id = "tr-geister-position",
                Date = new DateOnly(2026, 11, 6),
                FromAccountId = Step5Scenario.Freizeit,
                ToAccountId = Step5Scenario.Haupt,
                Amount = new Money(2_000),
                Purposes = [new PoolAllocation(Step5Scenario.PoolFreizeit, new Money(2_000))],
                RelatedSettlementId = "set-geister",
            }));

        Assert.DoesNotContain(await db.Resolve<IBookingRepository>().GetAllAsync(),
            b => b.Type == BookingType.AccountTransfer);
    }

    [Fact(DisplayName = "Bereits beglichene Position kann nicht erneut referenziert werden")]
    public async Task AlreadySettledPosition_IsConflict()
    {
        await using var db = new TestDatabase();
        var position = await SeedWithDebtAsync(db);
        var service = db.Resolve<TransferService>();

        await service.CreateAsync(new TransferSpec
        {
            Id = "tr-voll",
            Date = new DateOnly(2026, 11, 6),
            FromAccountId = Step5Scenario.Freizeit,
            ToAccountId = Step5Scenario.Haupt,
            Amount = new Money(2_000),
            Purposes = [new PoolAllocation(Step5Scenario.PoolFreizeit, new Money(2_000))],
            RelatedSettlementId = position.Id,
        });
        Assert.Empty(await db.Resolve<SettlementQueryService>().GetOpenAsync());

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.CreateAsync(new TransferSpec
            {
                Id = "tr-nochmal",
                Date = new DateOnly(2026, 11, 8),
                FromAccountId = Step5Scenario.Freizeit,
                ToAccountId = Step5Scenario.Haupt,
                Amount = new Money(1_000),
                Purposes = [new PoolAllocation(Step5Scenario.PoolFreizeit, new Money(1_000))],
                RelatedSettlementId = position.Id,
            }));
    }
}

/// <summary>
/// Regression: Jeder Buchungstyp, der Zellen der Zuordnungsmatrix
/// beeinflusst, muss SettlementPositions mitpflegen — sonst „vergißt"
/// die Tabelle eine Begleichung (Zeile bleibt beim Vollbetrag, Abfragen
/// brechen über die Integritätsconstraint).
/// </summary>
public sealed class SettlementLinkIncomeTests
{
    [Fact(DisplayName = "Einnahme auf negativer Zelle reduziert die offene Position (Teilbegleichung und Abschluss)")]
    public async Task Income_OnNegativeCell_ReducesAndClosesPosition()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await Step5Scenario.AddMixedPurchaseAsync(db);
        var open = Assert.Single(
            await db.Resolve<SettlementQueryService>().GetOpenAsync());
        Assert.Equal(new Money(2_000), open.RestAmount);

        // 10 € Einnahme aufs Hauptkonto, an pool-freizeit: die Zelle
        // (Haupt, pool-freizeit) geht von −20 € auf −10 €.
        await db.Resolve<IncomeService>().CreateAsync(new IncomeSpec
        {
            Id = "inc-2026-1101",
            Date = new DateOnly(2026, 11, 12),
            Amount = new Money(1_000),
            AccountId = Step5Scenario.Haupt,
            Note = "10 € Einnahme an den Freizeit-Pool",
            Allocations = [new IncomeAllocationSpec(Step5Scenario.PoolFreizeit, new Money(1_000))],
        });

        // Position offen mit 10 € Rest — und die Abfrage funktioniert
        // (keine Integritätsconstraint-Fehler).
        var rest = Assert.Single(await db.Resolve<SettlementQueryService>().GetOpenAsync());
        Assert.Equal(open.Id, rest.Id);
        Assert.Equal(new Money(1_000), rest.RestAmount);

        // Weitere 10 € auf dieselbe Zelle schließen die Position.
        await db.Resolve<IncomeService>().CreateAsync(new IncomeSpec
        {
            Id = "inc-2026-1102",
            Date = new DateOnly(2026, 11, 15),
            Amount = new Money(1_000),
            AccountId = Step5Scenario.Haupt,
            Note = "Abschluss der Ausgleichsposition",
            Allocations = [new IncomeAllocationSpec(Step5Scenario.PoolFreizeit, new Money(1_000))],
        });

        Assert.Empty(await db.Resolve<SettlementQueryService>().GetOpenAsync());
        var closed = Assert.Single(
            await db.Resolve<ISettlementPositionRepository>().GetAllAsync());
        Assert.Equal(SettlementStatus.Beglichen, closed.Status);
        Assert.Equal("inc-2026-1102", closed.SettledByBookingId);
        Assert.Equal(new DateOnly(2026, 11, 15), closed.SettledDate);
    }
}
