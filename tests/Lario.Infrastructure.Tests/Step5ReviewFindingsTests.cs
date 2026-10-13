using Lario.Application;
using Lario.Application.Journal;
using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lario.Infrastructure.Tests;

/// <summary>
/// Schritt-5-Review P1 auf echter SQLite: persistierte Ausgleichszeilen
/// behalten ihre Identität (Id, CreatedByBookingId, CreatedDate, Notiz)
/// über gleichdatierte und rückdatierte Buchungen — die Zeilen werden
/// nicht durch eine Neuableitung verdrängt. Vor dem Fix erzeugte jede
/// neue Buchung dieselbe (Konto, Pool)-Zelle erneut und verlor die
/// bestehende offene Zeile (Id und Begründungsbuchung wechselten).
/// </summary>
public class SettlementIdentityTests
{
    private static async Task<string> OpenRowIdAsync(TestDatabase db) =>
        (await db.Resolve<SettlementQueryService>().GetOpenAsync()).Single().Id;

    [Fact(DisplayName = "Gleichdatierte Einnahme (Teilbegleichung): Zeilenid und Begründung bleiben stabil")]
    public async Task SameDayIncome_PartialSettlement_KeepsRowId()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await Step5Scenario.AddMixedPurchaseAsync(db);
        var rowId = await OpenRowIdAsync(db);

        // Einnahme am selben Tag, Buchungsid sortiert nach der Ausgabe.
        await db.Resolve<IncomeService>().CreateAsync(new IncomeSpec
        {
            Id = "bk-2026-1210",
            Date = new DateOnly(2026, 11, 5),
            Amount = new Money(1_000),
            AccountId = Step5Scenario.Haupt,
            Note = "Teilbegleichung am selben Tag",
            Allocations = [new IncomeAllocationSpec(Step5Scenario.PoolFreizeit, new Money(1_000))],
        });

