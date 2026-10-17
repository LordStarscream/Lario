using Lario.Application;
using Lario.Application.Journal;
using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Lario.Infrastructure.Tests;

/// <summary>
/// Zwischenstep 7.5 auf echter SQLite (Abnahmekriterien A2/A3):
/// Migration (Spalte + partieller eindeutiger Index), Einmaligkeit
/// pro Konto (Anwendungs- und Datenbankebene), Korrekturweg
/// (In-place, Revision, Idempotenz, Konto-Wechsel mit
/// Einmaligkeitsregel) und Stabilität über einen Neustart.
/// Referenzzahlen: 100 € Haupt (60 € → Flex), 30 € Freizeit.
/// </summary>
public class Step7_5OpeningBalanceTests
{
    private const string Haupt = "acc-haupt";
    private const string Freizeit = "acc-freizeit";
    private const string Flex = "pool-flex";
    private const string PoolFreizeit = "pool-freizeit";
    private static readonly DateOnly Stichtag = new(2026, 1, 1);

    // ==================================================================
    // Migration
    // ==================================================================

    [Fact(DisplayName = "A2: Migration trägt die OpeningBalanceAccountId-Spalte und den partiellen eindeutigen Index")]
    public async Task Migration_AddsColumnAndPartialUniqueIndex()
    {
        await using var db = new TestDatabase();

        Assert.Contains("OpeningBalanceAccountId", await ColumnNamesAsync(db, "Bookings"));

        await using var connection =
            new SqliteConnection($"Data Source={db.DatabaseFile}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA index_list('Bookings');";
        await using var reader = await command.ExecuteReaderAsync();

        var indexName = "UX_Bookings_OpeningBalanceAccountId";
        bool found = false;
        string? partialFilter = null;
        while (await reader.ReadAsync())
        {
            // Spalten: seq, name, unique, origin, partial.
            if (reader.GetString(1) == indexName)
            {
                found = true;
                Assert.Equal(1, reader.GetInt32(2)); // unique
                Assert.Equal(1, reader.GetInt32(4)); // partial
            }
        }

        Assert.True(found, $"Index {indexName} fehlt in Bookings");
        reader.Dispose();

        // Filter prüft, dass NULL-Werte (alle anderen Buchungstypen)
        // nicht Teil der Eindeutigkeit sind.
        await using var sql = connection.CreateCommand();
        sql.CommandText = "SELECT sql FROM sqlite_master WHERE name = @name;";
        sql.Parameters.AddWithValue("@name", indexName);
        partialFilter = (string?)await sql.ExecuteScalarAsync();

        Assert.NotNull(partialFilter);
        Assert.Contains("IS NOT NULL", partialFilter!);
    }

    // ==================================================================
    // Einmaligkeit (A2)
    // ==================================================================

    [Fact(DisplayName = "A2: Zweiter Anfangsbestand desselben Kontos wird fachlich abgelehnt (409-Äquivalent)")]
    public async Task SecondOpeningBalanceSameAccount_ServiceRejects()
    {
        await using var db = new TestDatabase();
        await SeedAsync(db);

        await db.Resolve<OpeningBalanceService>().CreateAsync(
            new OpeningBalanceSpec("init-1", Stichtag, Haupt, new Money(100_000),
                [new PoolAllocation(Flex, new Money(60_000))], "Erster"));

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => db.Resolve<OpeningBalanceService>().CreateAsync(
                new OpeningBalanceSpec("init-2", Stichtag, Haupt, new Money(40_000),
                    [new PoolAllocation(Flex, new Money(40_000))])));

        Assert.Contains("bereits einen Anfangsbestand", conflict.Message);
        Assert.Contains("Korrektur", conflict.Message);

        // Das Journal enthält weiterhin genau einen Anfangsbestand.
        var bookings = await db.Resolve<IBookingRepository>().GetAllAsync();
        var openings = bookings
            .Where(b => b.Type == BookingType.OpeningBalance).ToList();
        var only = Assert.Single(openings);
        Assert.Equal("init-1", only.Id);
        Assert.Equal(Haupt, only.OpeningBalanceAccountId);
    }

