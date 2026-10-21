using Lario.Application;
using Lario.Application.Attachments;
using Lario.Application.Journal;
using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;
using Lario.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Lario.Infrastructure.Tests;

/// <summary>
/// Schritt 7a (Batch A, A7): Parallelfall Upload vs. Split-Entfernung.
///
/// Beide Operationen laufen auf einer echten SQLite-Datei (EF Core 10:
/// WAL-Modus, busy_timeout 0, Foreign Keys aktiv) parallel. Jede
/// Verzahnung muss exakt einen der konsistenten Endzustände liefern:
///   A: Upload commitet zuerst → die Korrektur umlagert den Beleg
///      in derselben Transaktion wie das Split-Löschen (D1b).
///   B: Korrektur commitet zuerst → der Upload scheitert an der
///      Referenz-/FK-Prüfung und räumt seine Datei auf (keine Waise).
///   D: Upload hält das Schreib-Lock, die Korrektur scheitert mit
///      SQLITE_BUSY → Split bleibt unverändert, der Beleg ist gültig.
/// In keinem Fall darf eine Orphan-Datei, eine Zeile ohne Datei oder
/// eine Referenz auf einen gelöschten Split zurückbleiben.
/// </summary>
public sealed class Step7aParallelRaceTests
{
    private static readonly byte[] Pdf =
    [
        0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, 0x0A,
    ];

    private static async Task<(TestDatabase Db, string BookingId, string SplitId)> SeedAsync()
    {
        var db = new TestDatabase();
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.CreateAccountAsync("acc-haupt", "Hauptkonto", AccountType.Bank);
        await admin.CreatePoolAsync("pool-flex", "Flex");
        await admin.CreateCategoryAsync("cat-wohnen", "Wohnen");
        await admin.CreateCategoryAsync("cat-miete", "Miete", parentId: "cat-wohnen");

        var expense = db.Resolve<ExpenseService>();
        await expense.CreateAsync(new ExpenseSpec
        {
            Id = "aus-race",
            Date = new DateOnly(2026, 10, 5),
            AccountId = "acc-haupt",
            TotalAmount = new Money(10_000),
            Splits =
            [
                new ExpenseSplitSpec { Id = "sp-1", Amount = new Money(6_000), PoolId = "pool-flex", CategoryId = "cat-miete" },
                new ExpenseSplitSpec { Id = "sp-2", Amount = new Money(4_000), PoolId = "pool-flex", CategoryId = "cat-miete" },
            ],
        });
        return (db, "aus-race", "sp-1");
    }

    /// <summary>Korrektur, die genau sp-1 entfernt (Gesamtbetrag wird 4000).</summary>
    private static CorrectionSpec RemovalSpec(string bookingId) => new(
        bookingId, "op-corr-race", ExpectedBaseRevision: 1,
        TotalAmount: new Money(4_000),
        Splits:
        [
            new ExpenseSplitSpec { Id = "sp-2", Amount = new Money(4_000), PoolId = "pool-flex", CategoryId = "cat-miete" },
        ]);

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Prüft die Endzustands-Invarianten für eine Runden-Kombination:
    /// keine Orphan-Dateien, keine Zeile ohne Datei, keine Referenz auf
    /// gelöschte Splits, Split-Zustand passend zum Korrekturergebnis und
    /// Upload-Ergebnis passend zu Datei/Zeile.
    /// </summary>
    private static async Task AssertConsistentTerminalStateAsync(
        TestDatabase db, string bookingId, bool uploadOk, bool correctionOk)
    {
        var rows = await db.Resolve<IAttachmentRepository>().GetAllAsync();
        var storeFiles = await db.Resolve<IAttachmentFileStore>().ListStoredFilesAsync();
        var diskFiles = System.IO.Directory.GetFiles(db.AttachmentsDir);
        var booking = await db.Resolve<IBookingRepository>().FindByIdAsync(bookingId);
        Assert.NotNull(booking);

        // Keine Orphan-Datei und keine Zeile ohne Datei.
        Assert.Equal(storeFiles.Count, diskFiles.Length);
        Assert.Equal(storeFiles.Count, rows.Count);
        foreach (var row in rows)
        {
            Assert.Contains(row.StoredName, storeFiles);
            Assert.Equal(bookingId, row.BookingId);
        }

        // Keine Referenz auf einen gelöschten Split.
        var splitIds = booking!.Splits.Select(s => s.Id).ToHashSet();
        foreach (var row in rows)
        {
            if (row.SplitId is not null)
                Assert.Contains(row.SplitId, splitIds);
        }

        // Split-Zustand passt exakt zum Korrekturergebnis (atomar:
        // sp-1 und der Ursprungszustand können nie beide fehlen).
        var actualSplits = booking.Splits.Select(s => s.Id).OrderBy(s => s).ToArray();
        if (correctionOk)
        {
            Assert.Equal(["sp-2"], actualSplits);
        }
        else
        {
            Assert.Equal(["sp-1", "sp-2"], actualSplits);
        }

        // Upload-Ergebnis passt zu Datei und Metadaten.
        if (uploadOk)
        {
            var row = Assert.Single(rows);
            Assert.True(
                row.SplitId == "sp-1" || (row.SplitId is null && row.OriginalSplitId == "sp-1"),
                $"Unerwartete Beleg-Zuordnung: SplitId={row.SplitId}, OriginalSplitId={row.OriginalSplitId}");
            var file = await db.Resolve<IAttachmentFileStore>().ReadAsync(row.StoredName);
            Assert.True(file.Exists);
            Assert.Equal(Pdf, await ReadAllAsync(file.Content!));
            file.Content!.Dispose();
        }
        else
        {
            Assert.Empty(rows);
            Assert.Empty(storeFiles);
        }

        // Beide Operationen dürfen nicht gleichzeitig fehlschlagen:
        // Wer das Schreib-Lock hält, commitet; der andere verliert.
        Assert.True(uploadOk || correctionOk, "Beide Operationen sind fehlgeschlagen — kein konsistender Gewinner.");
    }

