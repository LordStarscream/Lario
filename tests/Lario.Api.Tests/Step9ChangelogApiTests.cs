using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lario.Host;
using Lario.Host.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lario.Api.Tests;

/// <summary>
/// Zeilenaugenhöhe-Tests für das Changelog (Schritt 9, D2/D3/D6):
/// <list type="bullet">
/// <item>Design D6: <i>jede erstellte oder geänderte Buchungszeile ist
/// eine Changelog-Zeile</i> — unabhängig vom Schreibpfad. Der entscheidende
/// Regressionstest hier: <b>Session-API</b>-Buchungen (
/// <c>POST /api/v1/expenses</c>) müssen genau wie Push-Buchungen eine
/// Zeile erzeugen. (Früher legte nur der explizte Push-Pfad eine Zeile
/// an; der zentrale Kontext-Hook war wegen eines fehlenden "Value"-
/// Aliases in der Tabellenpräsenzprüfung stillschweigend deaktiviert.)</item>
/// <item>Design D3: Stammdaten-Zeilen trägt der MasterDataRepository-
/// Haken ein — pro Änderung genau eine Zeile (keine Doppelung durch
/// den Kontext-Sammler).</item>
/// <item>Design D2: <c>SyncProtocolState.CursorCounter</c> spiegelt das
/// größte <c>SyncChange.Id</c>.</item>
/// <item>Design D4: ein Pull ab dem zurückgegebenen Cursor ist leer
/// (keine Lücke, kein Duplikat).</item>
/// </list>
/// </summary>
public class Step9ChangelogApiTests : IDisposable
{
    private readonly LarioApiFactory _factory;
    private readonly HttpClient _sessionClient;