    [Fact(DisplayName = "A2: Eindeutigkeit wird datenbankseitig erzwungen (Umgehung der Anwendungsprüfung)")]
    public async Task SecondOpeningBalanceSameAccount_DatabaseRejects()
    {
        await using var db = new TestDatabase();
        await SeedAsync(db);

        var repo = db.Resolve<IBookingRepository>();
        await repo.AddAsync(new Booking(
            "init-db-1", BookingType.OpeningBalance, Stichtag, null,
            [new AccountPosting { AccountId = Haupt, Amount = new Money(100) }],
            [],
            openingBalanceAccountId: Haupt));

        // Gleicher Konto-Wert ohne Anwendungsprüfung: der partielle
        // eindeutige Index muss die zweite Zeile ablehnen.
        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(
            () => repo.AddAsync(new Booking(
                "init-db-2", BookingType.OpeningBalance, Stichtag, null,
                [new AccountPosting { AccountId = Haupt, Amount = new Money(10) }],
                [],
                openingBalanceAccountId: Haupt)));

        // Neue Kontexte wie neue Requests: das abgelehnte init-db-2 wäre
        // im selben DbContext noch als Added getrackt und würde beim
        // nächsten SaveChanges erneut (und wieder abgelehnt) eingebracht.
        var repo2 = db.Resolve<IBookingRepository>();
        await repo2.AddAsync(new Booking(
            "init-db-3", BookingType.OpeningBalance, Stichtag, null,
            [new AccountPosting { AccountId = Freizeit, Amount = new Money(30) }],
            [],
            openingBalanceAccountId: Freizeit));

        // Nicht-Anfangsbestände mit NULL tragen die Regel nicht aus.
        await repo2.AddAsync(new Booking(
            "bk-exp-1", BookingType.Expense, Stichtag, null,
            [new AccountPosting { AccountId = Haupt, Amount = new Money(-50) }],
            [new PoolPosting { PoolId = Flex, Amount = new Money(-50) }],
            splits:
            [
                new ExpenseSplit { Id = "s1", Amount = new Money(50), PoolId = Flex, CategoryId = "cat-1" }
            ]));
    }

    // ==================================================================
    // Korrekturweg (A3)
    // ==================================================================

