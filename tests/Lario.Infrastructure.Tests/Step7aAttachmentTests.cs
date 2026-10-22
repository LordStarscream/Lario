using System.Security.Cryptography;
using Lario.Application;
using Lario.Application.Attachments;
using Lario.Application.Journal;
using Lario.Domain;
using Lario.Domain.Attachments;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;
using Lario.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Lario.Infrastructure.Tests;

/// <summary>
/// Schritt 7a (Batch A, Testplan h): <see cref="SqliteAttachmentFileStore"/>
/// — atomare Schreibweise (.part → Fsync → Rename), exakte Größenzählung
/// mit Fail-Fast, strenge Namensschema-Prüfung (keine Path-Traversal),
/// fehlende Dateien als Zustand statt Fehler, Listenfilterung.
/// </summary>
public sealed class Step7aAttachmentFileStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteAttachmentFileStore _store;

    public Step7aAttachmentFileStoreTests()
    {
        _dir = System.IO.Directory.CreateTempSubdirectory("lario-attach-").FullName;
        _store = new SqliteAttachmentFileStore(_dir);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static byte[] Content(int length, byte seed = 7)
    {
        var bytes = new byte[length];
        var x = (uint)seed;
        for (var i = 0; i < length; i++)
        {
            x = x * 1103515245 + 12345;
            bytes[i] = (byte)(x >> 16);
        }
        return bytes;
    }

    private static string ValidName(string extension = ".pdf")
        => new string('a', 32) + extension;

    public void Dispose() => System.IO.Directory.Delete(_dir, recursive: true);

    [Fact(DisplayName = "h1: Write persistiert Datei atomar (kein .part) mit korrekter Größe, SHA-256 und Header")]
    public async Task Write_Persists_Atomic_With_Correct_Hash_And_Header()
    {
        var content = Content(100_000);
        var result = await _store.WriteAsync(ValidName(), new MemoryStream(content), maxBytes: 1_000_000);

        var finalPath = Path.Combine(_dir, ValidName());
        Assert.True(File.Exists(finalPath));
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
        Assert.Equal(content.Length, result.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), result.Sha256);
        Assert.Equal(content.AsSpan(0, 16).ToArray(), result.HeaderBytes);
        Assert.Equal(content, await File.ReadAllBytesAsync(finalPath));
    }

    [Fact(DisplayName = "h2: Write über dem Limit wirft und räumt Teil-Datei auf (keine Waise)")]
    public async Task Write_Over_Limit_Throws_And_Cleans_Up()
    {
        var ex = await Assert.ThrowsAsync<AttachmentTooLargeException>(() =>
            _store.WriteAsync(ValidName(), new MemoryStream(Content(2_000)), maxBytes: 1_000));

        Assert.Equal(1_000, ex.MaxBytes);
        Assert.True(ex.SizeBytes > 1_000);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact(DisplayName = "h3: Write akzeptiert exakt die Limitgröße und lehnt Limit+1 ab")]
    public async Task Write_At_Exact_Limit_Succeeds_One_Byte_More_Fails()
    {
        var result = await _store.WriteAsync(ValidName(), new MemoryStream(Content(1_000)), maxBytes: 1_000);
        Assert.Equal(1_000, result.SizeBytes);

        await _store.DeleteAsync(ValidName());
        await Assert.ThrowsAsync<AttachmentTooLargeException>(() =>
            _store.WriteAsync(ValidName(), new MemoryStream(Content(1_001)), maxBytes: 1_000));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact(DisplayName = "h4: Write mit leerer Datei liefert leeren Header und SHA-256 der leeren Eingabe")]
    public async Task Write_Empty_File_Works()
    {
        var result = await _store.WriteAsync(ValidName(), Stream.Null, maxBytes: 1_000);

        Assert.Equal(0, result.SizeBytes);
        Assert.Empty(result.HeaderBytes);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", result.Sha256);
    }

    [Theory(DisplayName = "h5: Write lehnt Namen außerhalb des Lario-Schemas ab (keine Path-Traversal)")]
    [InlineData("../../evil.pdf")]
    [InlineData("../evil.pdf")]
    [InlineData("UPPER.PDF")]
    [InlineData("abc.txt")]
    [InlineData("a.pdf")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.pdf")] // 33 hex
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.pdf")]  // 31 hex
    public async Task Write_Rejects_Unclean_Names(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.WriteAsync(name, Stream.Null, maxBytes: 1_000));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact(DisplayName = "h6: Read fehlende Datei liefert Missing statt Fehler")]
    public async Task Read_Missing_File_Returns_Missing()
    {
        var result = await _store.ReadAsync(ValidName());
        Assert.False(result.Exists);
        Assert.Null(result.Content);
    }

    [Fact(DisplayName = "h7: Read vorhandene Datei liefert Inhalt und exakte Größe")]
    public async Task Read_Existing_File_Returns_Content_And_Size()
    {
        var content = Content(5_000);
        await _store.WriteAsync(ValidName(".jpg"), new MemoryStream(content), maxBytes: 1_000_000);

        var result = await _store.ReadAsync(ValidName(".jpg"));
        Assert.True(result.Exists);
        Assert.Equal(content.Length, result.SizeBytes);
        Assert.Equal(content, await ReadAllAsync(result.Content!));
        result.Content!.Dispose();
    }

    [Fact(DisplayName = "h8: Delete löscht finale und .part-Dateien; fehlende Datei ist Erfolg; fremde Namen werfen")]
    public async Task Delete_Semantics()
    {
        await _store.WriteAsync(ValidName(), Stream.Null, maxBytes: 100);
        await _store.WriteAsync(ValidName(".png"), Stream.Null, maxBytes: 100);
        var partName = ValidName(".jpg") + ".part";
        await File.WriteAllTextAsync(Path.Combine(_dir, partName), "rest");

        await _store.DeleteAsync(ValidName());
        await _store.DeleteAsync(ValidName(".png"));
        await _store.DeleteAsync(ValidName(".jpg") + ".part");
        Assert.Empty(Directory.GetFiles(_dir));

        // Fehlende Datei ist Erfolg (D5).
        await _store.DeleteAsync(ValidName());

        await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteAsync("../../evil.pdf"));
    }

    [Fact(DisplayName = "h9: ListStoredFiles liefert nur Lario-Dateien (finale + .part), sortiert")]
    public async Task List_Only_Returns_Lario_Names()
    {
        var nameA = new string('b', 32) + ".pdf";
        var nameB = new string('a', 32) + ".jpg.part";
        await _store.WriteAsync(nameA, Stream.Null, maxBytes: 100);
        await _store.WriteAsync(nameB[..^5], Stream.Null, maxBytes: 100);
        await File.WriteAllTextAsync(Path.Combine(_dir, nameB), "rest");
        await File.WriteAllTextAsync(Path.Combine(_dir, "random.txt"), "fremd");
        await File.WriteAllTextAsync(Path.Combine(_dir, "UPPER.PDF"), "fremd");

        var names = await _store.ListStoredFilesAsync();
        // Sortiert: finale 'a…jpg' vor 'a…jpg.part', dann 'b…pdf'.
        Assert.Equal([nameB[..^5], nameB, nameA], names);
    }
}

