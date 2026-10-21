using System.Data.Common;
using Lario.Application;
using Lario.Application.Attachments;
using Lario.Application.Journal;
using Lario.Domain;
using Lario.Domain.Attachments;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;
using Lario.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
///
/// Review 7a, Befund 3 — zusätzlich zwei deterministische
/// Reproduktionen der Race-Fenster (kein Timing-Glück):
///   p3 (Race A): Der Split wird zwischen der Referenzprüfung des
///      Services und dem Metadaten-Insert gelöscht → der Komposit-FK
///      muss eine saubere 400-Referenzausnahme liefern, keine 500.
///   p4 (Race B): Ein Beleg wird zwischen der Attachment-Abfrage in
///      <c>ReplaceSplits</c> und dem Commit der Korrektur an den
///      entfallenden Teilposten angehängt → die Korrektur darf nicht
///      mit einem ungehandelten FK-Fehler enden, sondern liest den
///      frischen Lesezustand und umlagert den Beleg (D1b).
///   p5 (Review 7a-2, Befund 4): Zwischen dem Laden (Basisrevision 1)
///      und dem Commit der laufenden Korrektur A commitet eine
///      parallele Korrektur B die Basisrevision 2 → A muss als
///      sichtbarer Revisionskonflikt (409) scheitern; B bleibt
///      vollständig erhalten (kein lost update, keine Teilanlage).
/// </summary>
public sealed class Step7aParallelRaceTests
{
    private static readonly byte[] Pdf =
    [
        0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, 0x0A,
    ];

    private static async Task<(TestDatabase Db, string BookingId, string SplitId)> SeedAsync(
        Action<ServiceCollection, string>? configureServices = null)
    {
        var db = new TestDatabase(configureServices: configureServices);
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

    /// <summary>Korrektur, die genau sp-1 entfernt.</summary>
    private static CorrectionSpec RemovalSpec(string bookingId, string operationId, int expectedRevision)
        => new(
            bookingId, operationId, ExpectedBaseRevision: expectedRevision,
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
    /// Prüft die Endzustands-Invarianten: keine Orphan-Datei, keine
    /// Zeile ohne Datei, keine Referenz auf gelöschte Splits, exakte
    /// Beleg-Zuordnung (SplitId/OriginalSplitId), exakter Split-Zustand
    /// und exakte Buchungs-Revision.
    /// </summary>
    private static async Task AssertConsistentTerminalStateAsync(
        TestDatabase db,
        string bookingId,
        (string? SplitId, string? OriginalSplitId)[] expectedAttachments,
        string[] expectedSplitIds,
        int expectedBaseRevision)
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
            var file = await db.Resolve<IAttachmentFileStore>().ReadAsync(row.StoredName);
            Assert.True(file.Exists, $"Datei für Zeile '{row.Id}' fehlt: {row.StoredName}");
            Assert.Equal(Pdf, await ReadAllAsync(file.Content!));
            file.Content!.Dispose();
        }

        // Keine Referenz auf einen gelöschten Split.
        var splitIds = booking!.Splits.Select(s => s.Id).ToHashSet();
        foreach (var row in rows)
        {
            if (row.SplitId is not null)
            {
                Assert.Contains(row.SplitId, splitIds);
            }
        }

        // Exakte Zuordnung (Umlageung vs. ursprüngliche Split-Id).
        var actualPairs = rows
            .Select(r => (S: r.SplitId ?? "", O: r.OriginalSplitId ?? ""))
            .OrderBy(p => p.S, StringComparer.Ordinal)
            .ThenBy(p => p.O, StringComparer.Ordinal)
            .ToArray();
        var expectedPairs = expectedAttachments
            .Select(p => (S: p.SplitId ?? "", O: p.OriginalSplitId ?? ""))
            .OrderBy(p => p.S, StringComparer.Ordinal)
            .ThenBy(p => p.O, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedPairs, actualPairs);

        // Split-Zustand und Revision exakt wie erwartet (atomar:
        // sp-1 und der Ursprungszustand können nie beide fehlen).
        Assert.Equal(expectedSplitIds,
            booking.Splits.Select(s => s.Id).OrderBy(s => s, StringComparer.Ordinal).ToArray());
        Assert.Equal(expectedBaseRevision, booking.BaseRevision);
    }

    private static async Task<(bool Ok, string? Error)> RunUploadAsync(
        IServiceProvider services, string bookingId, string? splitId)
    {
        try
        {
            await services.GetRequiredService<AttachmentService>()
                .UploadAsync("quittung.pdf", "application/pdf", new MemoryStream(Pdf), bookingId, splitId, maxBytes: 10_000);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
        }
    }