    private static async Task<(bool Ok, string? Error)> RunUploadAsync(IServiceProvider services, string bookingId, string splitId)
    {
        try
        {
            var attachment = await services.GetRequiredService<AttachmentService>()
                .UploadAsync("quittung.pdf", "application/pdf", new MemoryStream(Pdf), bookingId, splitId, maxBytes: 10_000);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
        }
    }

    private static async Task<(bool Ok, string? Error)> RunCorrectionAsync(IServiceProvider services, string bookingId)
    {
        try
        {
            var result = await services.GetRequiredService<CorrectionService>().CorrectAsync(RemovalSpec(bookingId));
            return (result.Created, null);
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
        }
    }

    [Fact(DisplayName = "p1: Parallel Upload vs. Split-Entfernung → immer ein konsistenter Endzustand (A7)")]
    public async Task Parallel_UploadVsSplitRemoval_Always_Consistent_Terminal_State()
    {
        var outcomes = new List<string>();
        for (var round = 0; round < 3; round++)
        {
            var (db, bookingId, splitId) = await SeedAsync();
            await using (db)
            {
                using var uploadScope = db.Services.CreateScope();
                using var correctionScope = db.Services.CreateScope();

                var uploadTask = Task.Run(() => RunUploadAsync(uploadScope.ServiceProvider, bookingId, splitId));
                var correctionTask = Task.Run(() => RunCorrectionAsync(correctionScope.ServiceProvider, bookingId));

                var results = await Task.WhenAll(uploadTask, correctionTask);
                var (upload, correction) = (results[0], results[1]);
                var outcome = correction.Ok
                    ? (upload.Ok ? "A:beide-ok" : "B:korrektur-ok-upload-fehl")
                    : upload.Ok ? "D:upload-ok-korrektur-fehl" : "C:beide-fehl";
                outcomes.Add($"[{round}] {outcome}");
                Console.WriteLine($"[race {round}] {outcome}" +
                    (upload.Error is null ? "" : $" upload: {upload.Error}") +
                    (correction.Error is null ? "" : $" korrektur: {correction.Error}"));

                await AssertConsistentTerminalStateAsync(db, bookingId, upload.Ok, correction.Ok);
            }
        }

        // Sichtbarkeit der beobachteten Endzustände im Review-Log:
        Assert.Equal(3, outcomes.Count);
        Console.WriteLine("[race] beobachtete Endzustände: " + string.Join(" | ", outcomes));
    }

    [Fact(DisplayName = "p2: Korrektur commitet zuerst → Upload scheitert sauber (keine Datei, keine Zeile)")]
    public async Task Correction_Commits_First_Upload_Fails_Cleanly()
    {
        var (db, bookingId, splitId) = await SeedAsync();
        await using (db)
        {
            using var scope = db.Services.CreateScope();
            var sp = scope.ServiceProvider;

            var result = await sp.GetRequiredService<CorrectionService>().CorrectAsync(RemovalSpec(bookingId));
            Assert.True(result.Created);

            var upload = await RunUploadAsync(sp, bookingId, splitId);
            Assert.False(upload.Ok);
            Assert.Contains("InvalidReference", upload.Error, StringComparison.OrdinalIgnoreCase);

            await AssertConsistentTerminalStateAsync(db, bookingId, uploadOk: false, correctionOk: true);
        }
    }
}
