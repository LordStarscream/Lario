using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lario.Host;
using Lario.Host.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Lario.Api.Tests;

/// <summary>
/// Stammdaten-Endpunkte (Schritt 7, T2): Konten, Pools, Kategorien,
/// Steuerkategorien — CRUD, Steuer-Voreinstellung, Revisionskette
/// (Umbenennen/Archivieren/Reaktivieren erhöhen baseRevision), Fehlerformat.
/// Jede Testinstanz bekommt eine eigene SQLite-DB (LarioApiFactory).
/// </summary>
public class MasterDataApiTests : IDisposable
{
    private readonly LarioApiFactory _factory;
    private readonly HttpClient _client;

    public MasterDataApiTests()
    {
        _factory = new LarioApiFactory();

        // Testserver ohne SynchronizationContext — synchrones Warten ist
        // hier sicher (der Konstruktor darf nicht async sein).
        var login = LoginAsync(_factory.CreateClient()).GetAwaiter().GetResult();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.Token);
    }

    public void Dispose() => _factory.Dispose();

    // ---------- Konten ----------

    [Fact]
    public async Task Konto_Crud_RevisionKette_Wachst_Ueber_Umbenennen_Archivieren_Reaktivieren()
    {
        var created = await _client.PostAsJsonAsync("/api/v1/accounts",
            new { name = "Girokonto", type = "bank", isInterestBearing = false });
        created.EnsureSuccessStatusCode();
        var dto = (await created.Content.ReadFromJsonAsync<JsonElement>())!;

        var id = dto.GetProperty("id").GetString()!;
        Assert.StartsWith("acc-", id);
        Assert.Equal("Girokonto", dto.GetProperty("name").GetString());
        Assert.Equal("bank", dto.GetProperty("type").GetString());
        Assert.Equal("active", dto.GetProperty("status").GetString());
        Assert.Equal(1, dto.GetProperty("baseRevision").GetInt32());
        Assert.False(dto.GetProperty("isInterestBearing").GetBoolean());

        var renamed = await _client.PutAsJsonAsync($"/api/v1/accounts/{id}/name",
            new { name = "Hauptkonto" });
        renamed.EnsureSuccessStatusCode();
        var renamedDto = (await renamed.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal("Hauptkonto", renamedDto.GetProperty("name").GetString());
        Assert.Equal(2, renamedDto.GetProperty("baseRevision").GetInt32());

        var archived = await _client.PostAsync($"/api/v1/accounts/{id}/archive", new StringContent(string.Empty));
        archived.EnsureSuccessStatusCode();
        var archivedDto = (await archived.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal("archived", archivedDto.GetProperty("status").GetString());
        Assert.Equal(3, archivedDto.GetProperty("baseRevision").GetInt32());

        var reactivated = await _client.PostAsync($"/api/v1/accounts/{id}/unarchive", new StringContent(string.Empty));
        reactivated.EnsureSuccessStatusCode();
        var reactivatedDto = (await reactivated.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal("active", reactivatedDto.GetProperty("status").GetString());
        Assert.Equal(4, reactivatedDto.GetProperty("baseRevision").GetInt32());
    }

    [Fact]
    public async Task Konto_Anlegen_ohne_Typ_oder_mit_unbekanntem_Typ_liefert_400()
    {
        var ohneTyp = await _client.PostAsJsonAsync("/api/v1/accounts", new { name = "Ohne Typ" });
        Assert.Equal(HttpStatusCode.BadRequest, ohneTyp.StatusCode);
        var body1 = await ohneTyp.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("validation_error", body1!.Error.Code);

        var falsch = await _client.PostAsJsonAsync("/api/v1/accounts",
            new { name = "Falsch", type = "cash" });
        Assert.Equal(HttpStatusCode.BadRequest, falsch.StatusCode);
        var body2 = await falsch.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("validation_error", body2!.Error.Code);
    }

    [Fact]
    public async Task Konto_Liste_ist_nach_Name_sortiert_und_enthaelt_neues_Konto()
    {
        await _client.PostAsJsonAsync("/api/v1/accounts", new { name = "Zweites", type = "savings" });
        var response = await _client.PostAsJsonAsync("/api/v1/accounts",
            new { name = "Erstes", type = "bank" });
        response.EnsureSuccessStatusCode();

        var listResponse = await _client.GetAsync("/api/v1/accounts");
        listResponse.EnsureSuccessStatusCode();
        var list = (await listResponse.Content.ReadFromJsonAsync<JsonElement>())!;
        var names = list.EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToList();

        Assert.Equal(2, names.Count);
        Assert.Equal("Erstes", names[0]);
        Assert.Equal("Zweites", names[1]);
    }

    // ---------- Pools ----------

    [Fact]
    public async Task Pool_Crud_RevisionKette()
    {
        var created = await _client.PostAsJsonAsync("/api/v1/pools", new { name = "Flex" });
        created.EnsureSuccessStatusCode();
        var dto = (await created.Content.ReadFromJsonAsync<JsonElement>())!;
        var id = dto.GetProperty("id").GetString()!;
        Assert.StartsWith("pool-", id);
        Assert.Equal("active", dto.GetProperty("status").GetString());
        Assert.Equal(1, dto.GetProperty("baseRevision").GetInt32());

        var renamed = await _client.PutAsJsonAsync($"/api/v1/pools/{id}/name", new { name = "Rücklage" });
        renamed.EnsureSuccessStatusCode();
        Assert.Equal("Rücklage", (await renamed.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("name").GetString());

        var archived = await _client.PostAsync($"/api/v1/pools/{id}/archive", new StringContent(string.Empty));
        archived.EnsureSuccessStatusCode();
        Assert.Equal("archived", (await archived.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("status").GetString());

        var reactivated = await _client.PostAsync($"/api/v1/pools/{id}/unarchive", new StringContent(string.Empty));
        reactivated.EnsureSuccessStatusCode();
        Assert.Equal("active", (await reactivated.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("status").GetString());
    }

    [Fact]
    public async Task Pool_Anlegen_mit_leerem_Namen_liefert_400()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/pools", new { name = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("validation_error", body!.Error.Code);
    }

    // ---------- Kategorien + Steuer-Voreinstellung ----------

    [Fact]
    public async Task Kategorie_mit_TaxPreset_Antwort_tragt_Namen_und_Liste_die_wirksame_Voreinstellung()
    {
        // Steuerkategorie anlegen.
        var tax = await _client.PostAsJsonAsync("/api/v1/tax-categories",
            new { name = "MwSt 19 %" });
        tax.EnsureSuccessStatusCode();
        var taxId = (await tax.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("id").GetString()!;

        // Kategorie anlegen — zunächst ohne wirksame Voreinstellung.
        var cat = await _client.PostAsJsonAsync("/api/v1/categories",
            new { name = "Grundbedarf" });
        cat.EnsureSuccessStatusCode();
        var catDto = (await cat.Content.ReadFromJsonAsync<JsonElement>())!;
        var catId = catDto.GetProperty("id").GetString()!;
        Assert.True(catDto.GetProperty("taxPreset").ValueKind == JsonValueKind.Null);

        // Voreinstellung setzen — die Antwort trägt den Namen der Steuerkategorie.
        var preset = await _client.PutAsJsonAsync($"/api/v1/categories/{catId}/tax-preset",
            new { taxRelevant = true, taxCategoryId = taxId, inheritToChildren = true });
        preset.EnsureSuccessStatusCode();
        var presetDto = (await preset.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.True(presetDto.GetProperty("taxRelevant").GetBoolean());
        Assert.Equal(taxId, presetDto.GetProperty("taxCategoryId").GetString());
        Assert.Equal("MwSt 19 %", presetDto.GetProperty("taxCategoryName").GetString());
        Assert.True(presetDto.GetProperty("inheritToChildren").GetBoolean());

        // Die Kategorie-Antwort (Detail + Liste) trägt die wirksame Voreinstellung.
        var detail = await _client.GetAsync($"/api/v1/categories/{catId}");
        detail.EnsureSuccessStatusCode();
        var detailDto = (await detail.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal("MwSt 19 %", detailDto.GetProperty("taxPreset")
            .GetProperty("taxCategoryName").GetString());

        var listResponse = await _client.GetAsync("/api/v1/categories");
        listResponse.EnsureSuccessStatusCode();
        var list = (await listResponse.Content.ReadFromJsonAsync<JsonElement>())!;
        var entry = list.EnumerateArray()
            .Single(e => e.GetProperty("id").GetString() == catId);
        Assert.Equal(taxId, entry.GetProperty("taxPreset").GetProperty("taxCategoryId").GetString());

        // Voreinstellung entfernen — danach wieder null.
        var removed = await _client.DeleteAsync($"/api/v1/categories/{catId}/tax-preset");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        var afterRemove = await _client.GetAsync($"/api/v1/categories/{catId}");
        afterRemove.EnsureSuccessStatusCode();
        Assert.True((await afterRemove.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("taxPreset").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Kategorie_TaxPreset_mit_unbekannter_Steuerkategorie_liefert_400()
    {
        var cat = await _client.PostAsJsonAsync("/api/v1/categories", new { name = "Test" });
        cat.EnsureSuccessStatusCode();
        var catId = (await cat.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("id").GetString()!;

        var response = await _client.PutAsJsonAsync($"/api/v1/categories/{catId}/tax-preset",
            new { taxRelevant = true, taxCategoryId = "taxcat-unknown" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("invalid_reference", body!.Error.Code);
    }

    [Fact]
    public async Task Kategorie_Hierarchie_ParentId_wird_gepflegt()
    {
        var parent = await _client.PostAsJsonAsync("/api/v1/categories", new { name = "Einkauf" });
        parent.EnsureSuccessStatusCode();
        var parentId = (await parent.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("id").GetString()!;

        var child = await _client.PostAsJsonAsync("/api/v1/categories",
            new { name = "Lebensmittel", parentId = parentId });
        child.EnsureSuccessStatusCode();
        var childDto = (await child.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(parentId, childDto.GetProperty("parentId").GetString());
    }

    // ---------- Steuerkategorien ----------

    [Fact]
    public async Task Steuerkategorie_Crud_RevisionKette()
    {
        var created = await _client.PostAsJsonAsync("/api/v1/tax-categories",
            new { name = "MwSt 7 %" });
        created.EnsureSuccessStatusCode();
        var dto = (await created.Content.ReadFromJsonAsync<JsonElement>())!;
        var id = dto.GetProperty("id").GetString()!;
        Assert.StartsWith("taxcat-", id);
        Assert.Equal(1, dto.GetProperty("baseRevision").GetInt32());

        var renamed = await _client.PutAsJsonAsync($"/api/v1/tax-categories/{id}/name",
            new { name = "MwSt 7 % (Ernährung)" });
        renamed.EnsureSuccessStatusCode();
        Assert.Equal(2, (await renamed.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("baseRevision").GetInt32());

        var archived = await _client.PostAsync($"/api/v1/tax-categories/{id}/archive", new StringContent(string.Empty));
        archived.EnsureSuccessStatusCode();
        Assert.Equal("archived", (await archived.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("status").GetString());

        var reactivated = await _client.PostAsync($"/api/v1/tax-categories/{id}/unarchive", new StringContent(string.Empty));
        reactivated.EnsureSuccessStatusCode();
        Assert.Equal("active", (await reactivated.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("status").GetString());
    }

    // ---------- Fehler bei unbekannten Ids ----------

    [Theory]
    [InlineData("GET", "/api/v1/accounts/unknown-id")]
    [InlineData("POST", "/api/v1/accounts/unknown-id/archive")]
    [InlineData("PUT", "/api/v1/pools/unknown-id/name")]
    [InlineData("POST", "/api/v1/categories/unknown-id/archive")]
    [InlineData("GET", "/api/v1/tax-categories/unknown-id")]
    public async Task Unbekannte_Id_liefert_400_invalid_reference(
        string method, string path)
    {
        HttpResponseMessage response;
        if (method == "PUT")
        {
            response = await _client.PutAsJsonAsync(path, new { name = "X" });
        }
        else if (method == "POST")
        {
            response = await _client.PostAsync(path, new StringContent(string.Empty));
        }
        else
        {
            response = await _client.GetAsync(path);
        }

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("invalid_reference", body!.Error.Code);
        Assert.False(string.IsNullOrWhiteSpace(body.Error.Message));
    }

    // ---------- Hilfe ----------

    private sealed record LoginResponse(string Token, int ExpiresInMinutes);

    private static async Task<LoginResponse> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new
            {
                username = LarioApiFactory.TestUsername,
                password = LarioApiFactory.TestPassword
            });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }
}
