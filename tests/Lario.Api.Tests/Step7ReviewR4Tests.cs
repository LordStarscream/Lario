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
/// Regressionstests für PR #9 Review Runde 2 (P2, Korrektur/Steuer):
///
/// Die Client-Vorbelegung setzte bei unmarkierten Splits
/// taxMode=„Wie Voreinstellung“; hatte die Kategorie inzwischen eine
/// relevante Voreinstellung bekommen, materialisierte eine Korrektur
/// plötzlich neue Steuerkennzeichnungen, obwohl der Benutzer an den
/// Splits nichts Steuerliches geändert hatte.
///
/// Fix: <c>tax = null</c> in einem Korrektur-Split heißt „unverändert“
/// (dieselbe Konvention wie die übrigen Korrekturfelder) — ein
/// bestehender Split (gleiche Id) behält seine ursprüngliche
/// Kennzeichnung (inkl. Steuer-Notiz und bisheriger Nichtmarkierung),
/// ein neuer Split wird wie bei der Neuanlage aus der
/// Kategorie-Voreinstellung materialisiert. Eine Steueränderung muss
/// als explizite Kennzeichnung gesendet werden.
/// </summary>
public class Step7ReviewR4Tests : IDisposable
{
    private readonly LarioApiFactory _factory;
    private readonly HttpClient _client;