        var open = (await db.Resolve<SettlementQueryService>().GetOpenAsync()).Single();
        Assert.Equal(rowId, open.Id);
        Assert.Equal("bk-2026-1202", open.CreatedByBookingId);
        Assert.Equal(2_000, open.Amount.Cents);
        Assert.Equal(1_000, open.RestAmount.Cents);
    }

    [Fact(DisplayName = "Gleichdatierte Einnahme (Abschluss): Zeile wird beglichen, Id bleibt stabil")]
    public async Task SameDayIncome_FullSettlement_KeepsRowId()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await Step5Scenario.AddMixedPurchaseAsync(db);
        var rowId = await OpenRowIdAsync(db);

        await db.Resolve<IncomeService>().CreateAsync(new IncomeSpec
        {
            Id = "bk-2026-1210",
            Date = new DateOnly(2026, 11, 5),
            Amount = new Money(2_000),
            AccountId = Step5Scenario.Haupt,
            Note = "Abschluss am selben Tag",
            Allocations = [new IncomeAllocationSpec(Step5Scenario.PoolFreizeit, new Money(2_000))],
        });

        Assert.Empty(await db.Resolve<SettlementQueryService>().GetOpenAsync());
        var row = (await db.Resolve<SettlementQueryService>().GetAllAsync()).Single();
        Assert.Equal(rowId, row.Id);
        Assert.Equal(SettlementStatus.Beglichen, row.Status);
        Assert.Equal("bk-2026-1210", row.SettledByBookingId);
        Assert.Equal(0, row.RestAmount.Cents);
    }

    [Fact(DisplayName = "Rückdatierte Einnahme (Teilbegleichung): Zeilenid und Begründung bleiben stabil")]
    public async Task BackdatedIncome_PartialSettlement_KeepsRowId()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await Step5Scenario.AddMixedPurchaseAsync(db);
        var rowId = await OpenRowIdAsync(db);

        // Einnahme DREI TAGE VOR der Ausgabe — die Zelle läuft +10 € → −10 €.
        // Die Schuld wächst danach nur auf 10 € (die Einnahme mindert die
        // spätere Vorfinanzierung); der Begründer bleibt die Ausgabe.
        await db.Resolve<IncomeService>().CreateAsync(new IncomeSpec
        {
            Id = "bk-backdated-inc",
            Date = new DateOnly(2026, 11, 2),
            Amount = new Money(1_000),
            AccountId = Step5Scenario.Haupt,
            Note = "Rückdatierte Teilbegleichung",
            Allocations = [new IncomeAllocationSpec(Step5Scenario.PoolFreizeit, new Money(1_000))],
        });

        var open = (await db.Resolve<SettlementQueryService>().GetOpenAsync()).Single();
        Assert.Equal(rowId, open.Id);
        Assert.Equal("bk-2026-1202", open.CreatedByBookingId);
        Assert.Equal(1_000, open.Amount.Cents);
        Assert.Equal(1_000, open.RestAmount.Cents);
    }

    [Fact(DisplayName = "Rückdatierte Ausgabe vertieft die Episode: Zeilenid bleibt stabil, Betrag wächst")]
    public async Task BackdatedExpense_DeepensEpisode_KeepsRowId()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await Step5Scenario.AddMixedPurchaseAsync(db);
        var rowId = await OpenRowIdAsync(db);

        // Weitere Ausgabe VIER TAGE VOR der ersten (Zelle −15 € → −35 €).
        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = "bk-backdated-exp",
            Date = new DateOnly(2026, 11, 1),
            AccountId = Step5Scenario.Haupt,
            TotalAmount = new Money(1_500),
            Note = "Rückdatierte Ausgabe",
            Splits =
            [
                new ExpenseSplitSpec
                {
                    Id = "sp-b1",
                    Amount = new Money(1_500),
                    PoolId = Step5Scenario.PoolFreizeit,
                    CategoryId = "cat-freizeit",
                },
            ],
        });

        // Die View zeigt die Journal-Wahrheit: der offene Zyklus beginnt
        // jetzt mit der rückdatierten Ausgabe. Die persistierte Zeile
        // behält dagegen ihre stabile Identität (Begründung der Erstanlage).
        var open = (await db.Resolve<SettlementQueryService>().GetOpenAsync()).Single();
        Assert.Equal(rowId, open.Id);
        Assert.Equal("bk-backdated-exp", open.CreatedByBookingId);
        Assert.Equal(new DateOnly(2026, 11, 1), open.CreatedDate);
        Assert.Equal(3_500, open.Amount.Cents);
        Assert.Equal(3_500, open.RestAmount.Cents);

        var row = (await db.Resolve<ISettlementPositionRepository>().GetAllAsync()).Single();
        Assert.Equal(rowId, row.Id);
        Assert.Equal("bk-2026-1202", row.CreatedByBookingId);
        Assert.Equal(new DateOnly(2026, 11, 5), row.CreatedDate);

        // Salden bleiben rekonstruierbar.
        Assert.Equal(90_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
        Assert.Equal(26_500, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.PoolFreizeit));
    }

    [Fact(DisplayName = "Rückdatierte Ausgabe vereinigt zwei beglichene Zyklen: jüngste Zeile wird wieder geöffnet")]
    public async Task BackdatedExpense_MergesTwoSettledCycles_ReopensLastRow()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        await ExpenseAsync(db, "bk-ep-a", new DateOnly(2026, 11, 1), 2_000);
        await IncomeAsync(db, "bk-ep-b", new DateOnly(2026, 11, 2), 2_000);
        var rowAId = (await db.Resolve<SettlementQueryService>().GetAllAsync()).Single().Id;
        Assert.Empty(await db.Resolve<SettlementQueryService>().GetOpenAsync());

        await ExpenseAsync(db, "bk-ep-c", new DateOnly(2026, 11, 3), 2_000);
        var rowCId = (await db.Resolve<SettlementQueryService>().GetOpenAsync()).Single().Id;
        Assert.NotEqual(rowAId, rowCId);

        await IncomeAsync(db, "bk-ep-d", new DateOnly(2026, 11, 4), 2_000);
        Assert.Empty(await db.Resolve<SettlementQueryService>().GetOpenAsync());

        // Rückdatierte Ausgabe VOR beide Zyklen: die Zelle trägt jetzt einen
        // einzigen offenen Zyklus (−10, −30, −10, −30, −10), der die
        // jüngste beglichene Zeile wieder öffnet — die ältere bleibt
        // unverändert in der Historie.
        await ExpenseAsync(db, "bk-ep-x", new DateOnly(2026, 10, 31), 1_000);

        // Die View zeigt die Journal-Wahrheit (Zyklus beginnt jetzt mit der
        // rückdatierten Ausgabe) mit der stabilen Zeilen-Id; die persistierte
        // Zeile behält ihre Begründung.
        var open = (await db.Resolve<SettlementQueryService>().GetOpenAsync()).Single();
        Assert.Equal(rowCId, open.Id);
        Assert.Equal("bk-ep-x", open.CreatedByBookingId);
        Assert.Equal(new DateOnly(2026, 10, 31), open.CreatedDate);
        // Schuldzuwächse des vereinigten Zyklus: 10 € + 20 € + 20 € = 50 €,
        // davon 40 € wieder abgedeckt → 10 € offen.
        Assert.Equal(5_000, open.Amount.Cents);
        Assert.Equal(1_000, open.RestAmount.Cents);

        var rowC = (await db.Resolve<ISettlementPositionRepository>().GetAllAsync())
            .Single(r => r.Id == rowCId);
        Assert.Equal("bk-ep-c", rowC.CreatedByBookingId);

        // Die ältere beglichene Zeile hat nach der Zusammenführung kein
        // Gegenstück im abgeleiteten Journalzustand mehr und bleibt
        // unsichtbar (Historie) — sie wird nicht umgeschrieben.
        var all = await db.Resolve<SettlementQueryService>().GetAllAsync();
        Assert.DoesNotContain(all, r => r.Id == rowAId);
        var persistedA = (await db.Resolve<ISettlementPositionRepository>().GetAllAsync())
            .Single(r => r.Id == rowAId);
        Assert.Equal(SettlementStatus.Beglichen, persistedA.Status);
        Assert.Equal("bk-ep-b", persistedA.SettledByBookingId);
    }

    [Fact(DisplayName = "Rückdatierte Einnahme gleicht die offene Episode vollständig aus: Buchung wird gespeichert, Position kontrolliert abgeschlossen")]
    public async Task BackdatedIncome_FullSettlement_SavesIncomeAndClosesRow()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await Step5Scenario.AddMixedPurchaseAsync(db);
        var rowId = await OpenRowIdAsync(db);

        // Später erfasster tatsächlich eingegangener Geldfluss: Einnahme
        // +20 € mit echtem Datum 2.11., VOR der Ausgabe vom 5.11. Im
        // Replay läuft die Zelle +20 → 0 und bleibt zu Recht ohne
        // offenen Ausgleich — die gültige Einnahme wird gespeichert,
        // die Positionshistorie wird kontrolliert abgeschlossen.
        var result = await db.Resolve<IncomeService>().CreateAsync(new IncomeSpec
        {
            Id = "bk-backdated-cancel",
            Date = new DateOnly(2026, 11, 2),
            Amount = new Money(2_000),
            AccountId = Step5Scenario.Haupt,
            Note = "Rückdatierte Vollausgleichung",
            Allocations = [new IncomeAllocationSpec(Step5Scenario.PoolFreizeit, new Money(2_000))],
        });
        Assert.True(result.Created);

        // Salden: Einnahme +20 € auf dem Hauptkonto, Freizeit-Pool
        // wieder voll (300 €).
        Assert.Equal(93_500, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
        Assert.Equal(30_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Freizeit));
        Assert.Equal(0, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Wohnung));
        Assert.Equal(93_500, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.Flex));
        Assert.Equal(30_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.PoolFreizeit));

        // Keine offene Position mehr.
        Assert.Empty(await db.Resolve<SettlementQueryService>().GetOpenAsync());

        // Historie erhalten: dieselbe Zeile (Id, Begründung, Fachdatum,
        // Notiz, Höhe), kontrolliert abgeschlossen — Begleichungs-
        // referenz ist die rückdatierte Einnahme.
        var stored = (await db.Resolve<ISettlementPositionRepository>().GetAllAsync()).Single();
        Assert.Equal(rowId, stored.Id);
        Assert.Equal("bk-2026-1202", stored.CreatedByBookingId);
        Assert.Equal(new DateOnly(2026, 11, 5), stored.CreatedDate);
        Assert.Equal(2_000, stored.Amount.Cents);
        Assert.Equal(0, stored.RestAmount.Cents);
        Assert.Equal(SettlementStatus.Beglichen, stored.Status);
        Assert.Equal("bk-backdated-cancel", stored.SettledByBookingId);
        Assert.Equal(new DateOnly(2026, 11, 2), stored.SettledDate);

        // Neustart: neuer Provider auf derselben Datei — Zustand bleibt
        // stabil, Abfragen funktionieren.
        using var services = db.NewServiceProvider();
        using var scope = services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<SettlementQueryService>();
        Assert.Empty(await query.GetOpenAsync());
        var repo = scope.ServiceProvider.GetRequiredService<ISettlementPositionRepository>();
        var persisted = (await repo.GetAllAsync()).Single();
        Assert.Equal(rowId, persisted.Id);
        Assert.Equal(SettlementStatus.Beglichen, persisted.Status);
        Assert.Equal("bk-backdated-cancel", persisted.SettledByBookingId);
        Assert.Equal(2_000, persisted.Amount.Cents);
        Assert.Equal(0, persisted.RestAmount.Cents);
    }

    [Fact(DisplayName = "Gleichdatierter Zufluss mit kleinerer Id gleicht die offene Episode vollständig aus: Buchung wird gespeichert")]
    public async Task SameDayIncome_SmallerId_FullSettlement_SavesIncomeAndClosesRow()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await Step5Scenario.AddMixedPurchaseAsync(db);
        var rowId = await OpenRowIdAsync(db);

        // Derselbe Fall wie rückdatiert, aber gleichdatiert: der Zufluss
        // sortiert wegen der lexikografisch kleineren Id VOR der Ausgabe
        // — die kanonische Reihenfolge ist identisch (+20 → 0).
        var result = await db.Resolve<IncomeService>().CreateAsync(new IncomeSpec
        {
            Id = "bk-2026-1100", // < "bk-2026-1202" (lexikografisch)
            Date = new DateOnly(2026, 11, 5),
            Amount = new Money(2_000),
            AccountId = Step5Scenario.Haupt,
            Note = "Gleichdatierte Vollausgleichung",
            Allocations = [new IncomeAllocationSpec(Step5Scenario.PoolFreizeit, new Money(2_000))],
        });
        Assert.True(result.Created);

        Assert.Equal(93_500, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
        Assert.Equal(30_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.PoolFreizeit));
        Assert.Empty(await db.Resolve<SettlementQueryService>().GetOpenAsync());

        var stored = (await db.Resolve<ISettlementPositionRepository>().GetAllAsync()).Single();
        Assert.Equal(rowId, stored.Id);
        Assert.Equal(SettlementStatus.Beglichen, stored.Status);
        Assert.Equal("bk-2026-1100", stored.SettledByBookingId);
        Assert.Equal(new DateOnly(2026, 11, 5), stored.SettledDate);
    }

    [Fact(DisplayName = "Legacy-Zelle ohne Positionszeilen: neue Ausgabe legt genau eine offene Zeile an")]
    public async Task LegacyCell_NewExpense_CreatesExactlyOneOpenRow()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        // Schritt-4-Historie: Ausgabe −20 €, spätere Einnahme +20 € auf
        // derselben Zelle — abgeschlossener Zyklus.
        await ExpenseAsync(db, "bk-legacy-exp", new DateOnly(2026, 11, 1), 2_000);
        await IncomeAsync(db, "bk-legacy-inc", new DateOnly(2026, 11, 2), 2_000);

        // Datenbank aus einer Version ohne persistierte Positionszeilen
        // simulieren: die angelegte Zeile löschen.
        using (var scope = db.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LarioDbContext>();
            context.SettlementPositions.RemoveRange(
                await context.SettlementPositions.ToListAsync());
            await context.SaveChangesAsync();
        }

        // Startup-Backfill: keine offene Episode → keine neuen Zeilen.
        Assert.Equal(0, await db.Resolve<SettlementPositionBackfillService>()
            .EnsureConsistentAsync());

        // Neue Ausgabe −10 € auf derselben Zelle: die offene Episode ist
        // die JÜNGSTE (nach der abgeschlossenen Legacy-Episode) — genau
        // eine neue offene Positionszeile mit 10 € Rest wird gemeinsam
        // mit der Buchung gespeichert, die direkte Abfrage funktioniert.
        await ExpenseAsync(db, "bk-legacy-exp2", new DateOnly(2026, 11, 20), 1_000);

        var open = Assert.Single(await db.Resolve<SettlementQueryService>().GetOpenAsync());
        Assert.Equal("bk-legacy-exp2", open.CreatedByBookingId);
        Assert.Equal(new DateOnly(2026, 11, 20), open.CreatedDate);
        Assert.Equal(1_000, open.Amount.Cents);
        Assert.Equal(1_000, open.RestAmount.Cents);

        var stored = Assert.Single(await db.Resolve<ISettlementPositionRepository>().GetAllAsync());
        Assert.Equal(open.Id, stored.Id);
        Assert.Equal(SettlementStatus.Offen, stored.Status);
        Assert.Equal("bk-legacy-exp2", stored.CreatedByBookingId);
        Assert.Equal(1_000, stored.RestAmount.Cents);

        // Salden bleiben rekonstruierbar.
        Assert.Equal(99_000, await Step5Scenario.AccountBalanceAsync(db, Step5Scenario.Haupt));
        Assert.Equal(29_000, await Step5Scenario.PoolBalanceAsync(db, Step5Scenario.PoolFreizeit));
    }

    private static Task ExpenseAsync(
        TestDatabase db, string id, DateOnly date, long cents) =>
        db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = id,
            Date = date,
            AccountId = Step5Scenario.Haupt,
            TotalAmount = new Money(cents),
            Splits =
            [
                new ExpenseSplitSpec
                {
                    Id = $"sp-{id}",
                    Amount = new Money(cents),
                    PoolId = Step5Scenario.PoolFreizeit,
                    CategoryId = "cat-freizeit",
                },
            ],
        });

    private static Task IncomeAsync(
        TestDatabase db, string id, DateOnly date, long cents) =>
        db.Resolve<IncomeService>().CreateAsync(new IncomeSpec
        {
            Id = id,
            Date = date,
            Amount = new Money(cents),
            AccountId = Step5Scenario.Haupt,
            Allocations = [new IncomeAllocationSpec(Step5Scenario.PoolFreizeit, new Money(cents))],
        });
}

