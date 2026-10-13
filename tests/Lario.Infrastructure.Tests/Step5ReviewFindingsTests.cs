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
/// Schritt-5-Review R3 auf echter SQLite: ein abgeschlossener Sachverhalt
/// wird nicht durch einen späteren neuen Zyklus überschrieben. Vor dem
/// Fix trug die jüngste beglichene Zeile die neue offene Episode (wird
/// wieder geöffnet) — die abgeschlossene Historie war verloren. Jetzt
/// wird für den neuen Zyklus eine eigene Zeile mit neuem Id angelegt;
/// die beglichene Zeile bleibt unverändert (auch über einen Neustart).
/// </summary>
public class SettlementNewCycleTests
{
    [Fact(DisplayName = "Neuer Zyklus nach abgeschlossener Episode: eigene Zeile mit neuem Id, alte Zeile bleibt beglichen")]
    public async Task NewCycleAfterSettledEpisode_CreatesNewRow_KeepsOldSettled()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await Step5Scenario.AddMixedPurchaseAsync(db);
        var rowId = await OpenRowIdAsync(db);

        // Einnahme +20 € schließt den Zyklus (Zufluss nach der Begründung
        // in der kanonischen Reihenfolge) — die Zeile wird beglichen.
        await db.Resolve<IncomeService>().CreateAsync(new IncomeSpec
        {
            Id = "bk-2026-1210",
            Date = new DateOnly(2026, 11, 10),
            Amount = new Money(2_000),
            AccountId = Step5Scenario.Haupt,
            Note = "Abschluss",
            Allocations = [new IncomeAllocationSpec(Step5Scenario.PoolFreizeit, new Money(2_000))],
        });

        var settled = Assert.Single(await db.Resolve<ISettlementPositionRepository>().GetAllAsync());
        Assert.Equal(rowId, settled.Id);
        Assert.Equal(SettlementStatus.Beglichen, settled.Status);
        Assert.Equal("bk-2026-1210", settled.SettledByBookingId);

        // Neue Ausgabe −10 € auf derselben Zelle: neuer Zyklus — eigene
        // Zeile mit NEUEM Id, die beglichene Zeile bleibt unverändert.
        await ExpenseAsync(db, "bk-2026-1220", new DateOnly(2026, 11, 20), 1_000);

        var open = Assert.Single(await db.Resolve<SettlementQueryService>().GetOpenAsync());
        Assert.NotEqual(rowId, open.Id);
        Assert.Equal("bk-2026-1220", open.CreatedByBookingId);
        Assert.Equal(1_000, open.RestAmount.Cents);

        var all = await db.Resolve<ISettlementPositionRepository>().GetAllAsync();
        Assert.Equal(2, all.Count);
        var oldRow = all.Single(r => r.Id == rowId);
        Assert.Equal(SettlementStatus.Beglichen, oldRow.Status);
        Assert.Equal("bk-2026-1202", oldRow.CreatedByBookingId);
        Assert.Equal(2_000, oldRow.Amount.Cents);
        Assert.Equal(0, oldRow.RestAmount.Cents);
        Assert.Equal("bk-2026-1210", oldRow.SettledByBookingId);

