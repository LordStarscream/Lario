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
/// Persistenz- und Vertragstests (Schritt 7, T5):
/// <para>
/// A4 — Eine per API angelegte Ausgabe ist nach dem Neustart des
/// Hosts (neuer Prozess, dieselbe SQLite-Ablage) weiterhin vorhanden;
/// Salden werden auch nach dem Neustart ausschließlich abgeleitet.
/// Die Idempotenz-Aufzeichnung überlebt den Neustart ebenfalls.
/// </para>
/// <para>
/// A5 — Kein Schreibendpunkt nimmt Salden als Input entgegen
/// (OpenAPI-Vertragstest): Salden sind nur abgeleitet
/// (<c>GET /balances</c>), nie frei übermittelbare Wahrheit.
/// </para>
/// Die Referenzfälle A3 sind in <c>JournalWriteApiTests</c> komplett
/// abgedeckt (Korrektur 85→80, Teilrückzahlung, Korrektur nach
/// beglichem Ausgleich, Kontostandskorrektur ≠ Einnahme).
/// </summary>
public class PersistenceApiTests
{
    // ---------- A4: Persistenz über Neustart ----------

    [Fact]
    public async Task Ausgabe_ueberlebt_Neustart_Salden_werden_abgeleitet()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("lario-a4-").FullName;
        try
        {
            // ---------- Erster Hostlauf: Stammdaten + Ausgabe anlegen ----------
            var first = new LarioApiFactory(dataDirectory);
            var client1 = AuthenticatedClient(first);
            var (accId, poolId, catId) = await SeedMinimalAsync(client1);

            var expense = new
            {
                id = "exp-a4",
                date = "2026-11-10",
                totalAmount = 10_000,
                accountId = accId,
                note = "Miete",
                splits = new object[]
                {
                    new
                    {
                        id = "sp-1",
                        amount = 10_000,
                        poolId,
                        categoryId = catId,
                        note = (string?)null,
                        tax = (object?)null
                    }
                },
                expectedBaseRevision = (int?)null
            };

            var created = await client1.PostAsJsonAsync("/api/v1/expenses", expense);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.False((await created.Content.ReadFromJsonAsync<JsonElement>())!
                .GetProperty("idempotent").GetBoolean());

            client1.Dispose();
            first.Dispose(); // → Host „aus" (Prozess/DB-Verbindung beendet)

            // ---------- Zweiter Hostlauf: dieselbe Ablage ----------
            using var second = new LarioApiFactory(dataDirectory);
            using var client = AuthenticatedClient(second);

            // Die Ausgabe ist vorhanden (persistiert).
            var list = await (await client.GetAsync("/api/v1/bookings"))
                .Content.ReadFromJsonAsync<JsonElement>()!;
            Assert.Equal(1, list.GetArrayLength());
            var item = list[0];
            Assert.Equal("exp-a4", item.GetProperty("id").GetString());
            Assert.Equal(10_000, item.GetProperty("totalAmount").GetInt64());

            var detail = await (await client.GetAsync("/api/v1/bookings/exp-a4"))
                .Content.ReadFromJsonAsync<JsonElement>()!;
            Assert.Equal(10_000, detail.GetProperty("totalAmount").GetInt64());
            Assert.Equal(10_000, detail.GetProperty("splits")[0]
                .GetProperty("amount").GetInt64());
            Assert.Equal("Flex", detail.GetProperty("splits")[0]
                .GetProperty("poolName").GetString());

            // Salden abgeleitet: Konto -100 €, Pool -100 € (die Ausgabe
            // verkleinert Poolstand und Konto; nichts wurde „gespeichert"
            // — das Journal ist die einzige Quelle), Lücke = 0.
            var balances = await (await client.GetAsync("/api/v1/balances"))
                .Content.ReadFromJsonAsync<JsonElement>()!;
            Assert.Equal(
                -10_000,
                balances.GetProperty("accounts").EnumerateArray()
                    .First(a => a.GetProperty("accountId").GetString() == accId)
                    .GetProperty("balance").GetInt64());
            Assert.Equal(
                -10_000,
                balances.GetProperty("pools").EnumerateArray()
                    .First(p => p.GetProperty("poolId").GetString() == poolId)
                    .GetProperty("balance").GetInt64());
            Assert.Equal(-10_000,
                balances.GetProperty("totalAccountBalance").GetInt64());
            Assert.Equal(-10_000,
                balances.GetProperty("totalPoolBalance").GetInt64());
            Assert.Equal(0, balances.GetProperty("accountPoolGap").GetInt64());

            // Die offene Ausgleichsposition (Rechenvertrag §3.5) ist
            // ebenfalls persistiert und nach dem Neustart wieder da.
            var settlements = await (await client.GetAsync("/api/v1/settlements"))
                .Content.ReadFromJsonAsync<JsonElement>()!;
            Assert.Equal(1, settlements.GetArrayLength());
            var settlement = settlements[0];
            Assert.Equal("open", settlement.GetProperty("status").GetString());
            Assert.Equal(10_000, settlement.GetProperty("restAmount").GetInt64());
            Assert.Equal("exp-a4", settlement.GetProperty("createdByBookingId").GetString());

            // Idempotenz-Aufzeichnung überlebt den Neustart: gleiche
            // Id + gleicher Inhalt → keine zweite Buchung.
            var repeat = await client.PostAsJsonAsync(
                "/api/v1/expenses", expense);
            Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
            Assert.True((await repeat.Content.ReadFromJsonAsync<JsonElement>())!
                .GetProperty("idempotent").GetBoolean());

            var listAfter = await (await client.GetAsync("/api/v1/bookings"))
                .Content.ReadFromJsonAsync<JsonElement>()!;
            Assert.Equal(1, listAfter.GetArrayLength());
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

    // ---------- A5: Keine Salden als Input (OpenAPI-Vertrag) ----------

    [Fact]
    public async Task Kein_Schreibendpunkt_nimmt_Saldo_als_Input()
    {
        await using var factory = new LarioApiFactory();
        using var client = factory.CreateClient();

        // /openapi/v1.json liegt außerhalb /api/v1 → ohne Token.
        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var doc = (await response.Content.ReadFromJsonAsync<JsonElement>())!;

        var schemas = doc.GetProperty("components").GetProperty("schemas");
        var paths = doc.GetProperty("paths");

        var checkedEndpoints = 0;
        foreach (var path in paths.EnumerateObject())
        {
            foreach (var method in new[] { "post", "put", "patch" })
            {
                if (!path.Value.TryGetProperty(method, out var operation))
                {
                    continue;
                }

                if (!operation.TryGetProperty("requestBody", out var requestBody)
                    || !requestBody.GetProperty("content")
                        .TryGetProperty("application/json", out var media))
                {
                    continue;
                }

                checkedEndpoints++;
                var properties = new List<string>();
                CollectPropertyNames(
                    media.GetProperty("schema"), schemas, properties);

                foreach (var property in properties)
                {
                    var lower = property.ToLowerInvariant();
                    Assert.False(
                        lower.Contains("balance") || lower.Contains("saldo"),
                        $"{method.ToUpperInvariant()} {path.Name}: Eigenschaft " +
                        $"'{property}' nimmt einen Saldo als Input entgegen (A5)");
                }
            }
        }

        // Alle zehn Journal-Schreibendpunkte plus Stammdaten- und
        // Login-Endpunkte haben JSON-Body — der Vertragstest muss
        // auch etwas geprüft haben.
        Assert.True(
            checkedEndpoints >= 14,
            $"Erwartet mindestens 14 Schreibendpunkte mit JSON-Body, " +
            $"geprüft wurden nur {checkedEndpoints} — OpenAPI-Dokument " +
            "unvollständig?");
    }

    // ---------- Helpers ----------

    /// <summary>
    /// Legt den minimalen Stammdaten-Satz an (eine Bank, ein Pool,
    /// eine Kategorie) und liefert die Ids. <paramref name="client"/>
    /// muss bereits authentifiziert sein.
    /// </summary>
    private static async Task<(string AccountId, string PoolId, string CategoryId)>
        SeedMinimalAsync(HttpClient client)
    {
        var account = await (await client.PostAsJsonAsync("/api/v1/accounts", new
        {
            id = "acc-a4",
            name = "Giro",
            type = "bank",
            currency = "EUR",
            color = (string?)null
        })).Content.ReadFromJsonAsync<JsonElement>()!;

        var pool = await (await client.PostAsJsonAsync("/api/v1/pools", new
        {
            id = "pool-a4",
            name = "Flex",
            type = "flexible",
            currency = "EUR",
            color = (string?)null
        })).Content.ReadFromJsonAsync<JsonElement>()!;

        var category = await (await client.PostAsJsonAsync("/api/v1/categories", new
        {
            id = "cat-a4",
            name = "Wohnen",
            type = "expense",
            parentId = (string?)null,
            color = (string?)null
        })).Content.ReadFromJsonAsync<JsonElement>()!;

        return (
            account.GetProperty("id").GetString()!,
            pool.GetProperty("id").GetString()!,
            category.GetProperty("id").GetString()!);
    }

    private static HttpClient AuthenticatedClient(LarioApiFactory factory)
    {
        // Synchron-Login über einen Wegwerf-Client (etabliertes Muster
        // in den anderen Api-Testklassen); der eigentliche Client
        // bleibt für die Assertions.
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

    /// <summary>
    /// Sammelt alle Eigenschaftsnamen eines OpenAPI-Schemas (folgt
    /// <c>$ref</c>, verschachtelten Objekten und Array-Items).
    /// </summary>
    private static void CollectPropertyNames(
        JsonElement schema,
        JsonElement schemas,
        List<string> result,
        int depth = 0)
    {
        if (depth > 4)
        {
            return;
        }

        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("$ref", out var refElement))
        {
            var name = refElement.GetString()!.Split('/').Last();
            if (schemas.TryGetProperty(name, out var resolved))
            {
                CollectPropertyNames(resolved, schemas, result, depth + 1);
            }

            return;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                result.Add(property.Name);
                CollectPropertyNames(property.Value, schemas, result, depth + 1);
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            CollectPropertyNames(items, schemas, result, depth + 1);
        }
    }
}