    public Step7ReviewR4Tests()
    {
        _factory = new LarioApiFactory();
        _client = AuthenticatedClient(_factory);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ---------- P2: Unveränderte Splits behalten ihre Kennzeichnung ----------

    [Fact]
    public async Task P2_Bisherige_Nichtmarkierung_bleibt_erhalten_obwohl_Kategorie_jetzt_Voreinstellung_traegt()
    {
        var (acc, flex, freizeit, catA, catB, tax19) = await SeedAsync();

        // Ausgabe 85 €: 65 € Flex mit expliziter Kennzeichnung inkl.
        // Steuer-Notiz (Kategorie A), 20 € Freizeit unmarkiert
        // (Kategorie B, zum Anlagezeitpunkt ohne Voreinstellung).
        await PostAsync("/api/v1/expenses", new
        {
            id = "exp-r4-1",
            date = "2026-11-10",
            totalAmount = 8_500,
            accountId = acc,
            note = (string?)null,
            splits = new object[]
            {
                new
                {
                    id = "sp-r4-1-1",
                    amount = 6_500,
                    poolId = flex,
                    categoryId = catA,
                    note = (string?)null,
                    tax = new
                    {
                        relevant = true,
                        taxCategoryId = tax19,
                        markedAmount = 1_900,
                        note = "Vorsteuer 11/2026"
                    }
                },
                new
                {
                    id = "sp-r4-1-2",
                    amount = 2_000,
                    poolId = freizeit,
                    categoryId = catB,
                    note = (string?)null,
                    tax = (object?)null
                }
            },
            expectedBaseRevision = (int?)null
        });

        // Inzwischen wird eine relevante Voreinstellung für Kategorie B
        // eingerichtet — der ursprüngliche Split blieb unmarkiert.
        var preset = await _client.PutAsJsonAsync(
            $"/api/v1/categories/{catB}/tax-preset",
            new { taxRelevant = true, taxCategoryId = tax19, inheritToChildren = (bool?)null });
        Assert.Equal(HttpStatusCode.OK, preset.StatusCode);

        // Korrektur: Split-Betrag von sp-1 geändert (Aufteilung ändert
        // sich → beide Splits werden gesendet), Steuer-Eingaben bleiben
        // unangetastet (tax = null).
        var correction = await _client.PostAsJsonAsync(
            "/api/v1/bookings/exp-r4-1/corrections", new
        {
            operationId = "corr-r4-1",
            expectedBaseRevision = 1,
            date = (string?)null,
            note = (string?)null,
            accountId = (string?)null,
            totalAmount = 8_000,
            splits = new object[]
            {
                new
                {
                    id = "sp-r4-1-1",
                    amount = 6_000,
                    poolId = flex,
                    categoryId = catA,
                    note = (string?)null,
                    tax = (object?)null
                },
                new
                {
                    id = "sp-r4-1-2",
                    amount = 2_000,
                    poolId = freizeit,
                    categoryId = catB,
                    note = (string?)null,
                    tax = (object?)null
                }
            },
            allocations = (object[]?)null
        });
        Assert.Equal(HttpStatusCode.Created, correction.StatusCode);

        var detail = await GetDetailAsync("exp-r4-1");
        var splits = detail.GetProperty("splits").EnumerateArray().ToList();
        Assert.Equal(2, splits.Count);

        // sp-1: vollständige Original-Kennzeichnung bleibt erhalten
        // (inkl. Steuer-Notiz) — nicht neu von der Voreinstellung.
        var tax1 = splits[0].GetProperty("tax");
        Assert.True(tax1.GetProperty("relevant").GetBoolean());
        Assert.Equal(tax19, tax1.GetProperty("taxCategoryId").GetString());
        Assert.Equal(1_900, tax1.GetProperty("markedAmount").GetInt64());
        Assert.Equal("Vorsteuer 11/2026", tax1.GetProperty("note").GetString());

        // sp-2: historisch unmarkiert und unangetastet → bleibt
        // unmarkiert, die neue Voreinstellung wird nicht nachgetragen.
        var tax2 = splits[1].GetProperty("tax");
        Assert.Equal(JsonValueKind.Null, tax2.ValueKind);
    }

    [Fact]
    public async Task P2_Neuer_Split_in_der_Korrektur_wird_aus_der_Voreinstellung_materialisiert()
    {
        var (acc, flex, _, catA, _, tax19) = await SeedAsync();

        // Kategorie A trägt von Anfang an eine relevante Voreinstellung.
        var preset = await _client.PutAsJsonAsync(
            $"/api/v1/categories/{catA}/tax-preset",
            new { taxRelevant = true, taxCategoryId = tax19, inheritToChildren = (bool?)null });
        Assert.Equal(HttpStatusCode.OK, preset.StatusCode);

        // Ausgabe 80 € mit einem unmarkiert gelassenen Split → bei der
        // Neuanlage materialisiert (Voreinstellung).
        await PostAsync("/api/v1/expenses", new
        {
            id = "exp-r4-2",
            date = "2026-11-10",
            totalAmount = 8_000,
            accountId = acc,
            note = (string?)null,
            splits = new object[]
            {
                new
                {
                    id = "sp-r4-2-1",
                    amount = 8_000,
                    poolId = flex,
                    categoryId = catA,
                    note = (string?)null,
                    tax = (object?)null
                }
            },
            expectedBaseRevision = (int?)null
        });

        // Korrektur: Gesamtbetrag 85 €, neuer Split sp-r4-2-2 (500) ohne
        // Steuer-Angabe → wird aus der Voreinstellung materialisiert;
        // der bestehende Split behält seine (materialisierte) Kennzeichnung.
        var correction = await _client.PostAsJsonAsync(
            "/api/v1/bookings/exp-r4-2/corrections", new
        {
            operationId = "corr-r4-2",
            expectedBaseRevision = 1,
            date = (string?)null,
            note = (string?)null,
            accountId = (string?)null,
            totalAmount = 8_500,
            splits = new object[]
            {
                new
                {
                    id = "sp-r4-2-1",
                    amount = 8_000,
                    poolId = flex,
                    categoryId = catA,
                    note = (string?)null,
                    tax = (object?)null
                },
                new
                {
                    id = "sp-r4-2-2",
                    amount = 500,
                    poolId = flex,
                    categoryId = catA,
                    note = (string?)null,
                    tax = (object?)null
                }
            },
            allocations = (object[]?)null
        });
        Assert.Equal(HttpStatusCode.Created, correction.StatusCode);

        var detail = await GetDetailAsync("exp-r4-2");
        var splits = detail.GetProperty("splits").EnumerateArray().ToList();
        Assert.Equal(2, splits.Count);
        foreach (var split in splits)
        {
            var tax = split.GetProperty("tax");
            Assert.True(tax.ValueKind == JsonValueKind.Object);
            Assert.True(tax.GetProperty("relevant").GetBoolean());
            Assert.Equal(tax19, tax.GetProperty("taxCategoryId").GetString());
            Assert.Equal(JsonValueKind.Null, tax.GetProperty("markedAmount").ValueKind);
        }
    }

    [Fact]
    public async Task P2_Explizite_Steueranderung_ersetzt_die_bisherige_Kennzeichnung()
    {
        var (acc, flex, _, catA, _, tax19) = await SeedAsync();

        await PostAsync("/api/v1/expenses", new
        {
            id = "exp-r4-3",
            date = "2026-11-10",
            totalAmount = 8_000,
            accountId = acc,
            note = (string?)null,
            splits = new object[]
            {
                new
                {
                    id = "sp-r4-3-1",
                    amount = 8_000,
                    poolId = flex,
                    categoryId = catA,
                    note = (string?)null,
                    tax = new
                    {
                        relevant = true,
                        taxCategoryId = tax19,
                        markedAmount = (long?)null,
                        note = "Vorsteuer 11/2026"
                    }
                }
            },
            expectedBaseRevision = (int?)null
        });

        // Explizite Steueränderung: jetzt nicht mehr relevant — die
        // bisherige Kennzeichnung (inkl. Notiz) wird ersetzt.
        var correction = await _client.PostAsJsonAsync(
            "/api/v1/bookings/exp-r4-3/corrections", new
        {
            operationId = "corr-r4-3",
            expectedBaseRevision = 1,
            date = (string?)null,
            note = (string?)null,
            accountId = (string?)null,
            totalAmount = 8_000,
            splits = new object[]
            {
                new
                {
                    id = "sp-r4-3-1",
                    amount = 8_000,
                    poolId = flex,
                    categoryId = catA,
                    note = (string?)null,
                    tax = new { relevant = false, taxCategoryId = (string?)null, markedAmount = (long?)null, note = (string?)null }
                }
            },
            allocations = (object[]?)null
        });
        Assert.Equal(HttpStatusCode.Created, correction.StatusCode);

        var detail = await GetDetailAsync("exp-r4-3");
        var tax = detail.GetProperty("splits").EnumerateArray().Single().GetProperty("tax");
        Assert.False(tax.GetProperty("relevant").GetBoolean());
        Assert.Equal(JsonValueKind.Null, tax.GetProperty("taxCategoryId").ValueKind);
        Assert.Equal(JsonValueKind.Null, tax.GetProperty("note").ValueKind);
    }

    [Fact]
    public async Task P2_Idempotente_Wiederholung_der_Korrektur_quittiert_ohne_zweite_Revision()
    {
        var (acc, flex, _, catA, catB, tax19) = await SeedAsync();

        await PostAsync("/api/v1/expenses", new
        {
            id = "exp-r4-4",
            date = "2026-11-10",
            totalAmount = 8_500,
            accountId = acc,
            note = (string?)null,
            splits = new object[]
            {
                new { id = "sp-r4-4-1", amount = 6_500, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null },
                new { id = "sp-r4-4-2", amount = 2_000, poolId = flex, categoryId = catB, note = (string?)null, tax = (object?)null }
            },
            expectedBaseRevision = (int?)null
        });

        object payload() => new
        {
            operationId = "corr-r4-4",
            expectedBaseRevision = 1,
            date = (string?)null,
            note = (string?)null,
            accountId = (string?)null,
            totalAmount = 8_000,
            splits = new object[]
            {
                new { id = "sp-r4-4-1", amount = 6_000, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null },
                new { id = "sp-r4-4-2", amount = 2_000, poolId = flex, categoryId = catB, note = (string?)null, tax = (object?)null }
            },
            allocations = (object[]?)null
        };

        var erste = await _client.PostAsJsonAsync("/api/v1/bookings/exp-r4-4/corrections", payload());
        Assert.Equal(HttpStatusCode.Created, erste.StatusCode);

        // Gleiche Operation-Id + gleicher Inhalt (Client-Retry nach
        // verlorener Antwort) → wird nur quittiert, keine zweite
        // Korrektur, keine zweite Revisionserhöhung.
        var zweite = await _client.PostAsJsonAsync("/api/v1/bookings/exp-r4-4/corrections", payload());
        Assert.Equal(HttpStatusCode.OK, zweite.StatusCode);
        var body = (await zweite.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.True(body.GetProperty("idempotent").GetBoolean());

        // Keine zweite Korrektur → Revision 2 (Anlage + Korrektur),
        // nicht 3.
        var detail = await GetDetailAsync("exp-r4-4");
        Assert.Equal(2, detail.GetProperty("baseRevision").GetInt32());
    }

    // ---------- Helfer ----------

    private async Task<(string Acc, string Flex, string Freizeit,
        string CatA, string CatB, string Tax19)> SeedAsync()
    {
        var account = (await (await _client.PostAsJsonAsync("/api/v1/accounts", new
        {
            id = (string?)null,
            name = "Giro",
            type = "bank",
            currency = "EUR",
            color = (string?)null
        })).Content.ReadFromJsonAsync<JsonElement>())!;

        var flex = (await (await _client.PostAsJsonAsync("/api/v1/pools", new
        {
            id = (string?)null,
            name = "Flex",
            type = "flexible",
            currency = "EUR",
            color = (string?)null
        })).Content.ReadFromJsonAsync<JsonElement>())!;

        var freizeit = (await (await _client.PostAsJsonAsync("/api/v1/pools", new
        {
            id = (string?)null,
            name = "Freizeit",
            type = "flexible",
            currency = "EUR",
            color = (string?)null
        })).Content.ReadFromJsonAsync<JsonElement>())!;

        var catA = (await (await _client.PostAsJsonAsync("/api/v1/categories", new
        {
            id = (string?)null,
            name = "Wohnen",
            type = "expense",
            parentId = (string?)null,
            color = (string?)null
        })).Content.ReadFromJsonAsync<JsonElement>())!;

        var catB = (await (await _client.PostAsJsonAsync("/api/v1/categories", new
        {
            id = (string?)null,
            name = "Energie",
            type = "expense",
            parentId = (string?)null,
            color = (string?)null
        })).Content.ReadFromJsonAsync<JsonElement>())!;

        var tax19 = (await (await _client.PostAsJsonAsync("/api/v1/tax-categories", new
        {
            id = (string?)null,
            name = "Steuer 19",
            rate = 19.0
        })).Content.ReadFromJsonAsync<JsonElement>())!;

        return (
            account.GetProperty("id").GetString()!,
            flex.GetProperty("id").GetString()!,
            freizeit.GetProperty("id").GetString()!,
            catA.GetProperty("id").GetString()!,
            catB.GetProperty("id").GetString()!,
            tax19.GetProperty("id").GetString()!);
    }

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

    private async Task<JsonElement> GetDetailAsync(string id)
    {
        var response = await _client.GetAsync($"/api/v1/bookings/{id}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
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
