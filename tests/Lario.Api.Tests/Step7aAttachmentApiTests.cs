using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lario.Infrastructure;

namespace Lario.Api.Tests;

/// <summary>
/// Schritt 7a (Batch B, Task B2): Beleg-Endpunkte über die echte
/// HTTP-Oberfläche (echter Host + SQLite) — Upload (Typ, Größe,
/// Magic-Bytes, Referenzen, Unicode-Dateinamen), Metadaten-/Download-
/// Endpunkte, 404/410, Löschung, Neustart-Persistenz und Auth (401).
/// Die fachlichen Fälle (f1/f2, j/k, l) aus dem Testplan werden hier
/// zusätzlich auf HTTP-Ebene abgesichert.
/// </summary>
public sealed class Step7aAttachmentApiTests : IDisposable
{
    private readonly LarioApiFactory _factory;
    private readonly HttpClient _client;
    private bool _masterDataSeeded;
    private string _accountId = string.Empty;
    private string _poolId = string.Empty;
    private string _categoryId = string.Empty;

    public Step7aAttachmentApiTests()
    {
        _factory = new LarioApiFactory();

        var login = LoginAsync(_factory.CreateClient()).GetAwaiter().GetResult();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.Token);
    }

    public void Dispose() => _factory.Dispose();

    // ---------- Grundfunktionen (B2 1–3) ----------

    [Theory]
    [InlineData("quittung.pdf", "application/pdf", "pdf")]
    [InlineData("foto.jpeg", "image/jpeg", "jpeg")]
    [InlineData("bild.png", "image/png", "png")]
    public async Task Upload_gueltiger_Typ_liefert_201_Metadaten_und_Datei(
        string filename, string contentType, string kind)
    {
        var content = kind switch
        {
            "pdf" => PdfBytes,
            "jpeg" => JpegBytes,
            _ => PngBytes,
        };
        var (bookingId, splitId) = await SeedExpenseAsync();

        var response = await UploadAsync(
            _client, filename, content, contentType, bookingId, splitId);
        response.EnsureSuccessStatusCode();
        var dto = (await response.Content.ReadFromJsonAsync<JsonElement>())!;

        Assert.Equal(filename, dto.GetProperty("originalFilename").GetString());
        Assert.Equal(contentType, dto.GetProperty("contentType").GetString());
        Assert.Equal(content.Length, dto.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(ComputeSha256Hex(content), dto.GetProperty("sha256").GetString());
        Assert.Equal(splitId, dto.GetProperty("splitId").GetString());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("originalSplitId").ValueKind);

        // Die Datei liegt unter dem Beleg-Verzeichnis des Hosts.
        var files = Directory.GetFiles(AttachmentsDir);
        Assert.Single(files);
        // Design D1: StoredName = {Id}{.pdf|.jpg|.png}.
        Assert.Matches("^[0-9a-f]{32}\\.(pdf|jpg|png)$", Path.GetFileName(files[0]));

        // Der Download liefert exakt die hochgeladenen Bytes (inline).
        using var download = await _client.GetAsync(
            $"/api/v1/attachments/{dto.GetProperty("id").GetString()}/download");
        download.EnsureSuccessStatusCode();
        var downloaded = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(content, downloaded);
        Assert.Equal("inline",
            download.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Fact]
    public async Task Neustart_Metadaten_und_Datei_erhalten()
    {
        var dir = Directory.CreateTempSubdirectory("lario-attach-restart-").FullName;
        try
        {
            // Erster Host auf frischer Ablage: Upload, dann Neustart.
            var firstFactory = new LarioApiFactory(dataDirectory: dir);
            var bookingId = await WithClientAsync(firstFactory, async client =>
            {
                var (bookingId, splitId) = await SeedExpenseWithMasterDataAsync(client);
                var response = await UploadAsync(
                    client, "quittung.pdf", PdfBytes, "application/pdf", bookingId, splitId);
                response.EnsureSuccessStatusCode();
                return bookingId;
            });
            firstFactory.Dispose();

            // Zweiter Host auf derselben Ablage: Metadaten und Datei
            // müssen vorhanden sein (Persistenz über Neustart).
            var secondFactory = new LarioApiFactory(dataDirectory: dir);
            try
            {
                var result = await WithClientAsync(secondFactory, async client =>
                {
                    using var list = await client.GetAsync(
                        $"/api/v1/bookings/{bookingId}/attachments");
                    list.EnsureSuccessStatusCode();
                    var items = (await list.Content.ReadFromJsonAsync<JsonElement>())!;
                    var single = Assert.Single(items.EnumerateArray());
                    var id = single.GetProperty("id").GetString()!;
                    Assert.Equal(ComputeSha256Hex(PdfBytes),
                        single.GetProperty("sha256").GetString());

                    using var download = await client.GetAsync(
                        $"/api/v1/attachments/{id}/download");
                    download.EnsureSuccessStatusCode();
                    return await download.Content.ReadAsByteArrayAsync();
                });
                Assert.Equal(PdfBytes, result);
            }
            finally
            {
                secondFactory.Dispose();
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Mehrere_Dateien_mit_und_ohne_Split_liste_korrekt()
    {
        var (bookingId, splitId) = await SeedExpenseAsync();

        (await UploadAsync(_client, "a.pdf", PdfBytes, "application/pdf", bookingId, splitId)).EnsureSuccessStatusCode();
        (await UploadAsync(_client, "b.png", PngBytes, "image/png", bookingId)).EnsureSuccessStatusCode();

        using var list = await _client.GetAsync($"/api/v1/bookings/{bookingId}/attachments");
        list.EnsureSuccessStatusCode();
        var items = (await list.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(2, items.GetArrayLength());

        var byFilename = items.EnumerateArray()
            .ToDictionary(i => i.GetProperty("originalFilename").GetString()!);
        Assert.Equal(splitId, byFilename["a.pdf"].GetProperty("splitId").GetString());
        Assert.Equal(JsonValueKind.Null, byFilename["b.png"].GetProperty("splitId").ValueKind);
    }

    // ---------- Ablehnungen (B2 4–7) ----------

    [Fact]
    public async Task Upload_falsche_MagicBytes_abgewiesen_keine_Waise()
    {
        var (bookingId, _) = await SeedExpenseAsync();
        var notAPdf = Encoding.ASCII.GetBytes("definitely not a pdf at all");

        var response = await UploadAsync(
            _client, "faelschung.pdf", notAPdf, "application/pdf", bookingId);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal("validation_error", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("unsupported_attachment_type",
            body.GetProperty("error").GetProperty("detailCode").GetString());

        // Weder finale Datei noch .part-Datei, keine Metadatenzeile.
        Assert.Empty(Directory.GetFiles(AttachmentsDir));
        using var list = await _client.GetAsync($"/api/v1/bookings/{bookingId}/attachments");
        var items = (await list.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(0, items.GetArrayLength());
    }

    [Fact]
    public async Task Upload_nicht_erlaubter_Typ_abgewiesen()
    {
        var (bookingId, _) = await SeedExpenseAsync();

        var response = await UploadAsync(
            _client, "notiz.txt", Encoding.ASCII.GetBytes("hallo"), "text/plain", bookingId);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal("unsupported_attachment_type",
            body.GetProperty("error").GetProperty("detailCode").GetString());
        Assert.Empty(Directory.GetFiles(AttachmentsDir));
    }

    [Fact]
    public async Task Upload_unbekannte_Buchung_abgewiesen_keine_Waise()
    {
        var response = await UploadAsync(
            _client, "quittung.pdf", PdfBytes, "application/pdf", "gibt-es-nicht");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal("invalid_reference",
            body.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(Directory.GetFiles(AttachmentsDir));
    }

    [Fact]
    public async Task Upload_SplitId_fremder_Buchung_abgewiesen_keine_Waise()
    {
        // Fall f2: 'only-b' existiert nur in der zweiten Buchung; der
        // Prüftupel (aus-a, only-b) ist ungültig.
        var (bookingA, _) = await SeedExpenseAsync("aus-a", firstSplitId: "sp-1");
        await SeedExpenseAsync("aus-b", firstSplitId: "only-b");

        var response = await UploadAsync(
            _client, "quittung.pdf", PdfBytes, "application/pdf", bookingA, "only-b");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal("invalid_reference",
            body.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(Directory.GetFiles(AttachmentsDir));
    }

    [Fact]
    public async Task Gleiche_SplitId_in_zwei_Buchungen_beide_Uploads_gueltig()
    {
        // Fall f1: (BookingId, SplitId) ist der Prüftupel — dieselbe
        // Split-Id in zwei verschiedenen Buchungen ist legal.
        var (bookingA, splitId) = await SeedExpenseAsync("aus-a", firstSplitId: "sp-1");
        var (bookingB, _) = await SeedExpenseAsync("aus-b", firstSplitId: "sp-1");
        Assert.Equal("sp-1", splitId);

        var r1 = await UploadAsync(_client, "a.pdf", PdfBytes, "application/pdf", bookingA, splitId);
        var r2 = await UploadAsync(_client, "b.pdf", PdfBytes, "application/pdf", bookingB, splitId);

        Assert.Equal(System.Net.HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Created, r2.StatusCode);
        Assert.Equal(2, Directory.GetFiles(AttachmentsDir).Length);
    }

    [Fact]
    public async Task Upload_ohne_DateiFeld_abgewiesen()
    {
        var (bookingId, _) = await SeedExpenseAsync();

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(bookingId), "bookingId");
        var response = await _client.PostAsync("/api/v1/attachments", form);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal("validation_error",
            body.GetProperty("error").GetProperty("code").GetString());
    }

    // ---------- Größen (B2 j/k) ----------

    [Fact]
    public async Task Upload_exakt_MaxBytes_akzeptiert()
    {
        // Fall j: exakt maxBytes ist noch gültig.
        const int maxBytes = 1024;
        var factory = new LarioApiFactory(settings: new Dictionary<string, string>
        {
            ["Lario:Attachments:MaxBytes"] = maxBytes.ToString(),
        });
        try
        {
            await WithClientAsync(factory, async client =>
            {
                var (bookingId, _) = await SeedExpenseWithMasterDataAsync(client);
                var content = ValidPdfOfLength(maxBytes);

                var response = await UploadAsync(
                    client, "genau.pdf", content, "application/pdf", bookingId);

                Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
                var dto = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
                Assert.Equal(maxBytes, dto.GetProperty("sizeBytes").GetInt32());
                return true;
            });
        }
        finally
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task Upload_MaxBytes_plus_eins_abgewiesen_413_keine_Waise()
    {
        // Fall k: maxBytes + 1 wird mit 413 + attachment_too_large
        // abgewiesen — weder Datei noch .part bleibt liegen.
        const int maxBytes = 1024;
        var factory = new LarioApiFactory(settings: new Dictionary<string, string>
        {
            ["Lario:Attachments:MaxBytes"] = maxBytes.ToString(),
        });
        try
        {
            var dataDir = factory.DataDirectory;
            await WithClientAsync(factory, async client =>
            {
                var (bookingId, _) = await SeedExpenseWithMasterDataAsync(client);
                var content = ValidPdfOfLength(maxBytes + 1);

                var response = await UploadAsync(
                    client, "zu_gross.pdf", content, "application/pdf", bookingId);

                Assert.Equal((System.Net.HttpStatusCode)413, response.StatusCode);
                var body = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
                Assert.Equal("attachment_too_large",
                    body.GetProperty("error").GetProperty("detailCode").GetString());
                return true;
            });

            Assert.Empty(Directory.GetFiles(
                Path.Combine(dataDir, LarioDataPaths.AttachmentsDirectoryName)));
        }
        finally
        {
            factory.Dispose();
        }
    }

    // ---------- Unicode-Dateiname (B2 l) ----------

    [Fact]
    public async Task Upload_UmlautDateiname_Metadaten_und_ContentDisposition_korrekt()
    {
        var (bookingId, splitId) = await SeedExpenseAsync();
        const string originalName = "Beleg ü-ö-ä.pdf";

        var response = await UploadAsync(
            _client, originalName, PdfBytes, "application/pdf", bookingId, splitId);
        response.EnsureSuccessStatusCode();
        var dto = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(originalName, dto.GetProperty("originalFilename").GetString());

        using var download = await _client.GetAsync(
            $"/api/v1/attachments/{dto.GetProperty("id").GetString()}/download");
        download.EnsureSuccessStatusCode();
        var disposition = download.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);

        // Nicht-ASCII: der Originalname steht RFC-5987-kodiert in
        // 'filename*' (roher Header) und decodiert exakt zum Original
        // (FileNameStar ist beim HttpClient bereits dekodiert).
        var rawDisposition =
            download.Content.Headers.GetValues("Content-Disposition").Single();
        Assert.Contains("filename*=UTF-8''", rawDisposition);
        var filenameStar = Assert.IsType<string>(disposition!.FileNameStar);
        Assert.Equal(originalName, filenameStar);

        // ASCII-Name: zusätzlich klassisches filename mit Originalname.
        var (bookingId2, _) = await SeedExpenseAsync("aus-ascii");
        var r2 = await UploadAsync(
            _client, "plain.pdf", PdfBytes, "application/pdf", bookingId2);
        r2.EnsureSuccessStatusCode();
        var dto2 = (await r2.Content.ReadFromJsonAsync<JsonElement>())!;
        using var d2 = await _client.GetAsync(
            $"/api/v1/attachments/{dto2.GetProperty("id").GetString()}/download");
        d2.EnsureSuccessStatusCode();
        Assert.Equal("plain.pdf", d2.Content.Headers.ContentDisposition?.FileName);
    }

    // ---------- Löschen, 404, 410 (B2 12–14) ----------

    [Fact]
    public async Task Delete_204_Datei_weg_Metadaten_weg_Salden_unverandert()
    {
        var (bookingId, _) = await SeedExpenseAsync();
        (await UploadAsync(_client, "a.pdf", PdfBytes, "application/pdf", bookingId)).EnsureSuccessStatusCode();
        (await UploadAsync(_client, "b.png", PngBytes, "image/png", bookingId)).EnsureSuccessStatusCode();

        var first = (await (await _client.GetAsync(
            $"/api/v1/bookings/{bookingId}/attachments"))
            .Content.ReadFromJsonAsync<JsonElement>())!;
        var idA = first.EnumerateArray()
            .Single(i => i.GetProperty("originalFilename").GetString() == "a.pdf")
            .GetProperty("id").GetString()!;

        var before = await (await _client.GetAsync("/api/v1/journal/balances"))
            .Content.ReadAsStringAsync();

        var delete = await _client.DeleteAsync($"/api/v1/attachments/{idA}");
        Assert.Equal(System.Net.HttpStatusCode.NoContent, delete.StatusCode);

        // Metadaten und Datei sind weg; der zweite Beleg bleibt.
        var gone = await _client.GetAsync($"/api/v1/attachments/{idA}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Single(Directory.GetFiles(AttachmentsDir));

        // Die Buchung selbst ist unverändert (Salden identisch).
        var after = await (await _client.GetAsync("/api/v1/journal/balances"))
            .Content.ReadAsStringAsync();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Unbekannte_Id_404_fuer_Metadaten_Download_und_Delete()
    {
        const string unknown = "ffffffffffffffffffffffffffffffff";

        var metadata = await _client.GetAsync($"/api/v1/attachments/{unknown}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, metadata.StatusCode);

        var download = await _client.GetAsync($"/api/v1/attachments/{unknown}/download");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, download.StatusCode);

        var delete = await _client.DeleteAsync($"/api/v1/attachments/{unknown}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task Datei_manuell_entfernt_liefert_410_Metadaten_bleiben()
    {
        var (bookingId, _) = await SeedExpenseAsync();
        var response = await UploadAsync(
            _client, "quittung.pdf", PdfBytes, "application/pdf", bookingId);
        response.EnsureSuccessStatusCode();
        var dto = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        var id = dto.GetProperty("id").GetString()!;

        File.Delete(Directory.GetFiles(AttachmentsDir)[0]);

        // Download: Datei fehlt → 410 (nicht 500/404).
        var download = await _client.GetAsync($"/api/v1/attachments/{id}/download");
        Assert.Equal(System.Net.HttpStatusCode.Gone, download.StatusCode);
        var body = (await download.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal("attachment_missing",
            body.GetProperty("error").GetProperty("detailCode").GetString());

        // Metadaten sind weiterhin lesbar (Download-Endpunkt dokumentiert
        // den Zustand, Metadaten bleiben erhalten).
        var metadata = await _client.GetAsync($"/api/v1/attachments/{id}");
        Assert.Equal(System.Net.HttpStatusCode.OK, metadata.StatusCode);
    }

    [Fact]
    public async Task Unbekannte_Buchung_in_Liste_400_invalid_reference()
    {
        var response = await _client.GetAsync("/api/v1/bookings/gibt-es-nicht/attachments");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal("invalid_reference",
            body.GetProperty("error").GetProperty("code").GetString());
    }

    // ---------- Auth (B2 15) ----------

    [Fact]
    public async Task Upload_und_Download_ohne_Token_401()
    {
        var (bookingId, _) = await SeedExpenseAsync();
        var anonymous = _factory.CreateClient();

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(PdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(fileContent, "file", "quittung.pdf");
        form.Add(new StringContent(bookingId), "bookingId");

        var upload = await anonymous.PostAsync("/api/v1/attachments", form);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, upload.StatusCode);

        var download = await anonymous.GetAsync(
            "/api/v1/attachments/ffffffffffffffffffffffffffffffff/download");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, download.StatusCode);

        Assert.Empty(Directory.GetFiles(AttachmentsDir));
    }

    [Fact]
    public async Task Upload_mit_falschem_Token_401()
    {
        var (bookingId, _) = await SeedExpenseAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "falscher-token");

        var response = await UploadAsync(
            client, "quittung.pdf", PdfBytes, "application/pdf", bookingId);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- Helpers ----------

    private static readonly byte[] PdfBytes =
    [
        0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, // %PDF-1.4
        0x0A, 0x25, 0x45, 0x6F, 0x66, 0x20, 0x74, 0x65, 0x73, 0x74, 0x0A,
        0x00, 0x01, 0x02, 0x03,
    ];

    private static readonly byte[] JpegBytes =
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00,
        0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00,
    ];

    private static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00,
    ];

    private string AttachmentsDir => Path.Combine(
        _factory.DataDirectory, LarioDataPaths.AttachmentsDirectoryName);

    /// <summary>
    /// PDF mit korrekten Magic-Bytes und exakt <paramref name="length"/> Bytes.
    /// </summary>
    private static byte[] ValidPdfOfLength(int length)
    {
        var bytes = new byte[length];
        Array.Clear(bytes, 0, length);
        Array.Copy(PdfBytes, bytes, Math.Min(PdfBytes.Length, length));
        for (var i = Math.Min(PdfBytes.Length, length); i < length; i++)
        {
            bytes[i] = (byte)'A';
        }
        return bytes;
    }

    private static string ComputeSha256Hex(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private sealed record LoginResponse(string Token, int ExpiresInMinutes);

    private static async Task<LoginResponse> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username = LarioApiFactory.TestUsername,
            password = LarioApiFactory.TestPassword,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private static async Task<T> WithClientAsync<T>(
        LarioApiFactory factory, Func<HttpClient, Task<T>> action)
    {
        var client = factory.CreateClient();
        var login = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.Token);
        return await action(client);
    }

    /// <summary>
    /// Rechnung anlegen (Stammdaten werden pro Testinstanz einmalig
    /// angelegt). <paramref name="firstSplitId"/> erlaubt Splits mit
    /// Buchungs-unique Ids (für Fälle f1/f2).
    /// </summary>
    private async Task<(string BookingId, string SplitId)> SeedExpenseAsync(
        string? bookingId = null, string firstSplitId = "sp-1")
    {
        if (!_masterDataSeeded)
        {
            (_accountId, _poolId, _categoryId) = await CreateMasterDataAsync(_client);
            _masterDataSeeded = true;
        }

        return await CreateExpenseAsync(
            _client, bookingId, firstSplitId, _accountId, _poolId, _categoryId);
    }

    /// <summary>
    /// Stammdaten + Rechnung auf einem frischen Client (für Tests mit
    /// eigener Factory, z. B. Größenfälle j/k und Neustart).
    /// </summary>
    private static async Task<(string BookingId, string SplitId)> SeedExpenseWithMasterDataAsync(
        HttpClient client, string? bookingId = null, string firstSplitId = "sp-1")
    {
        var (accountId, poolId, categoryId) = await CreateMasterDataAsync(client);
        return await CreateExpenseAsync(client, bookingId, firstSplitId, accountId, poolId, categoryId);
    }

    /// <summary>
    /// Legt Konto/Pool/Kategorie an und liefert die hostgenerierten
    /// Ids (die Stammdaten-Endpunkte ignorieren Client-Ids — Design
    /// Schritt 7, T2).
    /// </summary>
    private static async Task<(string AccountId, string PoolId, string CategoryId)> CreateMasterDataAsync(
        HttpClient client)
    {
        var accountResponse = await client.PostAsJsonAsync("/api/v1/accounts", new
        {
            id = "acc-1",
            name = "Konto Eins",
            type = "bank",
            currency = "EUR",
            color = (string?)null,
        });
        accountResponse.EnsureSuccessStatusCode();
        var account = (await accountResponse.Content.ReadFromJsonAsync<JsonElement>())!;

        var poolResponse = await client.PostAsJsonAsync("/api/v1/pools", new
        {
            id = "flex",
            name = "Pool Flex",
            type = "flexible",
            currency = "EUR",
            color = (string?)null,
        });
        poolResponse.EnsureSuccessStatusCode();
        var pool = (await poolResponse.Content.ReadFromJsonAsync<JsonElement>())!;

        var categoryResponse = await client.PostAsJsonAsync("/api/v1/categories", new
        {
            id = "cat-a",
            name = "Kategorie A",
            type = "expense",
            parentId = (string?)null,
            color = (string?)null,
        });
        categoryResponse.EnsureSuccessStatusCode();
        var category = (await categoryResponse.Content.ReadFromJsonAsync<JsonElement>())!;

        return (
            account.GetProperty("id").GetString()!,
            pool.GetProperty("id").GetString()!,
            category.GetProperty("id").GetString()!);
    }

    private static async Task<(string BookingId, string SplitId)> CreateExpenseAsync(
        HttpClient client, string? bookingId, string firstSplitId,
        string accountId, string poolId, string categoryId)
    {
        var id = bookingId ?? "exp-1";
        var response = await client.PostAsJsonAsync("/api/v1/expenses", new
        {
            id,
            date = "2026-10-05",
            totalAmount = 10_000,
            accountId,
            note = (string?)null,
            splits = new object[]
            {
                new
                {
                    id = firstSplitId,
                    amount = 6_000,
                    poolId,
                    categoryId,
                    note = (string?)null,
                    tax = (object?)null,
                },
                new
                {
                    id = "sp-2",
                    amount = 4_000,
                    poolId,
                    categoryId,
                    note = (string?)null,
                    tax = (object?)null,
                },
            },
            expectedBaseRevision = (int?)null,
        });
        response.EnsureSuccessStatusCode();
        return (id, firstSplitId);
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        string filename,
        byte[] content,
        string contentType,
        string bookingId,
        string? splitId = null)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "file", filename);
        form.Add(new StringContent(bookingId), "bookingId");
        if (splitId is not null)
        {
            form.Add(new StringContent(splitId), "splitId");
        }

        return await client.PostAsync("/api/v1/attachments", form);
    }
}