    public Step9ChangelogApiTests()
    {
        _factory = new LarioApiFactory();
        var login = LoginAsync(_factory.CreateClient()).GetAwaiter().GetResult();
        _sessionClient = _factory.CreateClient();
        _sessionClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.Token);
    }

    public void Dispose() => _factory.Dispose();

    // ---------- D6: Session-API-Buchung erzeugt eine Changelog-Zeile ----------

    [Fact]
    public async Task Session_Ausgabe_erzeugt_genau_eine_BuchungsZeile_im_Changelog()
    {
        var (acc, _, pool, cat, _) = await SeedMasterDataAsync();

        var response = await _sessionClient.PostAsJsonAsync("/api/v1/expenses", new
        {
            id = "exp-session-1",
            date = "2026-11-10",
            totalAmount = 8_500,
            accountId = acc,
            note = (string?)null,
            splits = new object[]
            {
                new { id = "sp-1", amount = 8_500, poolId = pool, categoryId = cat, note = (string?)null, tax = (object?)null }
            }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var changes = await GetChangesAsync(0);
        var array = changes.GetProperty("changes").EnumerateArray().ToList();

        // 5 Stammdaten-Zeilen (Seed) + 1 Buchungs-Zeile.
        Assert.Equal(6, array.Count);
        var bookingZeile = Assert.Single(array,
            c => c.GetProperty("entityType").GetString() == "booking");
        Assert.Equal("exp-session-1", bookingZeile.GetProperty("entityId").GetString());
        Assert.Equal(1, bookingZeile.GetProperty("revision").GetInt32());

        // D4/D2: der Cursor zeigt auf die neue Zeile — ein Pull ab
        // diesem Punkt ist leer.
        var letztesId = array.Max(c => c.GetProperty("id").GetInt64());
        Assert.Equal(letztesId, changes.GetProperty("cursor").GetInt64());
        Assert.True(changes.GetProperty("complete").GetBoolean());

        var rest = await GetChangesAsync(letztesId);
        Assert.Equal(0, rest.GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public async Task Session_Ausgabe_idempotente_Wiederholung_zaehlt_nicht_nocheinmal()
    {
        var (acc, _, pool, cat, _) = await SeedMasterDataAsync();

        object payload = new
        {
            id = "exp-session-idem",
            date = "2026-11-10",
            totalAmount = 8_500,
            accountId = acc,
            note = (string?)null,
            splits = new object[]
            {
                new { id = "sp-1", amount = 8_500, poolId = pool, categoryId = cat, note = (string?)null, tax = (object?)null }
            }
        };

        (await _sessionClient.PostAsJsonAsync("/api/v1/expenses", payload))
            .EnsureSuccessStatusCode();
        // Gleiche Id + gleicher Inhalt: idempotente Wiederholung (200).
        (await _sessionClient.PostAsJsonAsync("/api/v1/expenses", payload))
            .EnsureSuccessStatusCode();

        var array = (await GetChangesAsync(0)).GetProperty("changes").EnumerateArray().ToList();
        Assert.Equal(1, array.Count(c => c.GetProperty("entityType").GetString() == "booking"));
    }

    // ---------- D3: Stammdaten — je Änderung genau eine Zeile ----------

    [Fact]
    public async Task Stammdaten_Aenderungen_erzeugen_je_genau_eine_Zeile()
    {
        // Die Konto-/Pool-/Kategorie-/Steuer-Ids werden serverseitig
        // generiert (NewId) — die Assertions nutzen die Antwort-Ids.
        var (acc, acc2, pool, cat, tax) = await SeedMasterDataAsync();

        // 5 Erzeugungen (Seed); dazu Umbenennung + Archivierung.
        (await _sessionClient.PutAsJsonAsync(
            $"/api/v1/accounts/{acc}/name", new { name = "Umbenannt" })).EnsureSuccessStatusCode();
        (await _sessionClient.PostAsync(
            $"/api/v1/pools/{pool}/archive", new StringContent(string.Empty))).EnsureSuccessStatusCode();

        var array = (await GetChangesAsync(0)).GetProperty("changes").EnumerateArray().ToList();
        Assert.Equal(7, array.Count);

        // Jede Entität erscheint genau so oft, wie sie geändert wurde —
        // der Kontext-Sammler darf die Haken-Zeilen nicht duplizieren.
        var zähler = array
            .GroupBy(c => (c.GetProperty("entityType").GetString()!,
                           c.GetProperty("entityId").GetString()!))
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(5, zähler.Count);
        Assert.Equal(2, zähler[("account", acc)]);   // create + umbenannt
        Assert.Equal(2, zähler[("pool", pool)]);     // create + archiviert
        Assert.Equal(1, zähler[("account", acc2)]);
        Assert.Equal(1, zähler[("category", cat)]);
        Assert.Equal(1, zähler[("taxCategory", tax)]);
    }

    // ---------- D2: Cursor spiegelt das größte Changelog-Id ----------

    [Fact]
    public async Task Cursor_Spiegelt_groesstes_ChangelogId_nach_gemischtem_Schreibverkehr()
    {
        var (acc, acc2, pool, cat, tax) = await SeedMasterDataAsync();
        var (_, deviceToken) = await PairAsync();
        var deviceClient = CreateDeviceClient(deviceToken);

        // Gemischter Schreibverkehr über beide Pfade.
        object ausgabe1 = new
        {
            id = "exp-gem-1",
            date = "2026-11-10",
            totalAmount = 1_000,
            accountId = acc,
            note = (string?)null,
            splits = new object[]
            {
                new { id = "sp-1", amount = 1_000, poolId = pool, categoryId = cat, note = (string?)null, tax = (object?)null }
            }
        };
        object ausgabe2 = new
        {
            id = "exp-gem-2",
            date = "2026-11-10",
            totalAmount = 2_000,
            accountId = acc2,
            note = (string?)null,
            splits = new object[]
            {
                new { id = "sp-1", amount = 2_000, poolId = pool, categoryId = cat, note = (string?)null, tax = (object?)null }
            }
        };
        (await _sessionClient.PostAsJsonAsync("/api/v1/expenses", ausgabe1)).EnsureSuccessStatusCode();
        (await _sessionClient.PostAsJsonAsync("/api/v1/expenses", ausgabe2)).EnsureSuccessStatusCode();
        (await deviceClient.PostAsJsonAsync(
            "/api/v1/sync/push", ValidPushPayload("op-gem", acc, pool, cat)))
            .EnsureSuccessStatusCode();
        (await _sessionClient.PutAsJsonAsync(
            $"/api/v1/accounts/{acc}/name", new { name = "Runde" })).EnsureSuccessStatusCode();

        // 5 (Seed) + 2 (Session) + 1 (Push) + 1 (Umbenennung) = 9 Zeilen,
        // aufsteigend, keine Duplikate.
        var changes = await GetChangesAsync(0);
        var array = changes.GetProperty("changes").EnumerateArray().ToList();
        Assert.Equal(9, array.Count);

        var ids = array.Select(c => c.GetProperty("id").GetInt64()).ToList();
        Assert.Equal(ids.OrderBy(i => i).ToList(), ids);

        // Jede Entität erscheint genau so oft, wie sie geändert wurde
        // (acc: create + umbenannt = 2×, alles sonst 1×) — keine
        // Duplikat-Zeilen.
        var zähler = array
            .GroupBy(c => (c.GetProperty("entityType").GetString()!,
                           c.GetProperty("entityId").GetString()!))
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(2, zähler[("account", acc)]);
        foreach (var (typ, id) in new[]
                 {
                     ("account", acc2), ("pool", pool), ("category", cat),
                     ("taxCategory", tax),
                     ("booking", "exp-gem-1"), ("booking", "exp-gem-2"),
                     ("booking", "exp-op-gem")
                 })
        {
            Assert.True(zähler.TryGetValue((typ, id), out var count) && count == 1,
                $"erwartet genau 1 Zeile für {typ} {id}, waren {zähler.GetValueOrDefault((typ, id))}");
        }
        Assert.Equal(9, zähler.Values.Sum());

        // D2: der protokollierte Cursor-Counter ist exakt das größte Id
        // der Tabelle (direkt aus der Datenbank geprüft, nicht nur via
        // Pull-Antwort).
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Lario.Infrastructure.LarioDbContext>();
        var state = await db.SyncProtocolStates.AsNoTracking().FirstAsync(s => s.Id == "1");
        var maxId = await db.SyncChanges.AsNoTracking().MaxAsync(c => c.Id);

        Assert.Equal(maxId, state.CursorCounter);
        Assert.Equal(ids.Last(), changes.GetProperty("cursor").GetInt64());

        // D4: Pull ab dem Cursor → leer (keine Lücke, kein Duplikat).
        var rest = await GetChangesAsync(ids.Last());
        Assert.Equal(0, rest.GetProperty("changes").GetArrayLength());
        Assert.True(rest.GetProperty("complete").GetBoolean());
    }

    // ---------- Hilfsmittel ----------

    private HttpClient CreateDeviceClient(string deviceToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", deviceToken);
        return client;
    }

    private async Task<(string DeviceId, string Token)> PairAsync(string name = "Syncgerät")
    {
        var response = await _sessionClient.PostAsJsonAsync("/api/v1/sync/pair",
            new { name });
        response.EnsureSuccessStatusCode();
        var dto = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        return (dto.GetProperty("id").GetString()!,
                dto.GetProperty("token").GetString()!);
    }

    private async Task<JsonElement> GetChangesAsync(long cursor, int limit = 500)
    {
        var (_, deviceToken) = await PairAsync($"Cursorgerät-{cursor}");
        var deviceClient = CreateDeviceClient(deviceToken);
        var response = await deviceClient
            .GetAsync($"/api/v1/sync/changes?cursor={cursor}&limit={limit}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static object ValidPushPayload(
        string operationId, string accountId, string poolId, string categoryId,
        long amount = 8_500)
    {
        return new
        {
            operationId,
            type = "expense",
            payload = new
            {
                id = $"exp-{operationId}",
                date = "2026-11-10",
                totalAmount = amount,
                accountId,
                note = (string?)null,
                splits = new object[]
                {
                    new { id = "sp-1", amount, poolId, categoryId, note = (string?)null, tax = (object?)null }
                },
                expectedBaseRevision = (int?)null
            },
            payloadHash = "0".PadLeft(64, '0'),
            createdAtUtc = "2026-11-10T12:00:00Z"
        };
    }

    private static async Task<LoginResponse> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username = LarioApiFactory.TestUsername,
            password = LarioApiFactory.TestPassword
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    /// <summary>
    /// Legt 2 Konten, 1 Pool, 1 Kategorie und 1 Steuerkategorie an
    /// (Session-API) — erzeugt 5 Stammdaten-Changelog-Zeilen.
    /// </summary>
    private async Task<(string Acc1, string Acc2, string Pool, string Category, string Tax)>
        SeedMasterDataAsync()
    {
        async Task<JsonElement> PostAsync(string url, object payload)
        {
            var response = await _sessionClient.PostAsJsonAsync(url, payload);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        }

        var acc1 = await PostAsync("/api/v1/accounts",
            new { id = "acc-1", name = "Konto Eins", type = "bank", currency = "EUR", color = (string?)null });
        var acc2 = await PostAsync("/api/v1/accounts",
            new { id = "acc-2", name = "Konto Zwei", type = "savings", currency = "EUR", color = (string?)null });
        var pool = await PostAsync("/api/v1/pools",
            new { id = "flex", name = "Pool Flex", type = "flexible", currency = "EUR", color = (string?)null });
        var cat = await PostAsync("/api/v1/categories",
            new { id = "cat-a", name = "Kategorie A", type = "expense", parentId = (string?)null, color = (string?)null });
        var tax = await PostAsync("/api/v1/tax-categories",
            new { id = "tax-19", name = "Steuer 19", rate = 19.0 });

        return (
            acc1.GetProperty("id").GetString()!,
            acc2.GetProperty("id").GetString()!,
            pool.GetProperty("id").GetString()!,
            cat.GetProperty("id").GetString()!,
            tax.GetProperty("id").GetString()!);
    }
}
