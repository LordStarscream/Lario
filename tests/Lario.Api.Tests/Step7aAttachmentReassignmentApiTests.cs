using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Lario.Api.Tests;

/// <summary>
/// Nachtrag N1 (Review 7a): Umzuordnung eines vorhandenen Belegs über
/// <c>PATCH /api/v1/attachments/{id}</c> — innerhalb derselben Buchung
/// (anderer Teilposten oder gesamte Ausgabe), Referenzfehler (fremde
/// Buchung, unbekannte Id), No-Op bei unveränderter Zuordnung
/// (<c>updatedAtUtc</c> bleibt null), OriginalSplitId bleibt bei der
/// manuellen Umzuordnung unberührt (Design D1b: es dokumentiert nur die
/// systemseitige Umlageung bei Split-Entfernung) und Auth (401).
/// </summary>
public sealed class Step7aAttachmentReassignmentApiTests : IDisposable
{
    private readonly LarioApiFactory _factory;
    private readonly HttpClient _client;
    private bool _masterDataSeeded;
    private string _accountId = string.Empty;
    private string _poolId = string.Empty;
    private string _categoryId = string.Empty;

    public Step7aAttachmentReassignmentApiTests()
    {
        _factory = new LarioApiFactory();
        var login = LoginAsync(_factory.CreateClient()).GetAwaiter().GetResult();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.Token);
    }

    public void Dispose() => _factory.Dispose();

    // ---------- Grundfunktion ----------

    [Fact]
    public async Task Uezuordnung_zum_anderen_Split_200_splitId_und_updatedAtUtc()
    {
        // Buchung mit zwei Splits: sp-1 (6000) und sp-2 (4000).
        var (bookingId, splitId) = await SeedExpenseAsync();
        var id = await UploadToSplitAsync(bookingId, "quittung.pdf", splitId);

        using var response = await _client.PatchAsync(
            $"/api/v1/attachments/{id}", JsonBody(new { splitId = "sp-2" }));
        response.EnsureSuccessStatusCode();
        var dto = (await response.Content.ReadFromJsonAsync<JsonElement>())!;

        Assert.Equal("sp-2", dto.GetProperty("splitId").GetString());
        Assert.NotEqual(JsonValueKind.Null,
            dto.GetProperty("updatedAtUtc").ValueKind);
        // originalSplitId bleibt null (keine systemseitige Umlageung).
        Assert.Equal(JsonValueKind.Null,
            dto.GetProperty("originalSplitId").ValueKind);

        // Die Liste der Buchung zeigt dieselbe Zuordnung.
        using var list = await _client.GetAsync(
            $"/api/v1/bookings/{bookingId}/attachments");
        list.EnsureSuccessStatusCode();
        var item = (await list.Content.ReadFromJsonAsync<JsonElement>())!;
        var single = Assert.Single(item.EnumerateArray());
        Assert.Equal("sp-2", single.GetProperty("splitId").GetString());
        Assert.NotEqual(JsonValueKind.Null,
            single.GetProperty("updatedAtUtc").ValueKind);
    }

    [Fact]
    public async Task Uezuordnung_zur_gesamten_Ausgabe_200_splitId_null()
    {
        var (bookingId, splitId) = await SeedExpenseAsync();
        var id = await UploadToSplitAsync(bookingId, "quittung.pdf", splitId);

        using var response = await _client.PatchAsync(
            $"/api/v1/attachments/{id}", JsonBody(new { splitId = (string?)null }));
        response.EnsureSuccessStatusCode();
        var dto = (await response.Content.ReadFromJsonAsync<JsonElement>())!;

        Assert.Equal(JsonValueKind.Null, dto.GetProperty("splitId").ValueKind);
        Assert.NotEqual(JsonValueKind.Null,
            dto.GetProperty("updatedAtUtc").ValueKind);
    }

    [Fact]
    public async Task Unveraenderte_Zuordnung_Noop_updatedAtUtc_bleibt_null()
    {
        // Bei unveränderter Referenz darf kein Schreibvorgang stattfinden:
        // updatedAtUtc bleibt null, bis tatsächlich etwas umgeordnet wurde.
        var (bookingId, splitId) = await SeedExpenseAsync();
        var id = await UploadToSplitAsync(bookingId, "quittung.pdf", splitId);

        using var response = await _client.PatchAsync(
            $"/api/v1/attachments/{id}", JsonBody(new { splitId }));
        response.EnsureSuccessStatusCode();
        var dto = (await response.Content.ReadFromJsonAsync<JsonElement>())!;

        Assert.Equal(splitId, dto.GetProperty("splitId").GetString());
        Assert.Equal(JsonValueKind.Null,
            dto.GetProperty("updatedAtUtc").ValueKind);
    }

    // ---------- Referenzfehler ----------

    [Fact]
    public async Task Split_fremder_Buchung_400_invalid_reference_zuordnung_unveraendert()
    {
        // (BookingId, SplitId) ist der Prüftupel: 'only-b-2' existiert
        // nur in der zweiten Buchung, nicht dem Beleg (Fall f2-Prinzip —
        // die zweite Buchung braucht eigene Split-Ids, weil jede Buchung
        // standardmäßig sp-1/sp-2 erhält).
        var (bookingA, splitIdA) = await SeedExpenseAsync("aus-a");
        await SeedExpenseAsync("aus-b", firstSplitId: "only-b-1", secondSplitId: "only-b-2");
        var id = await UploadToSplitAsync(bookingA, "quittung.pdf", splitIdA);

        using var response = await _client.PatchAsync(
            $"/api/v1/attachments/{id}", JsonBody(new { splitId = "only-b-2" }));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal("invalid_reference",
            body.GetProperty("error").GetProperty("code").GetString());

        // Zuordnung und Zeitstempel bleiben unberührt.
        using var metadata = await _client.GetAsync($"/api/v1/attachments/{id}");
        metadata.EnsureSuccessStatusCode();
        var dto = (await metadata.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(splitIdA, dto.GetProperty("splitId").GetString());
        Assert.Equal(JsonValueKind.Null,
            dto.GetProperty("updatedAtUtc").ValueKind);
    }

    [Fact]
    public async Task Unbekannte_SplitId_400_invalid_reference()
    {
        var (bookingId, splitId) = await SeedExpenseAsync();
        var id = await UploadToSplitAsync(bookingId, "quittung.pdf", splitId);

        using var response = await _client.PatchAsync(
            $"/api/v1/attachments/{id}", JsonBody(new { splitId = "gibt-es-nicht" }));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal("invalid_reference",
            body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unbekannter_Beleg_404()
    {
        const string unknown = "ffffffffffffffffffffffffffffffff";
        using var response = await _client.PatchAsync(
            $"/api/v1/attachments/{unknown}", JsonBody(new { splitId = (string?)null }));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PATCH_ohne_Koerper_400_validation_error()
    {
        var (bookingId, splitId) = await SeedExpenseAsync();
        var id = await UploadToSplitAsync(bookingId, "quittung.pdf", splitId);

        using var response = await _client.PatchAsync(
            $"/api/v1/attachments/{id}",
            new StringContent("", Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal("validation_error",
            body.GetProperty("error").GetProperty("code").GetString());
    }

    // ---------- OriginalSplitId bleibt bei manueller Umzuordnung unberührt (D1b) ----------

    [Fact]
    public async Task Manuelle_Uezuordnung_aender_originalSplitId_nicht()
    {
        // 1) Beleg an sp-1; 2) Korrektur entfernt sp-1 → systemseitige
        //    Umlageung (splitId null, originalSplitId sp-1); 3) manuelle
        //    Umzuordnung auf sp-2 → originalSplitId bleibt sp-1.
        var (bookingId, splitId) = await SeedExpenseAsync();
        var id = await UploadToSplitAsync(bookingId, "quittung.pdf", splitId);

        var correction = await _client.PostAsJsonAsync(
            $"/api/v1/bookings/{bookingId}/corrections", new
        {
            operationId = "corr-reassign",
            expectedBaseRevision = 1,
            date = (string?)null,
            note = (string?)null,
            accountId = (string?)null,
            totalAmount = 10_000,
            splits = new object[]
            {
                new
                {
                    id = "sp-2",
                    amount = 10_000,
                    poolId = _poolId,
                    categoryId = _categoryId,
                    note = (string?)null,
                    tax = (object?)null,
                },
            },
            allocations = (object[]?)null,
        });
        correction.EnsureSuccessStatusCode();

        using var umgehangen = await _client.GetAsync($"/api/v1/attachments/{id}");
        umgehangen.EnsureSuccessStatusCode();
        var dtoU = (await umgehangen.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(JsonValueKind.Null, dtoU.GetProperty("splitId").ValueKind);
        Assert.Equal("sp-1", dtoU.GetProperty("originalSplitId").GetString());
        // Die Umlageung der Korrektur ist keine Nutzeraktion: updatedAtUtc
        // bleibt null.
        Assert.Equal(JsonValueKind.Null,
            dtoU.GetProperty("updatedAtUtc").ValueKind);

        using var response = await _client.PatchAsync(
            $"/api/v1/attachments/{id}", JsonBody(new { splitId = "sp-2" }));
        response.EnsureSuccessStatusCode();
        var dto = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal("sp-2", dto.GetProperty("splitId").GetString());
        Assert.Equal("sp-1", dto.GetProperty("originalSplitId").GetString());
        Assert.NotEqual(JsonValueKind.Null,
            dto.GetProperty("updatedAtUtc").ValueKind);
    }

    // ---------- Auth ----------

    [Fact]
    public async Task PATCH_ohne_Token_401()
    {
        var (bookingId, splitId) = await SeedExpenseAsync();
        var id = await UploadToSplitAsync(bookingId, "quittung.pdf", splitId);
        var anonymous = _factory.CreateClient();

        using var response = await anonymous.PatchAsync(
            $"/api/v1/attachments/{id}", JsonBody(new { splitId = (string?)null }));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- Helpers ----------

    private static StringContent JsonBody(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

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

    /// <summary>
    /// Rechnung mit zwei Splits (sp-1: 6000, sp-2: 4000) anlegen — die
    /// Stammdaten werden pro Testinstanz einmalig angelegt.
    /// </summary>
    private async Task<(string BookingId, string SplitId)> SeedExpenseAsync(
        string? bookingId = null, string firstSplitId = "sp-1", string secondSplitId = "sp-2")
    {
        if (!_masterDataSeeded)
        {
            (_accountId, _poolId, _categoryId) = await CreateMasterDataAsync(_client);
            _masterDataSeeded = true;
        }

        var id = bookingId ?? "exp-1";
        var response = await _client.PostAsJsonAsync("/api/v1/expenses", new
        {
            id,
            date = "2026-10-05",
            totalAmount = 10_000,
            accountId = _accountId,
            note = (string?)null,
            splits = new object[]
            {
                new
                {
                    id = firstSplitId,
                    amount = 6_000,
                    poolId = _poolId,
                    categoryId = _categoryId,
                    note = (string?)null,
                    tax = (object?)null,
                },
                new
                {
                    id = secondSplitId,
                    amount = 4_000,
                    poolId = _poolId,
                    categoryId = _categoryId,
                    note = (string?)null,
                    tax = (object?)null,
                },
            },
            expectedBaseRevision = (int?)null,
        });
        response.EnsureSuccessStatusCode();
        return (id, firstSplitId);
    }

    private async Task<string> UploadToSplitAsync(
        string bookingId, string filename, string splitId)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(PdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(fileContent, "file", filename);
        form.Add(new StringContent(bookingId), "bookingId");
        form.Add(new StringContent(splitId), "splitId");

        var response = await _client.PostAsync("/api/v1/attachments", form);
        response.EnsureSuccessStatusCode();
        var dto = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        return dto.GetProperty("id").GetString()!;
    }

    private async Task<(string AccountId, string PoolId, string CategoryId)>
        CreateMasterDataAsync(HttpClient client)
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

    private static readonly byte[] PdfBytes =
    [
        0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, // %PDF-1.4
        0x0A, 0x25, 0x45, 0x6F, 0x66, 0x20, 0x74, 0x65, 0x73, 0x74, 0x0A,
        0x00, 0x01, 0x02, 0x03,
    ];
}
