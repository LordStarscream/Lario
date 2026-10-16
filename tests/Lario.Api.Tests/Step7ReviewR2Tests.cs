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
/// Regressionstests für PR #9 Review R2 (Stand dbc2c3d):
///
/// P2 — Das Storno-Total (Review R1 P2) wurde über den <i>aktuellen</i>
///      Original-Anzeigetotal berechnet. Nach Widerruf (Storno des
///      Stornos) und anschließender In-place-Korrektur des Originals
///      würden historische Storno-Anzeigewerte dadurch rückwirkend
///      umgeschrieben (R: −8500 → −8000, W: +8500 → +8000) und
///      widersprächen den eigenen Postings.
///
///      Fix: Höhe aus den eigenen unveränderlichen Storno-Postings
///      (rekonstruierte Kettenspitze zum Stornozeitpunkt),
///      Konvention (Vorzeichen) aus dem Typ der Kettenspitze.
///
///      Regression: E(85) → R → W → Korrektur E(80): R bleibt −8500,
///      W bleibt +8500, E zeigt 8000 — in Liste, Detail und
///      identischem Storno-Replay; auch nach Hostneustart.
/// </summary>
public class Step7ReviewR2Tests : IDisposable
{
    private readonly LarioApiFactory _factory;
    private readonly HttpClient _client;