        // Neustart: Zustand bleibt stabil (keine Wiedereröffnung der alten
        // Zeile bei der nächsten Abfrage/Übernahme).
        using var services = db.NewServiceProvider();
        using var scope = services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<SettlementQueryService>();
        var views = await query.GetAllAsync();
        Assert.Equal(2, views.Count);
        Assert.Single(views, v => v.Status == SettlementStatus.Offen);
        var repo = scope.ServiceProvider.GetRequiredService<ISettlementPositionRepository>();
        var persisted = await repo.GetAllAsync();
        var persistedOld = persisted.Single(r => r.Id == rowId);
        Assert.Equal(SettlementStatus.Beglichen, persistedOld.Status);
        Assert.Equal("bk-2026-1210", persistedOld.SettledByBookingId);
    }

    [Fact(DisplayName = "Neuer Zyklus nach rückdatiertem Vollausgleich: eigene Zeile, abgeschlossene Zeile bleibt erhalten")]
    public async Task NewCycleAfterBackdatedFullSettlement_CreatesNewRow_KeepsClosedHistory()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await Step5Scenario.AddMixedPurchaseAsync(db);
        var rowId = await OpenRowIdAsync(db);

        // Rückdatierte Einnahme +20 €: der Zyklus ist im Replay verschwunden,
        // die Zeile wird kontrolliert abgeschlossen (P1a).
        await db.Resolve<IncomeService>().CreateAsync(new IncomeSpec
        {
            Id = "bk-2026-1110",
            Date = new DateOnly(2026, 11, 2),
            Amount = new Money(2_000),
            AccountId = Step5Scenario.Haupt,
            Note = "Rückdatierte Vollausgleichung",
            Allocations = [new IncomeAllocationSpec(Step5Scenario.PoolFreizeit, new Money(2_000))],
        });

        var closed = Assert.Single(await db.Resolve<ISettlementPositionRepository>().GetAllAsync());
        Assert.Equal(rowId, closed.Id);
        Assert.Equal(SettlementStatus.Beglichen, closed.Status);
        Assert.Equal("bk-2026-1110", closed.SettledByBookingId);

        // Neue Ausgabe −10 €: neuer Zyklus — neue Zeile, die alte bleibt.
        await ExpenseAsync(db, "bk-2026-1220", new DateOnly(2026, 11, 20), 1_000);

        var all = await db.Resolve<ISettlementPositionRepository>().GetAllAsync();
        Assert.Equal(2, all.Count);
        var oldRow = all.Single(r => r.Id == rowId);
        Assert.Equal(SettlementStatus.Beglichen, oldRow.Status);
        Assert.Equal("bk-2026-1110", oldRow.SettledByBookingId);
        var newRow = all.Single(r => r.Id != rowId);
        Assert.Equal(SettlementStatus.Offen, newRow.Status);
        Assert.Equal("bk-2026-1220", newRow.CreatedByBookingId);
        Assert.Equal(1_000, newRow.RestAmount.Cents);

        // Die Ansicht zeigt beide (keine Integritätsfehler).
        var views = await db.Resolve<SettlementQueryService>().GetAllAsync();
        Assert.Equal(2, views.Count);
        Assert.Equal(1, views.Count(v => v.Status == SettlementStatus.Offen));
    }

    [Fact(DisplayName = "Neuer Zyklus nach Zusammenführung und Abschluss: dritte Zeile, Historie intakt")]
    public async Task NewCycleAfterMergeAndSettlement_CreatesThirdRow()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        // Zwei abgeschlossene Zyklen (je 10 €).
        await ExpenseAsync(db, "bk-r3-a", new DateOnly(2026, 11, 1), 1_000);
        await IncomeAsync(db, "bk-r3-ap", new DateOnly(2026, 11, 3), 1_000);
        await ExpenseAsync(db, "bk-r3-b", new DateOnly(2026, 11, 5), 1_000);
        await IncomeAsync(db, "bk-r3-bp", new DateOnly(2026, 11, 7), 1_000);
        var rowA = (await db.Resolve<ISettlementPositionRepository>().GetAllAsync())
            .Single(r => r.CreatedByBookingId == "bk-r3-a").Id;
        var rowB = (await db.Resolve<ISettlementPositionRepository>().GetAllAsync())
            .Single(r => r.CreatedByBookingId == "bk-r3-b").Id;

        // Rückdatierte Ausgabe vereinigt beide Zyklen zu einem offenen
        // (die jüngste Zeile wird wieder geöffnet): 30 € Schuldzuwachs,
        // 20 € bereits abgedeckt (bk-r3-ap, bk-r3-bp) -> 10 € offen.
        // Der Abschluss gleicht exakt die offenen 10 € aus (kein
        // Überschuss, der die Folgeausgabe aufheben würde); die Zeile
        // bleibt die Begründung des zweiten Zyklus.
        await ExpenseAsync(db, "bk-r3-c", new DateOnly(2026, 11, 2), 1_000);
        await IncomeAsync(db, "bk-r3-d", new DateOnly(2026, 11, 10), 1_000);

        // Neuer Zyklus: dritte Zeile mit neuem Id.
        await ExpenseAsync(db, "bk-r3-e", new DateOnly(2026, 11, 20), 1_000);

        var all = await db.Resolve<ISettlementPositionRepository>().GetAllAsync();
        Assert.Equal(3, all.Count);
        Assert.Equal(SettlementStatus.Beglichen,
            all.Single(r => r.Id == rowA).Status);
        Assert.Equal("bk-r3-ap",
            all.Single(r => r.Id == rowA).SettledByBookingId);
        var mergedRow = all.Single(r => r.Id == rowB);
        Assert.Equal(SettlementStatus.Beglichen, mergedRow.Status);
        Assert.Equal("bk-r3-d", mergedRow.SettledByBookingId);
        var newRow = all.Single(r => r.Id != rowA && r.Id != rowB);
        Assert.Equal(SettlementStatus.Offen, newRow.Status);
        Assert.Equal("bk-r3-e", newRow.CreatedByBookingId);

        // Die Ansicht zeigt alle drei Positionen (keine Integritätsfehler
        // durch die zusammengeführte Historie).
        var views = await db.Resolve<SettlementQueryService>().GetAllAsync();
        Assert.Equal(3, views.Count);
        Assert.Equal(1, views.Count(v => v.Status == SettlementStatus.Offen));
        Assert.Contains(views, v => v.CreatedByBookingId == "bk-r3-a"
            && v.SettledByBookingId == "bk-r3-ap");
        Assert.Contains(views, v => v.CreatedByBookingId == "bk-r3-b"
            && v.SettledByBookingId == "bk-r3-d");
    }

    [Fact(DisplayName = "Legacy-Episode ohne Zeile neben neuem Zyklus: Ansicht bleibt intakt, Legacy bleibt unsichtbar")]
    public async Task LegacyEpisodeWithoutRow_NewCycle_ViewStaysConsistent()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        // Abgeschlossener Zyklus, dann Zeile gelöscht (Altdatenbank vor
        // der Positionsverwaltung).
        await ExpenseAsync(db, "bk-legacy-exp", new DateOnly(2026, 11, 1), 1_000);
        await IncomeAsync(db, "bk-legacy-inc", new DateOnly(2026, 11, 3), 1_000);
        using (var scope = db.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LarioDbContext>();
            context.SettlementPositions.RemoveRange(
                await context.SettlementPositions.ToListAsync());
            await context.SaveChangesAsync();
        }

        // Neuer Zyklus: eigene Zeile; die Legacy-Episode bleibt unsichtbar,
        // die Abfrage schlägt nicht fehl.
        await ExpenseAsync(db, "bk-legacy-exp2", new DateOnly(2026, 11, 20), 1_000);

        var views = await db.Resolve<SettlementQueryService>().GetAllAsync();
        var open = Assert.Single(views);
        Assert.Equal("bk-legacy-exp2", open.CreatedByBookingId);
        Assert.Equal(SettlementStatus.Offen, open.Status);
        Assert.Single(await db.Resolve<ISettlementPositionRepository>().GetAllAsync());
    }

    [Fact(DisplayName = "Manipulierte beglichene Zeile in unverändertem Präfix: sichtbarer Fehler, Buchung wird verworfen")]
    public async Task TamperedSettledRow_UnchangedPrefix_FailsVisibly()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);
        await Step5Scenario.AddMixedPurchaseAsync(db);
        await db.Resolve<IncomeService>().CreateAsync(new IncomeSpec
        {
            Id = "bk-2026-1210",
            Date = new DateOnly(2026, 11, 10),
            Amount = new Money(2_000),
            AccountId = Step5Scenario.Haupt,
            Note = "Abschluss",
            Allocations = [new IncomeAllocationSpec(Step5Scenario.PoolFreizeit, new Money(2_000))],
        });

        // Beglichene Zeile außerhalb des Systems manipulieren (Rest != 0).
        using (var scope = db.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LarioDbContext>();
            var stored = await context.SettlementPositions.SingleAsync();
            stored.RestAmount = new Money(500);
            await context.SaveChangesAsync();
        }

        // Neue Buchung auf derselben Zelle: der werteidentische Präfix
        // besteht nicht mehr — die Abfrage/Speicherung schlägt sichtbar
        // fehl, die Buchung wird nicht persistiert.
        var before = await Step5Scenario.JournalAsync(db);
        var exception = await Assert.ThrowsAsync<LarioDomainException>(() =>
            ExpenseAsync(db, "bk-tamper", new DateOnly(2026, 11, 20), 1_000));
        Assert.Contains("Integritätsfehler", exception.Message);

        var after = await Step5Scenario.JournalAsync(db);
        Assert.Equal(before.Bookings.Count, after.Bookings.Count);
    }

    private static async Task<string> OpenRowIdAsync(TestDatabase db) =>
        (await db.Resolve<ISettlementPositionRepository>().GetAllAsync())
            .Single(r => r.Status == SettlementStatus.Offen).Id;

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

