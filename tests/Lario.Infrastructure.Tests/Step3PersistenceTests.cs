using Lario.Application;
using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;
using Lario.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lario.Infrastructure.Tests;

/// <summary>
/// Test-Hilfsmittel: echte SQLite-Datei in einem temporären Verzeichnis,
/// Migrationen angewendet, DI-Container mit der Datenschicht verdrahtet.
/// Kein EF-InMemory: Transaktionen, Constraints und SQLite-Verhalten
/// müssen wie im Produkt geprüft werden (AGENTS.md „Prüfungen").
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    /// <summary>Temporäres Ablageverzeichnis (bewusst nicht <c>Directory</c>: würde <see cref="System.IO.Directory"/> verdecken).</summary>
    public string TempDir { get; }

    public string DatabaseFile { get; }

    /// <summary>Ablageverzeichnis für Beleg-Dateien (Schritt 7a).</summary>
    public string AttachmentsDir { get; }

    public ServiceProvider Services { get; }

    /// <summary>
    /// <paramref name="configureServices"/> ermöglicht Test-Dekoratoren
    /// (z. B. deterministische Race-Reproduktionen, Schritt 7a Review
    /// Befund 3): die Registrierung wird nach <c>AddLarioData</c>,
    /// vor dem Build angepasst. <c>connectionString</c> ist die bereits
    /// festgelegte <c>Data Source=…</c>-Zeile.
    /// </summary>
    public TestDatabase(
        string? migrateTo = null,
        Action<ServiceCollection, string>? configureServices = null)
    {
        TempDir = System.IO.Directory.CreateTempSubdirectory("lario-test-").FullName;
        DatabaseFile = Path.Combine(TempDir, "lario.sqlite");
        AttachmentsDir = Path.Combine(TempDir, "attachments");
        var connectionString = $"Data Source={DatabaseFile}";

        var builder = new ServiceCollection();
        builder.AddLarioData(connectionString, AttachmentsDir);
        configureServices?.Invoke(builder, connectionString);
        Services = builder.BuildServiceProvider();

        // Migrationen auf die neue Datei anwenden (wie beim Host-Start).
        // <c>migrateTo</c> hält bei einer bestimmten Migration an — für
        // Upgrade-Tests, die Zeilen vor einer späteren Migration anlegen.
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<LarioDbContext>().Database.Migrate(migrateTo);
    }

    public T Resolve<T>() where T : notnull
    {
        var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    /// <summary>Neuer Provider auf derselben Datei — simuliert einen Neustart.</summary>
    public ServiceProvider NewServiceProvider()
    {
        var builder = new ServiceCollection();
        builder.AddLarioData($"Data Source={DatabaseFile}", AttachmentsDir);
        return builder.BuildServiceProvider();
    }

    public async Task<IReadOnlyList<string>> TableNamesAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={DatabaseFile}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        try
        {
            System.IO.Directory.Delete(TempDir, recursive: true);
        }
        catch (IOException)
        {
            // SQLite schließt die Datei evtl. erst verzögert; aufräumen
            // passiert spätestens beim nächsten Testlauf.
        }
    }
}