/// <summary>
/// Schritt-5-Review P2b auf echter SQLite: die Herkunft der
/// Verteilungszeilen (explizit / implizit) ist Buchungsinhalt
/// (<see cref="Booking.AllocationExplicit"/>) und steuert die Idempotenz —
/// eine Wiederholung mit anderer Herkunft ist ein Konflikt, auch wenn die
/// Zeilen der gespeicherten Auflösung entsprechen.
/// </summary>
public class AllocationProvenanceTests
{
    [Fact(DisplayName = "Transfer: Migration trägt die Spalte AllocationExplicit")]
    public async Task Migration_AddsAllocationExplicitColumn()
    {
        await using var db = new TestDatabase();
        Assert.Contains(
            "AllocationExplicit",
            await ColumnNamesAsync(db, "Bookings"));
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

    [Fact(DisplayName = "Transfer: explizite Wiederholung einer implizit aufgelösten Erstanlage ist ein Konflikt")]
    public async Task Transfer_Repeat_ExplicitAfterImplicit_IsConflict()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        var service = db.Resolve<TransferService>();
        var spec = new TransferSpec
        {
            Id = "tr-herkunft-1",
            Date = new DateOnly(2026, 11, 10),
            FromAccountId = Step5Scenario.Freizeit,
            ToAccountId = Step5Scenario.Wohnung,
            Amount = new Money(10_000),
            Purposes = null, // implizit: exakt eine positive Zelle (Freizeit)
        };

        var first = await service.CreateAsync(spec);
        Assert.True(first.Created);
        Assert.False(first.Booking.AllocationExplicit);

        var explicitRepeat = new TransferSpec
        {
            Id = spec.Id,
            Date = spec.Date,
            FromAccountId = spec.FromAccountId,
            ToAccountId = spec.ToAccountId,
            Amount = spec.Amount,
            Purposes = [new PoolAllocation(Step5Scenario.PoolFreizeit, new Money(10_000))],
        };

        var ex = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.CreateAsync(explicitRepeat));
        Assert.Contains("anderer Inhalt", ex.Message);