/// <summary>
/// Schritt-5-Review R4 auf echter SQLite: rückdatierte Zuflüsse, die
/// bereits beglichene Ausgleichszyklen verändern, dürfen spätere
/// Buchungen nicht ablehnen. Die Zeile bleibt begründungsgleich und
/// übernimmt die veränderten Werte (Fall A); schließt der rückdatierte
/// Zufluss einen offenen Zyklus, trägt die alte Zeile den abgeschlossenen
/// linken Teil und ein folgender neuer offener Zyklus bekommt eine eigene
/// Zeile mit neuem Id (Fall B); verschwindet der Zyklus im Replay, wird
/// die Zeile kontrolliert abgeschlossen und ein späterer neuer Zyklus
/// bekommt ebenfalls eine eigene Zeile (Fall C).
/// </summary>
public class SettlementBackdatedValueTests
{
    [Fact(DisplayName = "R4 Fall A: rückdatierter Zufluss vor der Begründung kürzt einen beglichenen Zyklus — Zeile behält Identität, übernimmt den neuen Wert")]
    public async Task BackdatedIncome_BeforeSettledCycle_RowAdoptsChangedValue()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        // Abgeschlossener Zyklus: Ausgabe 20 €, Einnahme 20 €.
        await ExpenseAsync(db, "bk-r4-a1", new DateOnly(2026, 11, 1), 2_000);
        await IncomeAsync(db, "bk-r4-a2", new DateOnly(2026, 11, 2), 2_000);