/// <summary>
/// Erste Migration: erwartet Tabellen und Constraints (Schritt 3).
/// </summary>
public class MigrationTests
{
    [Fact(DisplayName = "Migrate legt alle Fachtabellen an")]
    public async Task Migrate_CreatesAllTables()
    {
        await using var db = new TestDatabase();
        var tables = (await db.TableNamesAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var expected = new[]
        {
            // Schritt 3:
            "Accounts", "Pools", "Categories", "TaxCategories",
            "TaxPresets", "Bookings", "AccountPostings", "PoolPostings",
            // Schritt 4:
            "ExpenseSplits", "SplitTaxMarkings",
            // Schritt 5:
            "TransferPurposes", "ReassignmentAccounts", "SettlementPositions",
            // Schritt 6:
            "ReversalDeltas", "BookingCorrections",
        };
        foreach (var table in expected)
        {
            Assert.Contains(tables, t => t.Equals(table, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact(DisplayName = "Migration ist idempotent (zweite Anwendung ändert nichts)")]
    public async Task Migrate_IsIdempotent()
    {
        await using var db = new TestDatabase();
        using (var scope = db.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<LarioDbContext>().Database.Migrate();
        }

        // EF-interne Tabellen (__EFMigrationsHistory, __EFMigrationsLock) ausklammern:
        // es müssen exakt sechzehn Fachtabellen existieren (acht aus
        // Schritt 3, dazu ExpenseSplits und SplitTaxMarkings aus Schritt 4,
        // dazu TransferPurposes, ReassignmentAccounts und SettlementPositions
        // aus Schritt 5, dazu ReversalDeltas und BookingCorrections aus
        // Schritt 6, dazu Attachments aus Schritt 7a).
        var tables = (await db.TableNamesAsync())
            .Where(t => !t.StartsWith("__", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(16, tables.Count);
    }
}

/// <summary>
/// Stammdaten auf echter SQLite: Roundtrip über neue Katalogladevorgänge
/// (jeder Aufruf liest aus der Datei, nicht aus dem Speicher).
/// </summary>
public class MasterDataPersistenceTests
{
    [Fact(DisplayName = "Konto/Pool/Kategorie/Steuerkategorie überleben Neuladen")]
    public async Task MasterData_Roundtrip()
    {
        await using var db = new TestDatabase();
        var admin = db.Resolve<MasterDataAdmin>();

        await admin.CreateAccountAsync("acc-1", "Hauptkonto", AccountType.Bank, true);
        await admin.CreatePoolAsync("pool-1", "Flex");
        await admin.CreateCategoryAsync("cat-1", "Wohnen");
        await admin.CreateCategoryAsync("cat-2", "Miete", parentId: "cat-1");
        await admin.CreateTaxCategoryAsync("tax-1", "Steuern");
        await admin.ArchivePoolAsync("pool-1");

        var catalog = await db.Resolve<IMasterDataRepository>().LoadCatalogAsync();

        var account = catalog.FindAccount("acc-1")!;
        Assert.Equal(AccountType.Bank, account.Type);
        Assert.True(account.IsInterestBearing);
        Assert.Equal(EntityStatus.Active, account.Status);

        Assert.Equal(EntityStatus.Archived, catalog.FindPool("pool-1")!.Status);
        Assert.Equal("cat-1", catalog.FindCategory("cat-2")!.ParentId);
        Assert.NotNull(catalog.FindTaxCategory("tax-1"));
    }

    [Fact(DisplayName = "Doppelte Anlage derselben Id ist ein Konflikt, Datenbank unverändert")]
    public async Task DuplicateId_IsConflictAndDatabaseUnchanged()
    {
        await using var db = new TestDatabase();
        var admin = db.Resolve<MasterDataAdmin>();

        await admin.CreateAccountAsync("acc-1", "Hauptkonto", AccountType.Bank);
        await Assert.ThrowsAsync<DomainConflictException>(
            () => admin.CreateAccountAsync("acc-1", "Doppel", AccountType.Savings));

        var catalog = await db.Resolve<IMasterDataRepository>().LoadCatalogAsync();
        Assert.Single(catalog.Accounts);
        Assert.Equal("Hauptkonto", catalog.Accounts.Single().Name);
    }

    [Fact(DisplayName = "Steuer-Voreinstellung 1:1: setzen, ersetzen, entfernen")]
    public async Task TaxPreset_Roundtrip()
    {
        await using var db = new TestDatabase();
        var admin = db.Resolve<MasterDataAdmin>();
        var repo = db.Resolve<IMasterDataRepository>();

        await admin.CreateCategoryAsync("cat-1", "Wohnen");
        await admin.CreateTaxCategoryAsync("tax-1", "Steuern");

        // Setzen.
        var preset = await admin.SetTaxPresetAsync("cat-1", true, "tax-1", inheritToChildren: true);
        Assert.Equal(1, preset.BaseRevision);

        var catalog = await repo.LoadCatalogAsync();
        var category = catalog.FindCategory("cat-1")!;
        Assert.NotNull(category.TaxPreset);
        Assert.True(category.TaxPreset!.TaxRelevant);
        Assert.Equal("tax-1", category.TaxPreset.TaxCategoryId);
        Assert.True(category.TaxPreset.InheritToChildren);

        // Ersetzen: Revision steigt.
        await admin.SetTaxPresetAsync("cat-1", false, null, inheritToChildren: false);
        catalog = await repo.LoadCatalogAsync();
        category = catalog.FindCategory("cat-1")!;
        Assert.False(category.TaxPreset!.TaxRelevant);
        Assert.Null(category.TaxPreset.TaxCategoryId);
        Assert.Equal(2, category.TaxPreset.BaseRevision);

        // Entfernen: Zeile ist weg, Kategorie bleibt.
        await admin.RemoveTaxPresetAsync("cat-1");
        catalog = await repo.LoadCatalogAsync();
        Assert.NotNull(catalog.FindCategory("cat-1"));
        Assert.Null(catalog.FindCategory("cat-1")!.TaxPreset);
        Assert.Null(catalog.FindPreset("cat-1"));
    }

    [Fact(DisplayName = "Kategoriezyklus (Selbstreferenz): abgelehnt, nichts persistiert")]
    public async Task CategoryCycle_RejectedAndNothingPersisted()
    {
        await using var db = new TestDatabase();
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.CreateCategoryAsync("cat-a", "Wohnen");

        await Assert.ThrowsAsync<CycleDetectedException>(
            () => admin.CreateCategoryAsync("cat-self", "Zyklus", parentId: "cat-self"));

        var catalog = await db.Resolve<IMasterDataRepository>().LoadCatalogAsync();
        Assert.Single(catalog.Categories);
        Assert.NotNull(catalog.FindCategory("cat-a"));
        Assert.Null(catalog.FindCategory("cat-self"));
    }

    [Fact(DisplayName = "Neustart: zweiter Provider findet exakt dieselben Daten")]
    public async Task Restart_FindsSameData()
    {
        await using var db = new TestDatabase();
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.CreateAccountAsync("acc-1", "Hauptkonto", AccountType.Bank);
        await admin.CreatePoolAsync("pool-1", "Flex");

        // Neuer Provider auf derselben Datei (simulierter Neustart).
        using var services = db.NewServiceProvider();
        using var scope = services.CreateScope();
        var catalog = await scope.ServiceProvider
            .GetRequiredService<IMasterDataRepository>()
            .LoadCatalogAsync();

        Assert.Single(catalog.Accounts);
        Assert.Single(catalog.Pools);
        Assert.Equal("Hauptkonto", catalog.Accounts.Single().Name);
    }
}

/// <summary>
/// Anfangsbestand auf echter SQLite (Schritt 3): atomare Speicherung,
/// Idempotenz, Konflikt bei geändertem Inhalt, Rechenvertrag-Referenzen.
/// </summary>
public class OpeningBalancePersistenceTests
{
    private static OpeningBalanceSpec Spec(
        string id = "init-2026-01-01",
        long totalCents = 100_000,
        (string pool, long cents)[]? distribution = null,
        string? note = null)
    {
        distribution ??=
        [
            ("pool-flex", 70_000),
            ("pool-freizeit", 30_000),
        ];
        return new OpeningBalanceSpec(
            id,
            DateOnly.FromDateTime(new DateTime(2026, 1, 1)),
            "acc-haupt",
            new Money(totalCents),
            distribution.Select(d => new PoolAllocation(d.pool, new Money(d.cents))).ToList(),
            note);
    }

    private static async Task SeedMasterDataAsync(TestDatabase db)
    {
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.CreateAccountAsync("acc-haupt", "Hauptkonto", AccountType.Bank);
        await admin.CreatePoolAsync("pool-flex", "Flex");
        await admin.CreatePoolAsync("pool-freizeit", "Freizeit");
    }

    [Fact(DisplayName = "Anlage speichert Buchung und Postings; Wiederholung ist idempotent")]
    public async Task Create_IsIdempotent()
    {
        await using var db = new TestDatabase();
        await SeedMasterDataAsync(db);
        var service = db.Resolve<OpeningBalanceService>();

        var first = await service.CreateAsync(Spec());
        Assert.True(first.Created);

        // Dieselbe Id, gleicher Inhalt (z. B. nach verlorener Antwort).
        var second = await service.CreateAsync(Spec());
        Assert.False(second.Created);
        Assert.Equal(first.Booking.Id, second.Booking.Id);
        Assert.Equal(first.Booking.BaseRevision, second.Booking.BaseRevision);

        // Exakt eine Buchung in der Datenbank.
        var all = await db.Resolve<IBookingRepository>().GetAllAsync();
        Assert.Single(all);
        Assert.Single(all[0].AccountPostings);
        Assert.Equal(2, all[0].PoolPostings.Count);
    }

    [Fact(DisplayName = "Gleiche Id mit anderem Inhalt ist ein Konflikt; alte Buchung bleibt")]
    public async Task ContentConflict_KeepsExistingBooking()
    {
        await using var db = new TestDatabase();
        await SeedMasterDataAsync(db);
        var service = db.Resolve<OpeningBalanceService>();

        await service.CreateAsync(Spec(note: "Original"));

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.CreateAsync(Spec(note: "Geändert")));
        Assert.Contains("Konflikt", conflict.Message);

        var existing = await db.Resolve<IBookingRepository>().FindByIdAsync("init-2026-01-01");
        Assert.NotNull(existing);
        Assert.Equal("Original", existing!.Note);
    }

    [Fact(DisplayName = "Review-Szenario: Wiederholung nach Archivierung von Konto und Pool wird idempotent bestätigt")]
    public async Task IdempotentRepeat_AfterMasterDataArchived_IsConfirmed()
    {
        await using var db = new TestDatabase();
        await SeedMasterDataAsync(db);
        var service = db.Resolve<OpeningBalanceService>();

        var result = await service.CreateAsync(Spec());
        Assert.True(result.Created);

        // Stammdaten werden archiviert (z. B. Jahresabschluss der Konten).
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.ArchiveAccountAsync("acc-haupt");
        await admin.ArchivePoolAsync("pool-flex");
        await admin.ArchivePoolAsync("pool-freizeit");

        // Wiederholung derselben Vorgabe (z. B. nach verlorener Antwort):
        // Die Idempotenzprüfung greift VOR der Aktivitätsprüfung — die
        // vorhandene Buchung wird unverändert bestätigt, es entsteht keine
        // zweite Buchung und kein Fehler wegen archivierter Referenzen.
        var repeat = await service.CreateAsync(Spec());
        Assert.False(repeat.Created);
        Assert.Equal("init-2026-01-01", repeat.Booking.Id);
        Assert.Single(await db.Resolve<IBookingRepository>().GetAllAsync());
    }

    [Fact(DisplayName = "Ohne stabile Id entsteht keine Buchung (keine stille Duplizierung)")]
    public async Task EmptyId_IsRejected_WithoutBooking()
    {
        await using var db = new TestDatabase();
        await SeedMasterDataAsync(db);
        var service = db.Resolve<OpeningBalanceService>();

        await Assert.ThrowsAsync<LarioDomainException>(
            () => service.CreateAsync(new OpeningBalanceSpec(
                "",
                DateOnly.FromDateTime(new DateTime(2026, 1, 1)),
                "acc-haupt",
                new Money(100_000),
                [
                    new PoolAllocation("pool-flex", new Money(70_000)),
                    new PoolAllocation("pool-freizeit", new Money(30_000)),
                ])));

        Assert.Empty(await db.Resolve<IBookingRepository>().GetAllAsync());
    }

    [Fact(DisplayName = "Referenzfall 1 (Fixture 01-anfangsbestand.json): 100.000 Flex + 30.000 Freizeit, Summe 130.000")]
    public async Task ReferenceNumbers_ReconstructBalances()
    {
        await using var db = new TestDatabase();
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.CreateAccountAsync("acc-haupt", "Hauptkonto", AccountType.Bank);
        await admin.CreateAccountAsync("acc-freizeit", "Freizeitkonto", AccountType.Bank);
        await admin.CreatePoolAsync("pool-flex", "Flex");
        await admin.CreatePoolAsync("pool-freizeit", "Freizeit");

        // Exakte Buchungen aus docs/step-0/referenzfaelle/01-anfangsbestand.json:
        // init-2026-01-01: acc-haupt 100.000 € -> pool-flex 100.000 €
        // init-2026-01-01-freizeit: acc-freizeit 30.000 € -> pool-freizeit 30.000 €
        var service = db.Resolve<OpeningBalanceService>();
        await service.CreateAsync(new OpeningBalanceSpec(
            "init-2026-01-01",
            DateOnly.FromDateTime(new DateTime(2026, 1, 1)),
            "acc-haupt",
            new Money(100_000),
            [new PoolAllocation("pool-flex", new Money(100_000))]));
        await service.CreateAsync(new OpeningBalanceSpec(
            "init-2026-01-01-freizeit",
            DateOnly.FromDateTime(new DateTime(2026, 1, 1)),
            "acc-freizeit",
            new Money(30_000),
            [new PoolAllocation("pool-freizeit", new Money(30_000))]));

        var balances = await db.Resolve<BalanceQueryService>().GetBalancesAsync();

        // Erwarteter Endzustand laut Fixture:
        Assert.Equal(100_000, balances.Accounts.First(a => a.AccountId == "acc-haupt").Balance.Cents);
        Assert.Equal(30_000, balances.Accounts.First(a => a.AccountId == "acc-freizeit").Balance.Cents);
        Assert.Equal(100_000, balances.Pools.First(p => p.PoolId == "pool-flex").Balance.Cents);
        Assert.Equal(30_000, balances.Pools.First(p => p.PoolId == "pool-freizeit").Balance.Cents);
        Assert.Equal(130_000, balances.TotalAccountBalance.Cents);
        Assert.Equal(130_000, balances.TotalPoolBalance.Cents);
        Assert.Equal(0, balances.AccountPoolGap.Cents);
        // Anfangsbestände sind keine Einnahmen; keine Restposten in "noch nicht zugeordnet".
        Assert.Equal(0, balances.TotalIncome.Cents);
        Assert.Empty(balances.UnallocatedOpeningBalances);
    }

    [Fact(DisplayName = "Ungültige Referenzen: unbekanntes Konto und unbekannter Pool bleiben ohne Buchung")]
    public async Task InvalidReferences_LeaveNoBooking()
    {
        await using var db = new TestDatabase();
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.CreateAccountAsync("acc-haupt", "Hauptkonto", AccountType.Bank);
        await admin.CreatePoolAsync("pool-flex", "Flex");

        var service = db.Resolve<OpeningBalanceService>();
        var baseDate = DateOnly.FromDateTime(new DateTime(2026, 1, 1));

        await Assert.ThrowsAsync<InvalidReferenceException>(
            () => service.CreateAsync(new OpeningBalanceSpec(
                "init-ghost-account", baseDate, "acc-ghost",
                new Money(1_000),
                [new PoolAllocation("pool-flex", new Money(1_000))])));

        await Assert.ThrowsAsync<InvalidReferenceException>(
            () => service.CreateAsync(new OpeningBalanceSpec(
                "init-ghost-pool", baseDate, "acc-haupt",
                new Money(1_000),
                [new PoolAllocation("pool-ghost", new Money(1_000))])));

        // Keine Teil- oder Ghost-Buchung in der Datenbank.
        Assert.Empty(await db.Resolve<IBookingRepository>().GetAllAsync());
    }

    [Fact(DisplayName = "Teilweise Zuordnung: Rest 30.000 bleibt sichtbar, Konten-Pool-Lücke erklärt ihn")]
    public async Task PartialAllocation_LeavesVisibleRemainder()
    {
        await using var db = new TestDatabase();
        await SeedMasterDataAsync(db);
        var service = db.Resolve<OpeningBalanceService>();

        await service.CreateAsync(new OpeningBalanceSpec(
            "init-teil",
            DateOnly.FromDateTime(new DateTime(2026, 1, 1)),
            "acc-haupt",
            new Money(100_000),
            [new PoolAllocation("pool-flex", new Money(70_000))]));

        var balances = await db.Resolve<BalanceQueryService>().GetBalancesAsync();

        var unallocated = Assert.Single(balances.UnallocatedOpeningBalances);
        Assert.Equal("init-teil", unallocated.OpeningBalanceId);
        Assert.Equal(30_000, unallocated.Unallocated.Cents);
        Assert.Equal(30_000, balances.AccountPoolGap.Cents);
    }

    [Fact(DisplayName = "Atomare Speicherung: FK-Verletzung verwirft Buchung und Postings komplett")]
    public async Task AtomicSave_NoPartialState_OnConstraintViolation()
    {
        await using var db = new TestDatabase();
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.CreateAccountAsync("acc-haupt", "Hauptkonto", AccountType.Bank);
        await admin.CreatePoolAsync("pool-flex", "Flex");

        var repo = db.Resolve<IBookingRepository>();

        // Buchung auf ein nicht existierendes Konto: FK-Constraint bricht
        // die Transaktion ab.
        var invalid = new Booking(
            "b-invalid",
            BookingType.OpeningBalance,
            DateOnly.FromDateTime(new DateTime(2026, 1, 1)),
            null,
            [new AccountPosting { AccountId = "acc-geister", Amount = new Money(100) }],
            [new PoolPosting { PoolId = "pool-flex", Amount = new Money(100) }],
            openingBalanceAccountId: "acc-geister");

        await Assert.ThrowsAsync<DbUpdateException>(() => repo.AddAsync(invalid));

        // Weder Buchung noch Pool-Posting durften bleiben.
        var all = await repo.GetAllAsync();
        Assert.Empty(all);
        Assert.Null(await repo.FindByIdAsync("b-invalid"));
    }

    [Fact(DisplayName = "Plan-Schritt 3 Prüfen: 1.000 € = 700 € Flex + 300 € Freizeit, centgenau persistiert")]
    public async Task PlanReferenceCase_700_300_PersistsExactly()
    {
        await using var db = new TestDatabase();
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.CreateAccountAsync("acc-haupt", "Hauptkonto", AccountType.Bank);
        await admin.CreatePoolAsync("pool-flex", "Flex");
        await admin.CreatePoolAsync("pool-freizeit", "Freizeit");

        var service = db.Resolve<OpeningBalanceService>();
        await service.CreateAsync(new OpeningBalanceSpec(
            "init-plan",
            DateOnly.FromDateTime(new DateTime(2026, 1, 1)),
            "acc-haupt",
            new Money(100_000),
            [
                new PoolAllocation("pool-flex", new Money(70_000)),
                new PoolAllocation("pool-freizeit", new Money(30_000)),
            ]));

        var balances = await db.Resolve<BalanceQueryService>().GetBalancesAsync();

        // Kontosumme und zugeordnete Summe je 1.000 €; normale Einnahmen 0 €.
        Assert.Equal(100_000, balances.Accounts.Single().Balance.Cents);
        Assert.Equal(70_000, balances.Pools.First(p => p.PoolId == "pool-flex").Balance.Cents);
        Assert.Equal(30_000, balances.Pools.First(p => p.PoolId == "pool-freizeit").Balance.Cents);
        Assert.Equal(100_000, balances.TotalPoolBalance.Cents);
        Assert.Equal(0, balances.AccountPoolGap.Cents);
        Assert.Equal(0, balances.TotalIncome.Cents);
        Assert.Empty(balances.UnallocatedOpeningBalances);
    }
}