        // Die ursprüngliche Buchung bleibt unverändert.
        var stored = (await db.Resolve<IBookingRepository>().GetAllAsync())
            .Single(b => b.Id == spec.Id);
        Assert.False(stored.AllocationExplicit);
        Assert.Single(stored.TransferPurposes);
    }

    [Fact(DisplayName = "Transfer: implizite Wiederholung einer explizit verteilten Erstanlage ist ein Konflikt")]
    public async Task Transfer_Repeat_ImplicitAfterExplicit_IsConflict()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        var service = db.Resolve<TransferService>();
        var spec = new TransferSpec
        {
            Id = "tr-herkunft-2",
            Date = new DateOnly(2026, 11, 10),
            FromAccountId = Step5Scenario.Freizeit,
            ToAccountId = Step5Scenario.Wohnung,
            Amount = new Money(10_000),
            Purposes = [new PoolAllocation(Step5Scenario.PoolFreizeit, new Money(10_000))],
        };

        var first = await service.CreateAsync(spec);
        Assert.True(first.Created);
        Assert.True(first.Booking.AllocationExplicit);

        var repeat = new TransferSpec
        {
            Id = spec.Id,
            Date = spec.Date,
            FromAccountId = spec.FromAccountId,
            ToAccountId = spec.ToAccountId,
            Amount = spec.Amount,
            Purposes = null,
        };

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.CreateAsync(repeat));

        var stored = (await db.Resolve<IBookingRepository>().GetAllAsync())
            .Single(b => b.Id == spec.Id);
        Assert.True(stored.AllocationExplicit);
    }

    [Fact(DisplayName = "Umwidmung: implizite Wiederholung einer explizit kontoverteilten Erstanlage ist ein Konflikt")]
    public async Task Reassignment_Repeat_ImplicitAfterExplicit_IsConflict()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        var service = db.Resolve<ReassignmentService>();
        var spec = new ReassignmentSpec
        {
            Id = "rr-herkunft-1",
            Date = new DateOnly(2026, 11, 10),
            FromPoolId = Step5Scenario.Flex,
            ToPoolId = Step5Scenario.Ruecklage,
            Amount = new Money(5_000),
            Accounts = [new AccountAllocation(Step5Scenario.Haupt, new Money(5_000))],
        };

        var first = await service.CreateAsync(spec);
        Assert.True(first.Created);
        Assert.True(first.Booking.AllocationExplicit);

        var repeat = new ReassignmentSpec
        {
            Id = spec.Id,
            Date = spec.Date,
            FromPoolId = spec.FromPoolId,
            ToPoolId = spec.ToPoolId,
            Amount = spec.Amount,
            Accounts = null, // implizit: exakt ein Träger (Haupt)
        };

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.CreateAsync(repeat));

        var stored = (await db.Resolve<IBookingRepository>().GetAllAsync())
            .Single(b => b.Id == spec.Id);
        Assert.True(stored.AllocationExplicit);
    }

    [Fact(DisplayName = "Buchungen ohne Verteilungszeilen tragen AllocationExplicit = null")]
    public async Task NonAllocationBookings_HaveNullProvenance()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await Step5Scenario.AddMixedPurchaseAsync(db);

        var purchase = (await db.Resolve<IBookingRepository>().GetAllAsync())
            .Single(b => b.Id == "bk-2026-1202");
        Assert.Null(purchase.AllocationExplicit);
    }
}