/// <summary>
/// Schritt 7a (Batch A): <see cref="AttachmentService"/> — Upload-Flow
/// (Typ, Größe, Magic-Bytes, Referenz, Datei-Entfernung bei Verwerfung),
/// Download, Löschen in der Reihenfolge Datei → Metadaten (D5) und die
/// Umlageung bei Split-Entfernung (D1b). Echte SQLite-Datenbank.
/// </summary>
public sealed class Step7aAttachmentServiceTests
{
    private static readonly byte[] Pdf =
    [
        0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, // %PDF-1.4
        0x0A, 0x25, 0x45, 0x6F, 0x66, 0x20, 0x74, 0x65, 0x73, 0x74, 0x0A, // %Eof test
        0x00, 0x01, 0x02, 0x03,
    ];

    private static readonly byte[] Jpeg =
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00,
    ];

    private static readonly byte[] Png =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00,
    ];

    private static async Task<(TestDatabase Db, string BookingId, string SplitId)> SeedExpenseWithSplitAsync()
    {
        var db = new TestDatabase();
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.CreateAccountAsync("acc-haupt", "Hauptkonto", AccountType.Bank);
        await admin.CreatePoolAsync("pool-flex", "Flex");
        await admin.CreateCategoryAsync("cat-wohnen", "Wohnen");
        await admin.CreateCategoryAsync("cat-miete", "Miete", parentId: "cat-wohnen");

        var expense = db.Resolve<ExpenseService>();
        var spec = new ExpenseSpec
        {
            Id = "aus-2026-10",
            Date = new DateOnly(2026, 10, 5),
            AccountId = "acc-haupt",
            TotalAmount = new Money(10_000),
            Splits =
            [
                new ExpenseSplitSpec
                {
                    Id = "sp-1",
                    Amount = new Money(6_000),
                    PoolId = "pool-flex",
                    CategoryId = "cat-miete",
                },
                new ExpenseSplitSpec
                {
                    Id = "sp-2",
                    Amount = new Money(4_000),
                    PoolId = "pool-flex",
                    CategoryId = "cat-miete",
                },
            ],
        };
        await expense.CreateAsync(spec);
        return (db, spec.Id, "sp-1");
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static AttachmentService Service(TestDatabase db) => db.Resolve<AttachmentService>();

    private static IAttachmentFileStore FileStore(TestDatabase db) => db.Resolve<IAttachmentFileStore>();

    private static async Task<int> FileCountAsync(TestDatabase db)
        => (await FileStore(db).ListStoredFilesAsync()).Count;

    [Fact(DisplayName = "a1: Upload PDF mit gültiger Buchung und Split speichert Datei und Metadaten")]
    public async Task Upload_Valid_Pdf_Persists_File_And_Metadata()
    {
        var (db, _, splitId) = await SeedExpenseWithSplitAsync();
        await using (db)
        {
            var attachment = await Service(db).UploadAsync(
                "quittung.pdf", "application/pdf", new MemoryStream(Pdf),
                bookingId: "aus-2026-10",
                splitId: splitId, maxBytes: 10_000);

            Assert.EndsWith(".pdf", attachment.StoredName);
            Assert.Equal("application/pdf", attachment.ContentType);
            Assert.Equal(Pdf.Length, attachment.SizeBytes);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Pdf)).ToLowerInvariant(), attachment.Sha256);
            Assert.Equal("quittung.pdf", attachment.OriginalFilename);
            Assert.Equal(splitId, attachment.SplitId);
            Assert.Equal("aus-2026-10", attachment.BookingId);
            Assert.Equal(1, await FileCountAsync(db));
            Assert.NotNull(await db.Resolve<IAttachmentRepository>().FindByIdAsync(attachment.Id));
        }
    }

    [Fact(DisplayName = "a2: Upload ohne Split belegt die Gesamtbuchung (SplitId null)")]
    public async Task Upload_Without_Split_References_Whole_Booking()
    {
        var (db, bookingId, _) = await SeedExpenseWithSplitAsync();
        await using (db)
        {
            var attachment = await Service(db).UploadAsync(
                "beleg.png", "image/png", new MemoryStream(Png), bookingId, splitId: null, maxBytes: 10_000);

            Assert.Null(attachment.SplitId);
            Assert.Null(attachment.OriginalSplitId);
            Assert.EndsWith(".png", attachment.StoredName);
        }
    }

    [Fact(DisplayName = "a3: Upload mit Content-Type-Parametern wird normalisiert (z. B. ; name=...)")]
    public async Task Upload_Normalizes_Content_Type_Parameters()
    {
        var (db, bookingId, _) = await SeedExpenseWithSplitAsync();
        await using (db)
        {
            var attachment = await Service(db).UploadAsync(
                "b.jpg", "image/jpeg; name=\"x\"", new MemoryStream(Jpeg), bookingId, null, maxBytes: 10_000);

            Assert.Equal("image/jpeg", attachment.ContentType);
            Assert.EndsWith(".jpg", attachment.StoredName);
        }
    }

    [Fact(DisplayName = "a4: Upload mit unerlaubtem Typ wird verworfen — keine Datei, keine Metadaten")]
    public async Task Upload_Unsupported_Type_Leaves_No_Trace()
    {
        var (db, bookingId, _) = await SeedExpenseWithSplitAsync();
        await using (db)
        {
            await Assert.ThrowsAsync<UnsupportedAttachmentTypeException>(() =>
                Service(db).UploadAsync("doc.txt", "text/plain", Stream.Null, bookingId, null, maxBytes: 10_000));

            Assert.Equal(0, await FileCountAsync(db));
            Assert.Empty(await db.Resolve<IAttachmentRepository>().GetAllAsync());
        }
    }

    [Fact(DisplayName = "a5: Upload mit leerem/null Content-Type wird verworfen")]
    public async Task Upload_Empty_Content_Type_Leaves_No_Trace()
    {
        var (db, bookingId, _) = await SeedExpenseWithSplitAsync();
        await using (db)
        {
            await Assert.ThrowsAsync<UnsupportedAttachmentTypeException>(() =>
                Service(db).UploadAsync("x.pdf", null!, new MemoryStream(Pdf), bookingId, null, maxBytes: 10_000));
            Assert.Equal(0, await FileCountAsync(db));
        }
    }

    [Fact(DisplayName = "a6: Magic-Bytes passen nicht zum deklarierten Typ → verworfen, Datei wird entfernt")]
    public async Task Upload_Magic_Mismatch_Deletes_File()
    {
        var (db, bookingId, _) = await SeedExpenseWithSplitAsync();
        await using (db)
        {
            // Deklariert PDF, Inhalt ist JPEG.
            await Assert.ThrowsAsync<UnsupportedAttachmentTypeException>(() =>
                Service(db).UploadAsync("falsch.pdf", "application/pdf", new MemoryStream(Jpeg),
                    bookingId, null, maxBytes: 10_000));

            Assert.Equal(0, await FileCountAsync(db));
            Assert.Empty(await db.Resolve<IAttachmentRepository>().GetAllAsync());
        }
    }

    [Fact(DisplayName = "a7: Upload mit unbekannter Buchung → 400-Exception, Datei wird entfernt")]
    public async Task Upload_Unknown_Booking_Deletes_File()
    {
        var (db, _, _) = await SeedExpenseWithSplitAsync();
        await using (db)
        {
            await Assert.ThrowsAsync<InvalidReferenceException>(() =>
                Service(db).UploadAsync("x.pdf", "application/pdf", new MemoryStream(Pdf),
                    "unbekannt", null, maxBytes: 10_000));

            Assert.Equal(0, await FileCountAsync(db));
            Assert.Empty(await db.Resolve<IAttachmentRepository>().GetAllAsync());
        }
    }

    [Fact(DisplayName = "a8: Upload mit Split, der nicht zur Buchung gehört → 400-Exception, Datei wird entfernt")]
    public async Task Upload_Foreign_Split_Deletes_File()
    {
        var (db, bookingId, _) = await SeedExpenseWithSplitAsync();
        await using (db)
        {
            await Assert.ThrowsAsync<InvalidReferenceException>(() =>
                Service(db).UploadAsync("x.pdf", "application/pdf", new MemoryStream(Pdf),
                    bookingId, "fremder-split", maxBytes: 10_000));

            Assert.Equal(0, await FileCountAsync(db));
        }
    }

    [Fact(DisplayName = "a9: Upload über dem Größenlimit → AttachmentTooLargeException, keine Datei")]
    public async Task Upload_Too_Large_Leaves_No_File()
    {
        var (db, bookingId, _) = await SeedExpenseWithSplitAsync();
        await using (db)
        {
            await Assert.ThrowsAsync<AttachmentTooLargeException>(() =>
                Service(db).UploadAsync("gross.pdf", "application/pdf", new MemoryStream(Pdf),
                    bookingId, null, maxBytes: Pdf.Length - 1));

            Assert.Equal(0, await FileCountAsync(db));
        }
    }

    [Fact(DisplayName = "a10: Download liefert Metadaten + Datei; unbekannt (null,null); Datei fehlt → Missing")]
    public async Task Download_Cases()
    {
        var (db, bookingId, _) = await SeedExpenseWithSplitAsync();
        await using (db)
        {
            var service = Service(db);
            var attachment = await service.UploadAsync(
                "x.jpg", "image/jpeg", new MemoryStream(Jpeg), bookingId, null, maxBytes: 10_000);

            var (metadata, file) = await service.DownloadAsync(attachment.Id);
            Assert.NotNull(metadata);
            Assert.NotNull(file);
            Assert.True(file!.Exists);
            Assert.Equal(Jpeg, await ReadAllAsync(file.Content!));
            file.Content!.Dispose();

            var (unknownMeta, unknownFile) = await service.DownloadAsync("unbekannt");
            Assert.Null(unknownMeta);
            Assert.Null(unknownFile);

            // Datei wird hinter den Metadaten entfernt (Crash-Szenario D9b):
            await FileStore(db).DeleteAsync(attachment.StoredName);
            var (metadata2, file2) = await service.DownloadAsync(attachment.Id);
            Assert.NotNull(metadata2);
            Assert.False(file2!.Exists);
        }
    }

    [Fact(DisplayName = "a11: Löschen entfernt Datei und Metadaten; die Buchung bleibt unverändert")]
    public async Task Delete_Removes_File_And_Metadata_Booking_Intact()
    {
        var (db, bookingId, _) = await SeedExpenseWithSplitAsync();
        await using (db)
        {
            var service = Service(db);
            var before = await db.Resolve<IBookingRepository>().FindByIdAsync(bookingId);
            var attachment = await service.UploadAsync(
                "x.pdf", "application/pdf", new MemoryStream(Pdf), bookingId, null, maxBytes: 10_000);
            Assert.Equal(1, await FileCountAsync(db));

            var deleted = await service.DeleteAsync(attachment.Id);
            Assert.NotNull(deleted);
            Assert.Equal(0, await FileCountAsync(db));
            Assert.Null(await db.Resolve<IAttachmentRepository>().FindByIdAsync(attachment.Id));

            var after = await db.Resolve<IBookingRepository>().FindByIdAsync(bookingId);
            Assert.NotNull(after);
            Assert.Equal(before!.BaseRevision, after.BaseRevision);
            Assert.Equal(before.Splits.Count, after.Splits.Count);
        }
    }

    [Fact(DisplayName = "a12: Löschen mit unbekannter Id liefert null; mit fehlender Datei dennoch Erfolg (D5)")]
    public async Task Delete_Unknown_And_Missing_File()
    {
        var (db, bookingId, _) = await SeedExpenseWithSplitAsync();
        await using (db)
        {
            var service = Service(db);
            Assert.Null(await service.DeleteAsync("unbekannt"));

            var attachment = await service.UploadAsync(
                "x.pdf", "application/pdf", new MemoryStream(Pdf), bookingId, null, maxBytes: 10_000);
            await FileStore(db).DeleteAsync(attachment.StoredName);

            // Datei fehlt bereits — das Löschen darf trotzdem gelingen.
            Assert.NotNull(await service.DeleteAsync(attachment.Id));
            Assert.Null(await db.Resolve<IAttachmentRepository>().FindByIdAsync(attachment.Id));
        }
    }

    [Fact(DisplayName = "a13: ListByBooking liefert nur die Belege der Buchung")]
    public async Task ListByBooking_Filters_By_Booking()
    {
        var (db, bookingId, _) = await SeedExpenseWithSplitAsync();
        await using (db)
        {
            var service = Service(db);
            var a1 = await service.UploadAsync("a.pdf", "application/pdf", new MemoryStream(Pdf), bookingId, null, maxBytes: 10_000);
            var a2 = await service.UploadAsync("b.png", "image/png", new MemoryStream(Png), bookingId, null, maxBytes: 10_000);

            var list = await service.ListByBookingAsync(bookingId);
            Assert.Equal([a1.Id, a2.Id], list.Select(l => l.Id));
            Assert.Empty(await service.ListByBookingAsync("andere-buchung"));
        }
    }

    [Fact(DisplayName = "a15: gleiche Split-Id in zwei Buchungen → beide Zuordnungen unabhängig gültig (D9 f1)")]
    public async Task Same_Split_Id_In_Two_Bookings_Both_Valid()
    {
        var (db, bookingA, bookingB) = await SeedTwoExpensesWithSameSplitAsync();
        await using (db)
        {
            var service = Service(db);
            var upA = await service.UploadAsync(
                "a.pdf", "application/pdf", new MemoryStream(Pdf), bookingA, "sp-1", maxBytes: 10_000);
            var upB = await service.UploadAsync(
                "b.pdf", "application/pdf", new MemoryStream(Pdf), bookingB, "sp-1", maxBytes: 10_000);

            // Beide Buchungen tragen einen Split 'sp-1'; die Komposit-FK
            // (BookingId, SplitId) macht die Zuordnungen unabhängig gültig.
            Assert.Equal(bookingA, upA.BookingId);
            Assert.Equal("sp-1", upA.SplitId);
            Assert.Equal(bookingB, upB.BookingId);
            Assert.Equal("sp-1", upB.SplitId);
            Assert.Equal(2, await FileCountAsync(db));
            Assert.Equal(2, (await db.Resolve<IAttachmentRepository>().GetAllAsync()).Count);
        }
    }

    [Fact(DisplayName = "a16: Split existiert nur in anderer Buchung → Ablehnung ohne Datei und Zeile (D9 f2)")]
    public async Task Split_From_Other_Booking_Rejected_No_File_No_Row()
    {
        var (db, bookingA, _) = await SeedTwoExpensesWithSameSplitAsync();
        await using (db)
        {
            // 'only-b' ist ein Split der zweiten Buchung, nicht von 'aus-a'.
            var ex = await Assert.ThrowsAsync<InvalidReferenceException>(() =>
                Service(db).UploadAsync(
                    "fremd.pdf", "application/pdf", new MemoryStream(Pdf), bookingA, "only-b", maxBytes: 10_000));
            Assert.Contains("only-b", ex.Message);
            Assert.Equal(0, await FileCountAsync(db));
            Assert.Empty(await db.Resolve<IAttachmentRepository>().GetAllAsync());
        }
    }

    [Fact(DisplayName = "a17: Korrektur entfernt belegten Split → Umlageung in derselben Transaktion (D1b, D9 g)")]
    public async Task Correction_Removes_Booked_Split_Reassigns_Attachment()
    {
        var (db, bookingId, splitId) = await SeedExpenseWithSplitAsync();
        await using (db)
        {
            var attachment = await Service(db).UploadAsync(
                "quittung.pdf", "application/pdf", new MemoryStream(Pdf), bookingId, splitId, maxBytes: 10_000);

            // Korrektur entfernt sp-1 (Split-Summe passt exakt zu neuem
            // Gesamtbetrag 4000); die Umlageung des Belegs muss in derselben
            // DB-Transaktion passieren wie das Split-Löschen (D1b).
            var result = await db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
                bookingId, "op-corr-a17", ExpectedBaseRevision: 1,
                TotalAmount: new Money(4_000),
                Splits:
                [
                    new ExpenseSplitSpec
                    {
                        Id = "sp-2",
                        Amount = new Money(4_000),
                        PoolId = "pool-flex",
                        CategoryId = "cat-miete",
                    },
                ]));
            Assert.True(result.Created);

            // Beleg wandert auf die Gesamtbuchung: SplitId null, die alte
            // Id bleibt in OriginalSplitId nachvollziehbar; Buchung, Name
            // und Datei bleiben unverändert.
            var updated = await db.Resolve<IAttachmentRepository>().FindByIdAsync(attachment.Id);
            Assert.NotNull(updated);
            Assert.Null(updated!.SplitId);
            Assert.Equal(splitId, updated.OriginalSplitId);
            Assert.Equal(bookingId, updated.BookingId);
            Assert.Equal("quittung.pdf", updated.OriginalFilename);

            var file = await FileStore(db).ReadAsync(updated.StoredName);
            Assert.True(file.Exists);
            Assert.Equal(Pdf, await ReadAllAsync(file.Content!));

            // Die Buchung trägt nur noch sp-2 (Konten/Pools ändern sich
            // ausschließlich gemäß der Korrektur).
            var booking = await db.Resolve<IBookingRepository>().FindByIdAsync(bookingId);
            Assert.NotNull(booking);
            Assert.Equal("sp-2", Assert.Single(booking!.Splits).Id);
        }
    }

    [Fact(DisplayName = "a18: In-Place-Korrektur belegten Splits (Betrag/Pool) lässt Beleg-Zuordnung gelten (D1b, D9 h)")]
    public async Task Correction_In_Place_Changes_Booked_Split_Keeps_Attachment()
    {
        var (db, bookingId, splitId) = await SeedExpenseWithSplitAsync();
        await using (db)
        {
            var attachment = await Service(db).UploadAsync(
                "quittung.pdf", "application/pdf", new MemoryStream(Pdf), bookingId, splitId, maxBytes: 10_000);

            // sp-1 wird in-place geändert (gleiche Id, neuer Betrag 6000 → 7000);
            // die Beleg-Zuordnung auf 'sp-1' bleibt gültig — keine Aktion nötig.
            var result = await db.Resolve<CorrectionService>().CorrectAsync(new CorrectionSpec(
                bookingId, "op-corr-a18", ExpectedBaseRevision: 1,
                TotalAmount: new Money(10_000),
                Splits:
                [
                    new ExpenseSplitSpec
                    {
                        Id = "sp-1",
                        Amount = new Money(7_000),
                        PoolId = "pool-flex",
                        CategoryId = "cat-miete",
                    },
                    new ExpenseSplitSpec
                    {
                        Id = "sp-2",
                        Amount = new Money(3_000),
                        PoolId = "pool-flex",
                        CategoryId = "cat-miete",
                    },
                ]));
            Assert.True(result.Created);

            var updated = await db.Resolve<IAttachmentRepository>().FindByIdAsync(attachment.Id);
            Assert.NotNull(updated);
            Assert.Equal(splitId, updated!.SplitId);
            Assert.Null(updated.OriginalSplitId);

            var file = await FileStore(db).ReadAsync(updated.StoredName);
            Assert.True(file.Exists);
            Assert.Equal(Pdf, await ReadAllAsync(file.Content!));

            var booking = await db.Resolve<IBookingRepository>().FindByIdAsync(bookingId);
            Assert.Equal(new Money(7_000), Assert.Single(booking!.Splits, s => s.Id == "sp-1").Amount);
        }
    }

    [Fact(DisplayName = "a19: DB-Commit schlägt nach Rename fehl → finale Datei wird gelöscht (D9 b)")]
    public async Task Upload_DbCommitFailure_Removes_Final_File()
    {
        var (db, bookingId, splitId) = await SeedExpenseWithSplitAsync();
        await using (db)
        {
            using var scope = db.Services.CreateScope();
            var provider = scope.ServiceProvider;
            var realRepo = provider.GetRequiredService<IAttachmentRepository>();
            var realStore = provider.GetRequiredService<IAttachmentFileStore>();
            var bookingRepo = provider.GetRequiredService<IBookingRepository>();
            var service = new AttachmentService(new FailingAddRepository(realRepo), realStore, bookingRepo);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.UploadAsync(
                "quittung.pdf", "application/pdf", new MemoryStream(Pdf),
                bookingId, splitId, maxBytes: 10_000));

            // Die finale Datei (nach dem Rename) ist bereinigt; das
            // Verzeichnis bleibt leer (keine Waise, keine .part-Datei).
            Assert.Empty(System.IO.Directory.GetFiles(db.AttachmentsDir));
            Assert.Empty(await realRepo.GetAllAsync());
        }
    }

    [Fact(DisplayName = "a20: Delete mit fehlgeschlagener Datei-Löschung → DB-Zeile weg, Datei bleibt Waise (D9 e)")]
    public async Task Delete_FileDeleteFailure_Row_Gone_File_Orphaned()
    {
        var (db, bookingId, _) = await SeedExpenseWithSplitAsync();
        await using (db)
        {
            var attachment = await Service(db).UploadAsync(
                "quittung.pdf", "application/pdf", new MemoryStream(Pdf), bookingId, null, maxBytes: 10_000);

            using var scope = db.Services.CreateScope();
            var provider = scope.ServiceProvider;
            var realRepo = provider.GetRequiredService<IAttachmentRepository>();
            var realStore = provider.GetRequiredService<IAttachmentFileStore>();
            var bookingRepo = provider.GetRequiredService<IBookingRepository>();
            var service = new AttachmentService(realRepo, new FailingDeleteFileStore(realStore), bookingRepo);

            // Die Löschung selbst gelingt (DB-Zeile zuerst committen, D5);
            // der Dateilösch-Fehler hebt sie nicht auf.
            var deleted = await service.DeleteAsync(attachment.Id);
            Assert.NotNull(deleted);
            Assert.Null(await realRepo.FindByIdAsync(attachment.Id));

            // Die Datei bleibt als Waise auf dem Datenträger…
            Assert.True(File.Exists(Path.Combine(db.AttachmentsDir, attachment.StoredName)));

            // …und wird beim nächsten Start-Abgleich entfernt (D2b).
            var reconcileService = Service(db);
            var result = await reconcileService.ReconcileAsync();
            Assert.Equal(1, result.OrphanedFilesDeleted);
            Assert.Empty(await db.Resolve<IAttachmentRepository>().GetAllAsync());
            Assert.Empty(System.IO.Directory.GetFiles(db.AttachmentsDir));
        }
    }

    private static async Task<(TestDatabase Db, string BookingA, string BookingB)> SeedTwoExpensesWithSameSplitAsync()
    {
        var db = new TestDatabase();
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.CreateAccountAsync("acc-haupt", "Hauptkonto", AccountType.Bank);
        await admin.CreatePoolAsync("pool-flex", "Flex");
        await admin.CreateCategoryAsync("cat-wohnen", "Wohnen");
        await admin.CreateCategoryAsync("cat-miete", "Miete", parentId: "cat-wohnen");

        var expense = db.Resolve<ExpenseService>();
        // Beide Buchungen tragen 'sp-1'; nur 'aus-b' trägt zusätzlich
        // 'only-b' (für Fall f2: Tupel muss zur konkreten Buchung passen).
        await expense.CreateAsync(new ExpenseSpec
        {
            Id = "aus-a",
            Date = new DateOnly(2026, 10, 5),
            AccountId = "acc-haupt",
            TotalAmount = new Money(10_000),
            Splits =
            [
                new ExpenseSplitSpec { Id = "sp-1", Amount = new Money(6_000), PoolId = "pool-flex", CategoryId = "cat-miete" },
                new ExpenseSplitSpec { Id = "sp-2", Amount = new Money(4_000), PoolId = "pool-flex", CategoryId = "cat-miete" },
            ],
        });
        await expense.CreateAsync(new ExpenseSpec
        {
            Id = "aus-b",
            Date = new DateOnly(2026, 10, 6),
            AccountId = "acc-haupt",
            TotalAmount = new Money(10_000),
            Splits =
            [
                new ExpenseSplitSpec { Id = "sp-1", Amount = new Money(6_000), PoolId = "pool-flex", CategoryId = "cat-miete" },
                new ExpenseSplitSpec { Id = "only-b", Amount = new Money(4_000), PoolId = "pool-flex", CategoryId = "cat-miete" },
            ],
        });
        return (db, "aus-a", "aus-b");
    }
}