        var row = (await db.Resolve<SettlementQueryService>().GetAllAsync()).Single();
        Assert.Equal(SettlementStatus.Beglichen, row.Status);
        Assert.Equal(2_000, row.Amount.Cents);
        Assert.Equal("bk-r4-a1", row.CreatedByBookingId);
        Assert.Equal("bk-r4-a2", row.SettledByBookingId);

        // Einnahme 15 € DREI TAGE VOR der Begründung: der Überfluss
        // mindert die spätere Schuld — der Zyklus schließt an derselben
        // Einnahme, aber mit 5 € statt 20 €.
        await IncomeAsync(db, "bk-r4-a0", new DateOnly(2026, 10, 30), 1_500);

        var updated = (await db.Resolve<SettlementQueryService>().GetAllAsync()).Single();
        Assert.Equal(row.Id, updated.Id);
        Assert.Equal("bk-r4-a1", updated.CreatedByBookingId);
        Assert.Equal(new DateOnly(2026, 11, 1), updated.CreatedDate);
        Assert.Equal(500, updated.Amount.Cents);
        Assert.Equal(0, updated.RestAmount.Cents);
        Assert.Equal(SettlementStatus.Beglichen, updated.Status);
        Assert.Equal("bk-r4-a2", updated.SettledByBookingId);
        Assert.Equal(new DateOnly(2026, 11, 2), updated.SettledDate);