/// <summary>
/// Schritt-5-Review P2d auf echter SQLite: der Upgrade-Backfill
/// materialisiert offene Ausgleichspositionen ohne Zeile (Datenbanken aus
/// Zeiten vor Schritt 5), atomar und idempotent. Beglichene Episoden ohne
/// Zeile bleiben unsichtbar (siehe LegacyDb-Cases in
/// Step5SettlementFlowTests).
/// </summary>
public class SettlementBackfillTests
{
    [Fact(DisplayName = "Backfill: offene Position ohne Zeile wird nachgeführt, zweiter Lauf bleibt leer")]
    public async Task Backfill_OpenEpisodeWithoutRow_IsMaterialized_AndIdempotent()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await Step5Scenario.AddMixedPurchaseAsync(db);

        // Simulierte Altdatenbank: die Positionszeile existiert nicht.
        var ctx = db.Resolve<LarioDbContext>();
        var rows = await ctx.SettlementPositions.ToListAsync();
        var row = rows.Single();
        ctx.SettlementPositions.Remove(row);
        await ctx.SaveChangesAsync();

        var backfill = db.Resolve<SettlementPositionBackfillService>();
        Assert.Equal(1, await backfill.EnsureConsistentAsync());

        var open = (await db.Resolve<SettlementQueryService>().GetOpenAsync()).Single();
        Assert.Equal("bk-2026-1202", open.CreatedByBookingId);
        Assert.Equal(new DateOnly(2026, 11, 5), open.CreatedDate);
        Assert.Equal(2_000, open.Amount.Cents);
        Assert.Equal(2_000, open.RestAmount.Cents);
        var storedRow = (await db.Resolve<ISettlementPositionRepository>().GetAllAsync())
            .Single();
        Assert.StartsWith("Nachgeführte Position", storedRow.Note ?? string.Empty);

        // Zweiter Lauf: bereits konsistent.
        Assert.Equal(0, await backfill.EnsureConsistentAsync());
    }

    [Fact(DisplayName = "Backfill: bereits konsistente Datenbank bleibt unverändert")]
    public async Task Backfill_ConsistentDatabase_ChangesNothing()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await Step5Scenario.AddMixedPurchaseAsync(db);

        Assert.Equal(0, await db.Resolve<SettlementPositionBackfillService>()
            .EnsureConsistentAsync());
        Assert.Single(await db.Resolve<SettlementQueryService>().GetAllAsync());
    }
}