/// <summary>
/// Repository-Double für Upload-Fehlerfall D9(b): <c>AddAsync</c> wirft
/// (simulierter DB-Commit-Fehler nach dem Datei-Rename); alle anderen
/// Operationen delegieren an das echte Repository.
/// </summary>
internal sealed class FailingAddRepository(IAttachmentRepository inner) : IAttachmentRepository
{
    public Task AddAsync(Attachment attachment, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Simulierter DB-Commit-Fehler (Design 7a D9 b).");

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

    public Task UpdateSplitAssignmentAsync(
        string id, string? splitId, DateTime updatedAtUtc, CancellationToken cancellationToken = default)
        => inner.UpdateSplitAssignmentAsync(id, splitId, updatedAtUtc, cancellationToken);
}

/// <summary>
/// FileStore-Double für Lösch-Fehlerfall D9(e): <c>DeleteAsync</c> wirft
/// (simulierter Datei-Lösch-Fehler); alle anderen Operationen delegieren
/// an den echten Store.
/// </summary>
internal sealed class FailingDeleteFileStore(IAttachmentFileStore inner) : IAttachmentFileStore
{
    public Task<AttachmentWriteResult> WriteAsync(
        string storedName, Stream content, long maxBytes, CancellationToken cancellationToken = default)
        => inner.WriteAsync(storedName, content, maxBytes, cancellationToken);