    [Fact(DisplayName = "A3: Korrektur ist In-place (gleiche Id) mit Basisrevision++ und Historieneintrag")]
    public async Task Correction_IsInPlaceWithRevisionAndHistory()
    {
        await using var db = new TestDatabase();
        await SeedAsync(db);
        await db.Resolve<OpeningBalanceService>().CreateAsync(
            new OpeningBalanceSpec("init-1", Stichtag, Haupt, new Money(100_000),
                [new PoolAllocation(Flex, new Money(60_000))], "ursprünglich"));

        var result = await db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
            "init-1", "op-1", 1,
            TotalAmount: new Money(120_000),
            Note: "korrigiert",
            Allocations:
            [
                new IncomeAllocationSpec(Flex, new Money(70_000)),
                new IncomeAllocationSpec(PoolFreizeit, new Money(50_000))
            ]));

        Assert.True(result.Created);
        Assert.Equal("init-1", result.Booking.Id);
        Assert.Equal(2, result.Booking.BaseRevision);
        Assert.Equal(120_000, result.Booking.AccountPostings[0].Amount.Cents);

        var bookings = await db.Resolve<IBookingRepository>().GetAllAsync();
        // Keine zweite Buchung: genau ein Anfangsbestand, Revision 2.
        var openings = bookings
            .Where(b => b.Type == BookingType.OpeningBalance).ToList();
        var only = Assert.Single(openings);
        Assert.Equal("init-1", only.Id);
        Assert.Equal(2, only.BaseRevision);
        Assert.Equal("korrigiert", only.Note);
        Assert.Equal(
            [(Flex, 70_000L), (PoolFreizeit, 50_000L)],
            only.PoolPostings.OrderBy(p => p.PoolId).Select(p => (p.PoolId, p.Amount.Cents)));

        // Historieneintrag mit der Operation-Id.
        var history = await db.Resolve<IBookingRepository>()
            .FindCorrectionAsync("op-1");
        Assert.NotNull(history);
        Assert.Equal("init-1", history!.BookingId);
    }

    [Fact(DisplayName = "A3: Wiederholte Korrektur (gleiche Operation-Id und Inhalt) wird idempotent quittiert")]
    public async Task Correction_RepeatSameOperationId_IsIdempotent()
    {
        await using var db = new TestDatabase();
        await SeedAsync(db);
        await db.Resolve<OpeningBalanceService>().CreateAsync(
            new OpeningBalanceSpec("init-1", Stichtag, Haupt, new Money(100_000),
                [new PoolAllocation(Flex, new Money(100_000))], null));

        CorrectionSpec spec() => new(
            "init-1", "op-repeat", 1,
            TotalAmount: new Money(90_000),
            Allocations: [new IncomeAllocationSpec(Flex, new Money(90_000))]);

        var first = await db.Resolve<CorrectionService>().CorrectAsync(spec());
        Assert.True(first.Created);

        var second = await db.Resolve<CorrectionService>().CorrectAsync(spec());
        Assert.False(second.Created);
        Assert.Equal(90_000, second.Booking.AccountPostings[0].Amount.Cents);

        var bookings = await db.Resolve<IBookingRepository>().GetAllAsync();
        var only = Assert.Single(bookings, b => b.Type == BookingType.OpeningBalance);
        Assert.Equal(2, only.BaseRevision); // keine zweite Erhöhung
    }

    [Fact(DisplayName = "A3: Gleiche Operation-Id mit anderem Inhalt ist ein Konflikt")]
    public async Task Correction_SameOperationIdDifferentPayload_Conflicts()
    {
        await using var db = new TestDatabase();
        await SeedAsync(db);
        await db.Resolve<OpeningBalanceService>().CreateAsync(
            new OpeningBalanceSpec("init-1", Stichtag, Haupt, new Money(100_000),
                [new PoolAllocation(Flex, new Money(100_000))], null));

        var first = await db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
            "init-1", "op-diff", 1,
            TotalAmount: new Money(90_000),
            Allocations: [new IncomeAllocationSpec(Flex, new Money(90_000))]));
        Assert.True(first.Created);

        await Assert.ThrowsAsync<DomainConflictException>(
            () => db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
                "init-1", "op-diff", 1,
                TotalAmount: new Money(80_000),
                Allocations: [new IncomeAllocationSpec(Flex, new Money(80_000))])));

        var bookings = await db.Resolve<IBookingRepository>().GetAllAsync();
        var only = Assert.Single(bookings, b => b.Type == BookingType.OpeningBalance);
        Assert.Equal(90_000, only.AccountPostings[0].Amount.Cents);
        Assert.Equal(2, only.BaseRevision);
    }

    [Fact(DisplayName = "A3: Falsche Basisrevision wird als Revisionskonflikt abgelehnt")]
    public async Task Correction_WrongBaseRevision_Conflicts()
    {
        await using var db = new TestDatabase();
        await SeedAsync(db);
        await db.Resolve<OpeningBalanceService>().CreateAsync(
            new OpeningBalanceSpec("init-1", Stichtag, Haupt, new Money(100_000),
                [new PoolAllocation(Flex, new Money(100_000))], null));

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
                "init-1", "op-rev", 99, TotalAmount: new Money(90_000))));

        Assert.Equal(99, conflict.ExpectedBaseRevision);
        Assert.Equal(1, conflict.ActualBaseRevision);

        var bookings = await db.Resolve<IBookingRepository>().GetAllAsync();
        var only = Assert.Single(bookings, b => b.Type == BookingType.OpeningBalance);
        Assert.Equal(100_000, only.AccountPostings[0].Amount.Cents);
    }

    [Fact(DisplayName = "A3: Konto-Wechsel bewegt die Einmaligkeitszuordnung; altes Konto wird frei")]
    public async Task Correction_AccountMove_ReleasesOldAccount()
    {
        await using var db = new TestDatabase();
        await SeedAsync(db);
        await db.Resolve<OpeningBalanceService>().CreateAsync(
            new OpeningBalanceSpec("init-1", Stichtag, Haupt, new Money(100_000),
                [new PoolAllocation(Flex, new Money(100_000))], null));

        var result = await db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
            "init-1", "op-move", 1, AccountId: Freizeit));
        Assert.True(result.Created);
        Assert.Equal(Freizeit, result.Booking.OpeningBalanceAccountId);

        // Haupt ist frei: ein neuer Anfangsbestand ist zulässig.
        await db.Resolve<OpeningBalanceService>().CreateAsync(
            new OpeningBalanceSpec("init-2", Stichtag, Haupt, new Money(50_000),
                [new PoolAllocation(Flex, new Money(50_000))], null));

        var bookings = await db.Resolve<IBookingRepository>().GetAllAsync();
        var openings = bookings
            .Where(b => b.Type == BookingType.OpeningBalance)
            .OrderBy(b => b.Id)
            .ToList();
        Assert.Equal(2, openings.Count);
        Assert.Equal(Haupt, openings.First(b => b.Id == "init-2").OpeningBalanceAccountId);
        Assert.Equal(Freizeit, openings.First(b => b.Id == "init-1").OpeningBalanceAccountId);
    }

    [Fact(DisplayName = "A2/A3: Konto-Wechsel auf bereits belegtes Konto wird abgelehnt (Buchung bleibt unverändert)")]
    public async Task Correction_MoveToOccupiedAccount_Conflicts()
    {
        await using var db = new TestDatabase();
        await SeedAsync(db);
        await db.Resolve<OpeningBalanceService>().CreateAsync(
            new OpeningBalanceSpec("init-1", Stichtag, Haupt, new Money(100_000),
                [new PoolAllocation(Flex, new Money(100_000))], null));
        await db.Resolve<OpeningBalanceService>().CreateAsync(
            new OpeningBalanceSpec("init-2", Stichtag, Freizeit, new Money(30_000),
                [new PoolAllocation(PoolFreizeit, new Money(30_000))], null));

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
                "init-1", "op-occupied", 1, AccountId: Freizeit)));

        Assert.Contains("init-2", conflict.Message);

        var bookings = await db.Resolve<IBookingRepository>().GetAllAsync();
        var init1 = Assert.Single(bookings, b => b.Id == "init-1");
        Assert.Equal(Haupt, init1.OpeningBalanceAccountId);
        Assert.Equal(1, init1.BaseRevision);
    }

    // ==================================================================
    // Neustart
    // ==================================================================

    [Fact(DisplayName = "A3: Anfangsbestand und Korrektur-Historie überleben einen Neustart")]
    public async Task OpeningBalance_SurvivesRestart()
    {
        await using var db = new TestDatabase();
        await SeedAsync(db);
        await db.Resolve<OpeningBalanceService>().CreateAsync(
            new OpeningBalanceSpec("init-1", Stichtag, Haupt, new Money(100_000),
                [new PoolAllocation(Flex, new Money(60_000))], "vor Neustart"));
        await db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
            "init-1", "op-restart", 1, TotalAmount: new Money(95_000)));
        await db.DisposeAsync();

        using var fresh = db.NewServiceProvider();
        using var scope = fresh.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
        var bookings = await repo.GetAllAsync();

        var only = Assert.Single(bookings, b => b.Type == BookingType.OpeningBalance);
        Assert.Equal("init-1", only.Id);
        Assert.Equal(95_000, only.AccountPostings[0].Amount.Cents);
        Assert.Equal(Haupt, only.OpeningBalanceAccountId);
        Assert.Equal(2, only.BaseRevision);

        var history = await repo.FindCorrectionAsync("op-restart");
        Assert.NotNull(history);

        // Die Einmaligkeitsregel gilt auch nach dem Neustart.
        await Assert.ThrowsAsync<DomainConflictException>(
            () => scope.ServiceProvider
                .GetRequiredService<OpeningBalanceService>()
                .CreateAsync(new OpeningBalanceSpec(
                    "init-2", Stichtag, Haupt, new Money(1),
                    [new PoolAllocation(Flex, new Money(1))])));
    }

    // ---------- Hilfsmittel ----------

    private static async Task SeedAsync(TestDatabase db)
    {
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.CreateAccountAsync(Haupt, "Hauptkonto", AccountType.Bank);
        await admin.CreateAccountAsync(Freizeit, "Freizeitkonto", AccountType.Bank);
        await admin.CreatePoolAsync(Flex, "Flex");
        await admin.CreatePoolAsync(PoolFreizeit, "Freizeit");
        await admin.CreateCategoryAsync("cat-1", "Lebensmittel");
    }

    private static async Task<IReadOnlyList<string>> ColumnNamesAsync(
        TestDatabase db, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={db.DatabaseFile}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}');";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(1));
        }
        return names;
    }
}