        Assert.Empty(await db.Resolve<SettlementQueryService>().GetOpenAsync());
    }

    [Fact(DisplayName = "R4 Fall B: rückdatierter Zufluss zwischen zwei Ausgaben schließt den ersten Zyklus — alte Zeile trägt den linken Teil, neuer Zyklus bekommt eigene Zeile")]
    public async Task BackdatedIncome_SplitsOpenCycle_OldRowCloses_NewCycleOwnRow()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        // Zwei Ausgaben ohne Begleichung: ein offener Zyklus (40 €,
        // Begründer ist die erste Ausgabe).
        await ExpenseAsync(db, "bk-r4-b1", new DateOnly(2026, 11, 1), 2_000);
        await ExpenseAsync(db, "bk-r4-b2", new DateOnly(2026, 11, 3), 2_000);
        var openRowId = await OpenRowIdAsync(db);
        var openRow = (await db.Resolve<SettlementQueryService>().GetOpenAsync()).Single();
        Assert.Equal(4_000, openRow.Amount.Cents);
        Assert.Equal(4_000, openRow.RestAmount.Cents);

        // Einnahme 20 € am 11/2 (zwischen den Ausgaben): der erste Teil
        // des Zyklus schließt (20 €, begründet von bk-r4-b1, beglichen
        // von bk-r4-b0), der zweite Teil läuft weiter (20 €, begründet
        // von bk-r4-b2).
        await IncomeAsync(db, "bk-r4-b0", new DateOnly(2026, 11, 2), 2_000);

        // Der abgeschlossene linke Teil trägt die bestehende Zeile
        // (Identität bleibt: Id, Begründung, Fachdatum).
        var settledRow = (await db.Resolve<ISettlementPositionRepository>().GetAllAsync())
            .Single(r => r.Id == openRowId);
        Assert.Equal("bk-r4-b1", settledRow.CreatedByBookingId);
        Assert.Equal(new DateOnly(2026, 11, 1), settledRow.CreatedDate);
        Assert.Equal(2_000, settledRow.Amount.Cents);
        Assert.Equal(0, settledRow.RestAmount.Cents);
        Assert.Equal(SettlementStatus.Beglichen, settledRow.Status);
        Assert.Equal("bk-r4-b0", settledRow.SettledByBookingId);
        Assert.Equal(new DateOnly(2026, 11, 2), settledRow.SettledDate);

        // Der neue offene Zyklus hat eine eigene Zeile mit neuem Id und
        // eigener Begründung — die abgeschlossene Zeile wird nicht
        // überschrieben.
        var open = (await db.Resolve<SettlementQueryService>().GetOpenAsync()).Single();
        Assert.NotEqual(openRowId, open.Id);
        Assert.Equal("bk-r4-b2", open.CreatedByBookingId);
        Assert.Equal(new DateOnly(2026, 11, 3), open.CreatedDate);
        Assert.Equal(2_000, open.Amount.Cents);
        Assert.Equal(2_000, open.RestAmount.Cents);

        // Beide Positionen sind sichtbar (keine Integritätsfehler durch
        // den geteilten Zyklus).
        var views = await db.Resolve<SettlementQueryService>().GetAllAsync();
        Assert.Equal(2, views.Count);
    }

    [Fact(DisplayName = "R4 Fall C: rückdatierter Zufluss vor der Begründung lässt den Zyklus verschwinden — Zeile wird kontrolliert abgeschlossen, späterer neuer Zyklus bekommt eigene Zeile")]
    public async Task BackdatedIncome_VanishesCycle_NewCycleOwnRow()
    {
        await using var db = new TestDatabase();
        await Step5Scenario.SeedAsync(db);

        // Offener Zyklus: Ausgabe 20 €.
        await ExpenseAsync(db, "bk-r4-c1", new DateOnly(2026, 11, 1), 2_000);
        var openRowId = await OpenRowIdAsync(db);

        // Einnahme 20 € ZWEI TAGE VOR der Ausgabe: der Überfluss deckt
        // die Ausgabe vollständig ab — im Replay existiert der Zyklus
        // nicht (Vollausgleich, verschwindender Zyklus).
        await IncomeAsync(db, "bk-r4-c0", new DateOnly(2026, 10, 30), 2_000);

        // Die offene Zeile wird kontrolliert abgeschlossen (Identität
        // und Höhe bleiben, Begleichungsreferenz ist die neue Buchung —
        // Schritt-5-Review P1a); sie verschwindet nicht still.
        var closedRow = (await db.Resolve<ISettlementPositionRepository>().GetAllAsync())
            .Single(r => r.Id == openRowId);
        Assert.Equal("bk-r4-c1", closedRow.CreatedByBookingId);
        Assert.Equal(2_000, closedRow.Amount.Cents);
        Assert.Equal(0, closedRow.RestAmount.Cents);
        Assert.Equal(SettlementStatus.Beglichen, closedRow.Status);
        Assert.Equal("bk-r4-c0", closedRow.SettledByBookingId);
        Assert.Equal(new DateOnly(2026, 10, 30), closedRow.SettledDate);
        Assert.Empty(await db.Resolve<SettlementQueryService>().GetOpenAsync());

        // Späterer neuer Zyklus: eigene Zeile mit neuem Id — die
        // abgeschlossene Zeile bleibt als Historie erhalten.
        await ExpenseAsync(db, "bk-r4-c2", new DateOnly(2026, 11, 5), 1_500);

        var open = (await db.Resolve<SettlementQueryService>().GetOpenAsync()).Single();
        Assert.NotEqual(openRowId, open.Id);
        Assert.Equal("bk-r4-c2", open.CreatedByBookingId);
        Assert.Equal(1_500, open.Amount.Cents);

        var views = await db.Resolve<SettlementQueryService>().GetAllAsync();
        Assert.Equal(2, views.Count);
        Assert.Contains(views, v => v.CreatedByBookingId == "bk-r4-c1"
            && v.SettledByBookingId == "bk-r4-c0");
    }

    private static async Task<string> OpenRowIdAsync(TestDatabase db) =>
        (await db.Resolve<ISettlementPositionRepository>().GetAllAsync())
            .Single(r => r.Status == SettlementStatus.Offen).Id;

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