    public Task<AttachmentFileContent> ReadAsync(string storedName, CancellationToken cancellationToken = default)
        => inner.ReadAsync(storedName, cancellationToken);

    public Task DeleteAsync(string storedName, CancellationToken cancellationToken = default)
        => throw new IOException("Simulierter Datei-Lösch-Fehler (Design 7a D9 e).");

    public Task<IReadOnlyList<string>> ListStoredFilesAsync(CancellationToken cancellationToken = default)
        => inner.ListStoredFilesAsync(cancellationToken);
}

/// <summary>
/// Schritt 7a (Batch A, Testplan g): Start-Abgleich (D9b) — finale
/// Dateien ohne Metadatenzeile (Waisen) und offene .part-Dateien
/// (Abbruch-Reste) werden entfernt; Metadaten ohne Datei bleiben
/// (Download liefert später 410); verpaarte Belege bleiben unangetastet.
/// </summary>
public sealed class Step7aAttachmentReconcileTests
{
    private const string BookingId = "bk-reconcile";

    private static string ValidName(string hex, string extension) => hex + extension;

    /// <summary>Reale Buchung anlegen — die Attachment-Zeile hat einen FK darauf.</summary>
    private static async Task SeedBookingAsync(TestDatabase db)
    {
        var admin = db.Resolve<MasterDataAdmin>();
        await admin.CreateAccountAsync("acc-r", "Rechnung", AccountType.Bank);
        await admin.CreatePoolAsync("pool-r", "Pool");
        await admin.CreateCategoryAsync("cat-r", "Kategorie");
        await db.Resolve<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = BookingId,
            Date = new DateOnly(2026, 10, 1),
            AccountId = "acc-r",
            TotalAmount = new Money(1_000),
            Splits = [new ExpenseSplitSpec
            {
                Id = "sp-r",
                Amount = new Money(1_000),
                PoolId = "pool-r",
                CategoryId = "cat-r",
            }],
        });
    }

    private static async Task<Attachment> CreateMetadataPairAsync(
        TestDatabase db, string hex, string extension)
    {
        var storedName = ValidName(hex, extension);
        var result = await db.Resolve<IAttachmentFileStore>().WriteAsync(storedName, Stream.Null, maxBytes: 1_000_000);
        var attachment = new Attachment
        {
            Id = hex,
            OriginalFilename = "x" + extension,
            ContentType = "application/pdf",
            SizeBytes = result.SizeBytes,
            Sha256 = result.Sha256,
            StoredName = storedName,
            BookingId = BookingId,
            CreatedAtUtc = DateTime.UtcNow,
        };
        await db.Resolve<IAttachmentRepository>().AddAsync(attachment);
        return attachment;
    }

    private static async Task<string> CreateFinalFileOnlyAsync(TestDatabase db, string hex, string extension)
    {
        var name = ValidName(hex, extension);
        await db.Resolve<IAttachmentFileStore>().WriteAsync(name, Stream.Null, maxBytes: 1_000_000);
        return name;
    }

    private static async Task<string> CreatePartFileOnlyAsync(TestDatabase db, string hex, string extension)
    {
        var name = ValidName(hex, extension);
        Directory.CreateDirectory(db.AttachmentsDir);
        var partPath = Path.Combine(db.AttachmentsDir, name + ".part");
        await File.WriteAllBytesAsync(partPath, [0x01]);
        return name;
    }

    [Fact(DisplayName = "g1: Abgleich entfernt Waisen-Dateien und .part-Reste, behält verpaarte Belege")]
    public async Task Reconcile_Deletes_Orphans_And_Parts_Keeps_Pairs()
    {
        await using var db = new TestDatabase();
        await SeedBookingAsync(db);

        var kept = await CreateMetadataPairAsync(db, new string('1', 32), ".pdf");
        var orphan = await CreateFinalFileOnlyAsync(db, new string('2', 32), ".png");
        // Finale Waise + offener .part-Rest für denselben Namen.
        var partName = await CreateFinalFileOnlyAsync(db, new string('3', 32), ".jpg");
        await CreatePartFileOnlyAsync(db, new string('3', 32), ".jpg");
        var result = await db.Resolve<AttachmentService>().ReconcileAsync();

        Assert.Equal(2, result.OrphanedFilesDeleted); // finale Waisen (.png, .jpg)
        Assert.Equal(1, result.PartFilesDeleted);     // .jpg.part
        Assert.Equal(0, result.MetadataWithoutFile);

        Assert.True(File.Exists(Path.Combine(db.AttachmentsDir, kept.StoredName)));
        Assert.False(File.Exists(Path.Combine(db.AttachmentsDir, orphan)));
        Assert.False(File.Exists(Path.Combine(db.AttachmentsDir, partName + ".part")));
        Assert.NotNull(await db.Resolve<IAttachmentRepository>().FindByIdAsync(kept.Id));

        // Idempotenz: zweiter Lauf findet nichts mehr.
        var second = await db.Resolve<AttachmentService>().ReconcileAsync();
        Assert.Equal(0, second.OrphanedFilesDeleted);
        Assert.Equal(0, second.PartFilesDeleted);
    }

    [Fact(DisplayName = "g2: Abgleich behält Metadaten ohne Datei (Download liefert dann 410)")]
    public async Task Reconcile_Keeps_Metadata_Without_File()
    {
        await using var db = new TestDatabase();
        await SeedBookingAsync(db);
        var hex = new string('4', 32);
        var attachment = new Attachment
        {
            Id = hex,
            OriginalFilename = "x.pdf",
            ContentType = "application/pdf",
            SizeBytes = 0,
            Sha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            StoredName = ValidName(hex, ".pdf"),
            BookingId = BookingId,
            CreatedAtUtc = DateTime.UtcNow,
        };
        await db.Resolve<IAttachmentRepository>().AddAsync(attachment);
        // Bewusst keine Datei.

        var result = await db.Resolve<AttachmentService>().ReconcileAsync();
        Assert.Equal(0, result.OrphanedFilesDeleted);
        Assert.Equal(0, result.PartFilesDeleted);
        Assert.Equal(1, result.MetadataWithoutFile);
        Assert.NotNull(await db.Resolve<IAttachmentRepository>().FindByIdAsync(hex));
    }

    [Fact(DisplayName = "g3: Abgleich entfernt Waisen, die ein .part-Gegenstück haben, konsistent")]
    public async Task Reconcile_Handles_Final_And_Part_Together()
    {
        await using var db = new TestDatabase();
        var hex = new string('5', 32);
        // Zuerst die finale Datei; dann ein .part-Rest desselben Namens
        // (zweite Uploads starten, während die finale Datei liegt).
        var final = await CreateFinalFileOnlyAsync(db, hex, ".pdf");
        await CreatePartFileOnlyAsync(db, hex, ".pdf");

        var result = await db.Resolve<AttachmentService>().ReconcileAsync();
        Assert.Equal(1, result.OrphanedFilesDeleted); // finale Datei
        Assert.Equal(1, result.PartFilesDeleted);     // .part
        Assert.False(File.Exists(Path.Combine(db.AttachmentsDir, final)));
        Assert.False(File.Exists(Path.Combine(db.AttachmentsDir, final + ".part")));
    }
}