    public Step7ReviewR2Tests()
    {
        _factory = new LarioApiFactory();
        _client = AuthenticatedClient(_factory);
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task P2_Historische_Stornobetraege_bleiben_nach_Widerruf_und_Korrektur_erhalten()
    {
        var (acc, pool, cat) = await SeedMinimalAsync();

        // E: Ausgabe 85 € (Revision 1).
        await PostAsync("/api/v1/expenses", Expense(acc, pool, cat, 8_500));

        // R: Storno von E (Kette: R → E). E: Revision 1 → 2, R: Revision 1.
        var r = (await (await _client.PostAsJsonAsync(
            "/api/v1/bookings/exp-r2/reversals", Reversal("rev-r2-1", 1, "2026-11-11")))
            .Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(-8_500, r.GetProperty("booking").GetProperty("totalAmount").GetInt64());

        // W: Widerruf (Storno des Stornos, Kette: W → R → E).
        // R: Revision 1 → 2, E: Revision 2 → 3.
        var w = (await (await _client.PostAsJsonAsync(
            "/api/v1/bookings/rev-r2-1/reversals", Reversal("rev-r2-2", 1, "2026-11-12")))
            .Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(8_500, w.GetProperty("booking").GetProperty("totalAmount").GetInt64());

        // Korrektur E: 85 € → 80 € (E liegt nach R und W bei Revision 3).
        var correction = await _client.PostAsJsonAsync(
            "/api/v1/bookings/exp-r2/corrections", new
        {
            operationId = "corr-r2-1",
            expectedBaseRevision = 3,
            date = (string?)null,
            note = "Preisnachlass",
            accountId = (string?)null,
            totalAmount = 8_000,
            splits = new object[]
            {
                new
                {
                    id = "sp-1",
                    amount = 8_000,
                    poolId = pool,
                    categoryId = cat,
                    note = (string?)null,
                    tax = (object?)null
                }
            },
            allocations = (object[]?)null
        });
        Assert.Equal(HttpStatusCode.Created, correction.StatusCode);

        // Vor dem Fix: R zeigte −8.000 und W +8.000 (über den aktuellen
        // Originalzustand rekursiv berechnet) — die historischen
        // Stornobewegungen wurden rückwirkend umgeschrieben.
        var items = (await GetBookingsAsync()).EnumerateArray()
            .ToDictionary(b => b.GetProperty("id").GetString()!);

        Assert.Equal(8_000, items["exp-r2"].GetProperty("totalAmount").GetInt64());
        Assert.Equal(-8_500, items["rev-r2-1"].GetProperty("totalAmount").GetInt64());
        Assert.Equal(8_500, items["rev-r2-2"].GetProperty("totalAmount").GetInt64());

        // Detail-Endpunkte: dieselbe historische Konvention.
        var rDetail = await (await _client.GetAsync("/api/v1/bookings/rev-r2-1"))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal(-8_500, rDetail.GetProperty("totalAmount").GetInt64());

        var wDetail = await (await _client.GetAsync("/api/v1/bookings/rev-r2-2"))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal(8_500, wDetail.GetProperty("totalAmount").GetInt64());

        var eDetail = await (await _client.GetAsync("/api/v1/bookings/exp-r2"))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal(8_000, eDetail.GetProperty("totalAmount").GetInt64());
        Assert.Equal(4, eDetail.GetProperty("baseRevision").GetInt32());

        // Identischer Storno-Replay (gleiche Id + Inhalt) liefert die
        // vorhandene Buchung — mit dem historischen Wert, nicht dem
        // rekonstruierten über das korrigierte Original.
        var replay = await _client.PostAsJsonAsync(
            "/api/v1/bookings/exp-r2/reversals", Reversal("rev-r2-1", 1, "2026-11-11"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayBody = (await replay.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.True(replayBody.GetProperty("idempotent").GetBoolean());
        Assert.Equal(-8_500, replayBody.GetProperty("booking")
            .GetProperty("totalAmount").GetInt64());

        // Die Journalsalden bleiben konsistent: die effektive Ausgabe ist
        // 80 € (Korrektur) — R und W haben sich gegenseitig aufgehoben.
        var balances = await (await _client.GetAsync("/api/v1/balances"))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal(
            -8_000,
            balances.GetProperty("accounts").EnumerateArray()
                .First(a => a.GetProperty("accountId").GetString() == acc)
                .GetProperty("balance").GetInt64());
    }

    [Fact]
    public async Task P2_Historische_Stornobetraege_ueberleben_Neustart()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("lario-r2-").FullName;
        try
        {
            // ---------- Erster Hostlauf: E(85) → R → W → Korrektur E(80) ----------
            var first = new LarioApiFactory(dataDirectory);
            using (var client = AuthenticatedClient(first))
            {
                var (acc, pool, cat) = await SeedAsync(client);

                await PostAsync(client, "/api/v1/expenses", Expense(acc, pool, cat, 8_500));
                await PostAsync(
                    client,
                    "/api/v1/bookings/exp-r2/reversals", Reversal("rev-r2-1", 1, "2026-11-11"));
                await PostAsync(
                    client,
                    "/api/v1/bookings/rev-r2-1/reversals", Reversal("rev-r2-2", 1, "2026-11-12"));

                var correction = await client.PostAsJsonAsync(
                    "/api/v1/bookings/exp-r2/corrections", new
                {
                    operationId = "corr-r2b",
                    expectedBaseRevision = 3,
                    date = (string?)null,
                    note = (string?)null,
                    accountId = (string?)null,
                    totalAmount = 8_000,
                    splits = new object[]
                    {
                        new
                        {
                            id = "sp-1",
                            amount = 8_000,
                            poolId = pool,
                            categoryId = cat,
                            note = (string?)null,
                            tax = (object?)null
                        }
                    },
                    allocations = (object[]?)null
                });
                Assert.Equal(HttpStatusCode.Created, correction.StatusCode);
            }
            first.Dispose(); // → Host „aus"

            // ---------- Zweiter Hostlauf: dieselbe Ablage ----------
            using var second = new LarioApiFactory(dataDirectory);
            using var clientAfterRestart = AuthenticatedClient(second);

            var items = (await (await clientAfterRestart.GetAsync("/api/v1/bookings"))
                .Content.ReadFromJsonAsync<JsonElement>()!).EnumerateArray()
                .ToDictionary(b => b.GetProperty("id").GetString()!);

            // Historische Anzeigewerte sind persistiert — die Anzeige
            // wird aus den Storno-Postings abgeleitet, nicht aus dem
            // (korrigierten) Original neu berechnet.
            Assert.Equal(8_000, items["exp-r2"].GetProperty("totalAmount").GetInt64());
            Assert.Equal(-8_500, items["rev-r2-1"].GetProperty("totalAmount").GetInt64());
            Assert.Equal(8_500, items["rev-r2-2"].GetProperty("totalAmount").GetInt64());

            var rDetail = await (await clientAfterRestart.GetAsync("/api/v1/bookings/rev-r2-1"))
                .Content.ReadFromJsonAsync<JsonElement>()!;
            Assert.Equal(-8_500, rDetail.GetProperty("totalAmount").GetInt64());
        }
        finally
        {
            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Temp-Verzeichnis bleibt liegen — kein Testergebnis.
            }
        }
    }

    // ---------- Helfer ----------

    private Task<(string Acc, string Pool, string Cat)> SeedMinimalAsync()
        => SeedAsync(_client);

    private async Task<JsonElement> PostAsync(string url, object payload)
    {
        var response = await _client.PostAsJsonAsync(url, payload);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{url} → {(int)response.StatusCode}: " +
                await response.Content.ReadAsStringAsync());
        }
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private async Task<JsonElement> PostAsync(
        HttpClient client, string url, object payload)
    {
        var response = await client.PostAsJsonAsync(url, payload);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{url} → {(int)response.StatusCode}: " +
                await response.Content.ReadAsStringAsync());
        }
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private async Task<JsonElement> GetBookingsAsync()
    {
        var response = await _client.GetAsync("/api/v1/bookings");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static object Expense(string acc, string pool, string cat, long amount) => new
    {
        id = "exp-r2",
        date = "2026-11-10",
        totalAmount = amount,
        accountId = acc,
        note = (string?)null,
        splits = new object[]
        {
            new
            {
                id = "sp-1",
                amount,
                poolId = pool,
                categoryId = cat,
                note = (string?)null,
                tax = (object?)null
            }
        },
        expectedBaseRevision = (int?)null
    };

    private static object Reversal(string id, int expectedBaseRevision, string date) => new
    {
        id,
        expectedBaseRevision,
        date,
        note = (string?)null
    };

    /// <summary>
    /// Legt den minimalen Stammdaten-Satz an. Die Ids werden serverseitig
    /// generiert (MasterDataEndpoints) — die Antwort-Ids müssen für die
    /// Journal-Referenzen verwendet werden.
    /// </summary>
    private static async Task<(string Acc, string Pool, string Cat)> SeedAsync(
        HttpClient client)
    {
        var account = (await (await client.PostAsJsonAsync("/api/v1/accounts", new
        {
            id = (string?)null,
            name = "Giro",
            type = "bank",
            currency = "EUR",
            color = (string?)null
        })).Content.ReadFromJsonAsync<JsonElement>())!;

        var pool = (await (await client.PostAsJsonAsync("/api/v1/pools", new
        {
            id = (string?)null,
            name = "Flex",
            type = "flexible",
            currency = "EUR",
            color = (string?)null
        })).Content.ReadFromJsonAsync<JsonElement>())!;

        var category = (await (await client.PostAsJsonAsync("/api/v1/categories", new
        {
            id = (string?)null,
            name = "Wohnen",
            type = "expense",
            parentId = (string?)null,
            color = (string?)null
        })).Content.ReadFromJsonAsync<JsonElement>())!;

        return (
            account.GetProperty("id").GetString()!,
            pool.GetProperty("id").GetString()!,
            category.GetProperty("id").GetString()!);
    }

    private static HttpClient AuthenticatedClient(LarioApiFactory factory)
    {
        var bootstrap = factory.CreateClient();
        var login = LoginAsync(bootstrap).GetAwaiter().GetResult();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.Token);
        bootstrap.Dispose();
        return client;
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
}