    private static async Task<(bool Ok, string? Error)> RunCorrectionAsync(
        IServiceProvider services, CorrectionSpec spec)
    {
        try
        {
            var result = await services.GetRequiredService<CorrectionService>().CorrectAsync(spec);
            return (result.Created, null);
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name + ": " + ex.Message.Split('\n')[0]
                + " | Basis: " + ex.GetBaseException()?.Message);
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
                var correctionTask = Task.Run(() => RunCorrectionAsync(
                    correctionScope.ServiceProvider, RemovalSpec(bookingId, "op-corr-race", 1)));

                var results = await Task.WhenAll(uploadTask, correctionTask);
                var (upload, correction) = (results[0], results[1]);
                var outcome = correction.Ok
                    ? (upload.Ok ? "A:beide-ok" : "B:korrektur-ok-upload-fehl")
                    : upload.Ok ? "D:upload-ok-korrektur-fehl" : "C:beide-fehl";
                outcomes.Add($"[{round}] {outcome}");
                Console.WriteLine($"[race {round}] {outcome}" +
                    (upload.Error is null ? "" : $" upload: {upload.Error}") +
                    (correction.Error is null ? "" : $" korrektur: {correction.Error}"));

                // Review 7a, Befund 3 (Verstärkung): Auf den Ergebnisstand
                // der parallelen Runde folgen ein zweiter Upload
                // (Buchungsebene) und eine zweite Korrektur auf der neuen
                // Revision. Beide müssen Erfolg haben — ein veralteter
                // Lesezustand (alte Revision, alter Split-Zustand) darf
                // weder als Revisionskonflikt noch als FK-Fehler
                // durchkommen.
                var secondUpload = await RunUploadAsync(
                    uploadScope.ServiceProvider, bookingId, splitId: null);
                Assert.True(secondUpload.Ok,
                    $"Zweiter Upload (Buchungsebene) fehlgeschlagen: {secondUpload.Error}");

                var secondRevision = correction.Ok ? 2 : 1;
                var secondCorrection = await RunCorrectionAsync(correctionScope.ServiceProvider,
                    RemovalSpec(bookingId, "op-corr-race-2", secondRevision));
                Assert.True(secondCorrection.Ok,
                    $"Zweite Korrektur (erwartete Revision {secondRevision}) fehlgeschlagen: " +
                    $"{secondCorrection.Error}");

                // Endzustand nach allen vier Operationen: sp-1 ist in
                // jedem Fall entfernt (erste oder zweite Korrektur),
                // die Revision ist entsprechend erhöht.
                var expectedAttachments = correction.Ok
                    ? (upload.Ok
                        ? new (string?, string?)[] { (null, "sp-1"), (null, null) }
                        : new (string?, string?)[] { (null, null) })
                    : new (string?, string?)[] { ("sp-1", null), (null, null) };
                await AssertConsistentTerminalStateAsync(
                    db, bookingId, expectedAttachments,
                    expectedSplitIds: ["sp-2"],
                    expectedBaseRevision: correction.Ok ? 3 : 2);
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

            var result = await sp.GetRequiredService<CorrectionService>()
                .CorrectAsync(RemovalSpec(bookingId, "op-corr-race", 1));
            Assert.True(result.Created);

            var upload = await RunUploadAsync(sp, bookingId, splitId);
            Assert.False(upload.Ok);
            Assert.Contains("InvalidReference", upload.Error, StringComparison.OrdinalIgnoreCase);

            await AssertConsistentTerminalStateAsync(
                db, bookingId,
                expectedAttachments: [],
                expectedSplitIds: ["sp-2"],
                expectedBaseRevision: 2);
        }
    }

    /// <summary>
    /// Review 7a, Befund 3 (Race A, deterministisch): Dekorierendes
    /// <see cref="IAttachmentRepository"/> — <c>AddAsync</c> löscht
    /// 'sp-1' über eine zweite SQLite-Verbindung, also exakt zwischen
    /// der Referenzprüfung des Services (Buchung und Split existieren
    /// noch) und dem Metadaten-Insert (Komposit-FK verletzt). Alles
    /// Weitere wird an das echte Repository delegiert.
    /// </summary>
    private sealed class SplitDeletingAttachmentRepository(
        IAttachmentRepository inner, string connectionString) : IAttachmentRepository
    {
        public async Task AddAsync(Attachment attachment, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ExpenseSplits WHERE Id = 'sp-1';";
            await command.ExecuteNonQueryAsync(cancellationToken);
            await inner.AddAsync(attachment, cancellationToken);
        }

        public Task<Attachment?> FindByIdAsync(string id, CancellationToken cancellationToken = default)
            => inner.FindByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<Attachment>> GetByBookingAsync(string bookingId, CancellationToken cancellationToken = default)
            => inner.GetByBookingAsync(bookingId, cancellationToken);

        public Task<IReadOnlyList<Attachment>> GetAllAsync(CancellationToken cancellationToken = default)
            => inner.GetAllAsync(cancellationToken);

        public Task RemoveAsync(string id, CancellationToken cancellationToken = default)
            => inner.RemoveAsync(id, cancellationToken);

        public Task UpdateSplitReferenceAsync(
            string id, string? splitId, string? originalSplitId, CancellationToken cancellationToken = default)
            => inner.UpdateSplitReferenceAsync(id, splitId, originalSplitId, cancellationToken);
    }

    [Fact(DisplayName = "p3: deterministisch Race A — Split zwischen Referenzprüfung und Insert gelöscht → saubere Referenzausnahme, keine Waise")]
    public async Task RaceA_SplitDeletedBetweenCheckAndInsert_Fails_Cleanly()
    {
        var (db, bookingId, splitId) = await SeedAsync(configureServices: (services, connectionString) =>
        {
            var original = Assert.Single(services, d => d.ServiceType == typeof(IAttachmentRepository));
            services.Remove(original);
            services.AddScoped<IAttachmentRepository>(sp => new SplitDeletingAttachmentRepository(
                new AttachmentRepository(sp.GetRequiredService<LarioDbContext>()), connectionString));
        });
        await using (db)
        {
            using var scope = db.Services.CreateScope();
            var upload = await RunUploadAsync(scope.ServiceProvider, bookingId, splitId);

            // Die Referenzprüfung war erfolgreich (Split existierte),
            // das Insert scheitert am Komposit-FK → fachliche
            // Referenzausnahme (400), kein interner Fehler (500).
            Assert.False(upload.Ok);
            Assert.Contains("InvalidReference", upload.Error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DbUpdateException", upload.Error, StringComparison.OrdinalIgnoreCase);

            // Keine Waise: keine Datei, keine Metadatenzeile; der Split
            // ist (durch die simulierte parallele Operation) weg, die
            // Buchung selbst unverändert.
            await AssertConsistentTerminalStateAsync(
                db, bookingId,
                expectedAttachments: [],
                expectedSplitIds: ["sp-2"],
                expectedBaseRevision: 1);
        }
    }

    /// <summary>
    /// Review 7a, Befund 3 (Race B, deterministisch): Befehls-
    /// Interceptor, der beim ersten <c>"Attachments"</c>-Select — der
    /// ReplaceSplits-Lesung (Autocommit-Lese, noch vor As
    /// Update-Transaktion; davor liest <c>CorrectionService</c>
    /// Attachments nicht) — über eine zweite SQLite-Verbindung einen
    /// gültigen Beleg an 'sp-1' anlegt und committet. Empirisch
    /// verifiziert (21.10.): A's Lese-SELECTs sehen jeweils den
    /// jeweils letzten committierten Zustand (kein über mehrere
    /// Aussagen haltender Read-Snapshot) — die Lese sieht den Beleg
    /// und <c>ReplaceSplits</c> hängt ihn an die Gesamtbuchung um
    /// (Erhaltungsregel, D1b); der Versuch committet im ersten
    /// Anlauf ohne FK-Fehler. (Der FK-Fehler-Retry in
    /// <c>BookingRepository.UpdateAsync</c> ist verteidigende
    /// Tiefe: unter Sqlites Schreib-Lock-Serialisierung — die
    /// Transaktion hält den Lock bereits vor der ersten DML — kann
    /// kein paralleler Beleg-Commit zwischen As Lese und As Commit
    /// landen; das Produktions-Race endet stattdessen in dieser
    /// Umlageung oder im Upload-FK-Fehler (Race A, p3).)
    /// </summary>
    private sealed class StaleAttachmentInjector(
        string connectionString, string attachmentsDir, string bookingId, string splitId)
        : IDbCommandInterceptor
    {
        private readonly string _storedName =
            "0123456789abcdef0123456789abcdef.pdf";

        private int _injected;
        private volatile bool _armed;

        public void Arm() => _armed = true;

        private InterceptionResult<DbDataReader> MaybeInject(
            DbCommand command, InterceptionResult<DbDataReader> result)
        {
            if (_armed
                && Volatile.Read(ref _injected) == 0
                && command.CommandText.Contains("\"Attachments\"", StringComparison.Ordinal))
            {
                if (Interlocked.CompareExchange(ref _injected, 1, 0) == 0)
                {
                    Directory.CreateDirectory(attachmentsDir);
                    File.WriteAllBytes(
                        System.IO.Path.Combine(attachmentsDir, _storedName), Pdf);
                    using var connection = new SqliteConnection(connectionString);
                    connection.Open();
                    using var insert = connection.CreateCommand();
                    insert.CommandText =
                        "INSERT INTO \"Attachments\" " +
                        "(Id, OriginalFilename, ContentType, SizeBytes, Sha256, StoredName, " +
                        " BookingId, SplitId, OriginalSplitId, CreatedAtUtc) " +
                        "VALUES ('stale0123456789abcdef0123456789', 'quittung.pdf', " +
                        "'application/pdf', 10, " +
                        "'0000000000000000000000000000000000000000000000000000000000000000', " +
                        $"'{_storedName}', '{bookingId}', '{splitId}', NULL, " +
                        $"'{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}');";
                    insert.ExecuteNonQuery();
                }
            }
            return result;
        }

        public InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
            => MaybeInject(command, result);

        public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken)
            => new(MaybeInject(command, result));
    }

    /// <summary>
    /// Review 7a-2, Befund 4 (P1, deterministisch): Der erste
    /// <c>SELECT</c>, der <c>"Attachments"</c> liest — die
    /// <c>ReplaceSplits</c>-Abfrage (Autocommit-Lese, noch vor As
    /// Update-Transaktion; A hat bis dahin nur gelesen und hält
    /// keinen Schreiblock — die Update-Transaktion lockt erst zu
    /// Beginn von <c>SaveChanges</c>, empirisch verifiziert 21.10.) — committet über eine zweite SQLite-Verbindung
    /// eine echte parallele Korrektur B (Note + Basisrevision 2 +
    /// Historienzeile) mit ihrem eigenen CAS auf Basisrevision 1.
    /// A hat die Buchung vor Bs Commit mit Basisrevision 1 geladen;
    /// ihr Commit muss dann als sichtbarer Revisionskonflikt (409)
    /// scheitern — statt B stillschweigend zu übersteuern (lost
    /// update). Empirisch verifiziert (21.10.): Autocommit-Reads
    /// sehen jeweils den letzten committierten Zustand, und As
    /// Update-Transaktion akquiriert den Schreiblock bereits zu
    /// Beginn von <c>SaveChanges</c> (vor der ersten DML) — daher
    /// ist die Lese-Phase der einzige Injektionspunkt, an dem B
    /// ohne Blockierung committieren kann.
    /// </summary>
    private sealed class MidFlightRevisionCommitter(
        string connectionString, string bookingId) : IDbCommandInterceptor
    {
        private int _committed;
        private volatile bool _armed;

        public void Arm() => _armed = true;

        public InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            MaybeCommit(command);
            return result;
        }

        public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken)
        {
            MaybeCommit(command);
            return new(result);
        }

        private void MaybeCommit(DbCommand command)
        {
            if (!_armed
                || !command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                || !command.CommandText.Contains("\"Attachments\"", StringComparison.Ordinal)
                || Interlocked.CompareExchange(ref _committed, 1, 0) != 0)
            {
                return;
            }

            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var update = connection.CreateCommand();
            update.CommandText =
                "UPDATE \"Bookings\" SET \"Note\" = 'B-Notiz', \"BaseRevision\" = 2 " +
                $"WHERE \"Id\" = '{bookingId}' AND \"BaseRevision\" = 1;";
            update.ExecuteNonQuery();
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO \"BookingCorrections\" " +
                "(OperationId, BookingId, OldSnapshot, NewSnapshot, Note, Payload, " +
                " CreatedAtUtc) " +
                $"VALUES ('op-corr-b', '{bookingId}', '{{}}', '{{}}', 'B-Notiz', '{{}}', " +
                $"'{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}');";
            insert.ExecuteNonQuery();
        }
    }

    [Fact(DisplayName = "p5: deterministisch — parallele Korrektur zwischen Laden und Commit → 409 statt stiller Übersteuerung (Befund 4)")]
    public async Task ParallelCorrection_CommittedBetweenLoadAndSaveChanges_Fails_As_Visible_Conflict()
    {
        var (db, bookingId, _) = await SeedAsync();
        await using (db)
        {
            // Zweite AddDbContext-Registrierung: Optionen akkumulieren
            // (EF Core 10, empirisch verifiziert) — der Interceptor
            // ergänzt die SQLite-Optionen, der Rest kommt aus AddLarioData.
            var committer = new MidFlightRevisionCommitter(
                $"Data Source={db.DatabaseFile}", bookingId);
            var services = new ServiceCollection();
            services.AddLarioData($"Data Source={db.DatabaseFile}", db.AttachmentsDir);
            services.AddDbContext<LarioDbContext>(o => o.AddInterceptors(committer));
            var provider = services.BuildServiceProvider();
            await using (provider)
            {
                committer.Arm();
                using var scope = provider.CreateScope();

                // A = Split-Entfernung (Basisrevision 1); B (vom
                // Interceptor) commitet die Basisrevision 2 zwischen
                // A's Laden und A's Commit.
                var correction = await RunCorrectionAsync(
                    scope.ServiceProvider, RemovalSpec(bookingId, "op-corr-race", 1));

                // A scheitert als sichtbarer Revisionskonflikt (409) —
                // kein interner Fehler (kein DbUpdateException), und
                // auch keine stille Übersteuerung von B (lost update).
                Assert.False(correction.Ok);
                Assert.Contains(
                    "DomainConflict", correction.Error, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    "DbUpdate", correction.Error, StringComparison.OrdinalIgnoreCase);

                // Bs Inhalt, Revision, Historie und Splits bleiben
                // vollständig erhalten — von A wird nichts teilweise
                // angelegt (sp-1 ist nicht entfernt, Note und Revision
                // stammen von B, genau eine Historienzeile = B).
                var booking = await db.Resolve<IBookingRepository>().FindByIdAsync(bookingId);
                Assert.NotNull(booking);
                Assert.Equal("B-Notiz", booking!.Note);
                Assert.Equal(2, booking.BaseRevision);
                Assert.Equal(
                    ["sp-1", "sp-2"],
                    booking.Splits.Select(s => s.Id).OrderBy(s => s, StringComparer.Ordinal).ToArray());

                // A's Korrekturzeile ist durch die Rückrollung weg,
                // Bs bleibt.
                await using var history = new SqliteConnection($"Data Source={db.DatabaseFile}");
                await history.OpenAsync();
                using var operationQuery = history.CreateCommand();
                operationQuery.CommandText =
                    "SELECT OperationId FROM \"BookingCorrections\" " +
                    $"WHERE BookingId = '{bookingId}' ORDER BY OperationId;";
                using var operationReader = await operationQuery.ExecuteReaderAsync();
                var operations = new List<string>();
                while (await operationReader.ReadAsync())
                {
                    operations.Add(operationReader.GetString(0));
                }
                Assert.Equal(["op-corr-b"], operations);

                // Beleg-/Datei-Konsistenz des abgewiesenen A: keine
                // Zeile, keine Datei (A hat hier keine Belege angefasst).
                await AssertConsistentTerminalStateAsync(
                    db, bookingId,
                    expectedAttachments: [],
                    expectedSplitIds: ["sp-1", "sp-2"],
                    expectedBaseRevision: 2);
            }
        }
    }

    [Fact(DisplayName = "p4: deterministisch Race B — Beleg an entfallenden Split zwischen Lese- und Commit → Umlageung statt FK-Fehlern (Befund 3)")]
    public async Task RaceB_AttachmentLandsBetweenReadAndCommit_Correction_Relayouts()
    {
        var (db, bookingId, splitId) = await SeedAsync();
        await using (db)
        {
            // Zweite AddDbContext-Registrierung: Optionen akkumulieren
            // (EF Core 10, empirisch verifiziert) — der Interceptor
            // ergänzt die SQLite-Optionen, der Rest kommt aus AddLarioData.
            var injector = new StaleAttachmentInjector(
                $"Data Source={db.DatabaseFile}", db.AttachmentsDir, bookingId, splitId);
            var services = new ServiceCollection();
            services.AddLarioData($"Data Source={db.DatabaseFile}", db.AttachmentsDir);
            services.AddDbContext<LarioDbContext>(o => o.AddInterceptors(injector));
            var provider = services.BuildServiceProvider();
            await using (provider)
            {
                injector.Arm();
                using var scope = provider.CreateScope();
                var sp = scope.ServiceProvider;

                var correction = await RunCorrectionAsync(
                    sp, RemovalSpec(bookingId, "op-corr-race", 1));

                // Die Korrektur hat den FK-Konflikt intern aufgelöst
                // (frischer Lesezustand, Umlageung) — kein ungehandelter
                // DbUpdateException, kein sichtbarer Konflikt.
                Assert.True(correction.Ok,
                    $"Korrektur fehlgeschlagen (Race B nicht beherrscht): {correction.Error}");

                await AssertConsistentTerminalStateAsync(
                    db, bookingId,
                    expectedAttachments: [(null, "sp-1")],
                    expectedSplitIds: ["sp-2"],
                    expectedBaseRevision: 2);
            }
        }
    }
}
