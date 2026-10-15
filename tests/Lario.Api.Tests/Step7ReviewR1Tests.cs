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
/// Regressionstests für PR #9 Review R1 (Commit 8c856bc, Stand T1–T5):
///
/// P1 — Die Auth-Pfadprüfung muss dieselben Pfadvarianten decken wie das
///      case-insensitive ASP.NET-Routing: gemischte Schreibweise
///      (/API/V1/...) umgeht die Authentifizierung nicht, Login bleibt
///      erreichbar, und /api/v1foo gehört nicht zur API-Fläche
///      (segmentbasiert, kein Präfix-Match).
/// P2 — Fehlende Listen, explizite null und null-Elemente in den
///      Command-Listenfeldern liefern 400 validation_error statt 500
///      (NRE); es entsteht keine Buchung, und Revisionen bleiben
///      unverbraucht.
/// P3 — Das Storno-Total ist die Negation des Original-Anzeigetotals
///      (85 € Ausgabe → −8.500 statt +17.000); der Widerruf (Storno des
///      Stornos) ist wieder positiv; die Konvention gilt in
///      Write-Endpunkten, Detail- und Listen-Endpunkt. (Die Revision im
///      Storno ist die Buchungsrevision des Originals — Start 1; eine
///      Kontostandskorrektur ist laut Design Schritt 6 §3.2 nicht
///      stornierbar und bleibt außerhalb des Negationsvertrags.)
/// </summary>
public class Step7ReviewR1Tests : IDisposable
{
    private readonly LarioApiFactory _factory;
    private readonly HttpClient _client;

    public Step7ReviewR1Tests()
    {
        _factory = new LarioApiFactory();

        var login = LoginAsync(_factory.CreateClient()).GetAwaiter().GetResult();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.Token);
    }

    public void Dispose() => _factory.Dispose();

    // ---------- P1: Auth-Pfadprüfung case-insensitiv ----------

    [Fact]
    public async Task P1_Gemischte_Schreibweise_ohne_Token_liefert_401()
    {
        // Das Routing matcht Literaltexte case-insensitiv — die
        // Auth-Pfadprüfung muss es auch, sonst Bypass.
        var response = await _factory.CreateClient()
            .GetAsync("/API/V1/bookings");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(body);
        Assert.Equal("unauthorized", body!.Error.Code);
    }

    [Fact]
    public async Task P1_Gemischte_Schreibweise_POST_ohne_Token_401_und_buchert_nichts()
    {
        var (acc1, _, flex, _, catA, _, _) = await SeedMasterDataAsync();

        var response = await _factory.CreateClient().PostAsJsonAsync("/Api/V1/expenses", new
        {
            id = "exp-bypass",
            date = "2026-11-10",
            totalAmount = 8_500,
            accountId = acc1,
            note = (string?)null,
            splits = new object[]
            {
                new { id = "sp-1", amount = 8_500, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null }
            },
            expectedBaseRevision = (int?)null
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var list = await GetBookingsAsync();
        Assert.Equal(0, list.GetArrayLength());
    }

    [Fact]
    public async Task P1_Login_bleibt_case_insensitiv_erreikbaar()
    {
        // Die Login-Ausnahme darf durch die case-insensitive Prüfung
        // nicht verschwinden (sonst wäre der Einstieg blockiert).
        var response = await _factory.CreateClient().PostAsJsonAsync("/API/V1/auth/login", new
        {
            username = LarioApiFactory.TestUsername,
            password = LarioApiFactory.TestPassword
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(login);
        Assert.False(string.IsNullOrWhiteSpace(login!.Token));
    }

    [Fact]
    public async Task P1_API_v1foo_gehoert_nicht_zur_API_Flaeche_wird_nicht_401()
    {
        // Segmentbasiert: /api/v1foo ist kein Teil der /api/v1-Oberfläche
        // (kein Präfix-Match) und wird nicht durch die Auth blockiert —
        // es erreicht Routing/Static-Fallback (keine 401).
        var response = await _factory.CreateClient().GetAsync("/api/v1foo");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- P2: null-/null-Elemente in Command-Listen → 400 ----------

    [Fact]
    public async Task P2_Ausgabe_splits_fehlt_null_oder_NullElement_400_keine_Buchung()
    {
        var (acc1, _, flex, _, catA, _, _) = await SeedMasterDataAsync();

        var fehlt = await _client.PostAsJsonAsync("/api/v1/expenses", new
        {
            id = "exp-n1",
            date = "2026-11-10",
            totalAmount = 8_500,
            accountId = acc1,
            note = (string?)null,
            expectedBaseRevision = (int?)null
        });
        await AssertValidationErrorAsync(fehlt, "splits");

        var nullListe = await _client.PostAsJsonAsync("/api/v1/expenses", new
        {
            id = "exp-n2",
            date = "2026-11-10",
            totalAmount = 8_500,
            accountId = acc1,
            note = (string?)null,
            splits = (object[]?)null,
            expectedBaseRevision = (int?)null
        });
        await AssertValidationErrorAsync(nullListe, "splits");

        var nullElement = await _client.PostAsJsonAsync("/api/v1/expenses", new
        {
            id = "exp-n3",
            date = "2026-11-10",
            totalAmount = 8_500,
            accountId = acc1,
            note = (string?)null,
            splits = new object?[] { null },
            expectedBaseRevision = (int?)null
        });
        await AssertValidationErrorAsync(nullElement, "splits");

        var list = await GetBookingsAsync();
        Assert.Equal(0, list.GetArrayLength());
    }

    [Fact]
    public async Task P2_Einnahme_allocations_fehlt_null_oder_NullElement_400_keine_Buchung()
    {
        var (acc1, _, flex, _, _, _, _) = await SeedMasterDataAsync();

        var fehlt = await _client.PostAsJsonAsync("/api/v1/income", new
        {
            id = "inc-n1",
            date = "2026-11-30",
            amount = 10_000,
            accountId = acc1,
            note = (string?)null,
            fundingMonth = (string?)null,
            expectedBaseRevision = (int?)null
        });
        await AssertValidationErrorAsync(fehlt, "allocations");

        var nullListe = await _client.PostAsJsonAsync("/api/v1/income", new
        {
            id = "inc-n2",
            date = "2026-11-30",
            amount = 10_000,
            accountId = acc1,
            note = (string?)null,
            fundingMonth = (string?)null,
            allocations = (object[]?)null,
            expectedBaseRevision = (int?)null
        });
        await AssertValidationErrorAsync(nullListe, "allocations");

        var nullElement = await _client.PostAsJsonAsync("/api/v1/income", new
        {
            id = "inc-n3",
            date = "2026-11-30",
            amount = 10_000,
            accountId = acc1,
            note = (string?)null,
            fundingMonth = (string?)null,
            allocations = new object?[] { null },
            expectedBaseRevision = (int?)null
        });
        await AssertValidationErrorAsync(nullElement, "allocations");

        var list = await GetBookingsAsync();
        Assert.Equal(0, list.GetArrayLength());
    }

    [Fact]
    public async Task P2_Anfangsbestand_distribution_fehlt_null_oder_NullElement_400()
    {
        var (acc1, _, _, _, _, _, _) = await SeedMasterDataAsync();

        var fehlt = await _client.PostAsJsonAsync("/api/v1/opening-balances", new
        {
            id = "init-n1",
            date = "2026-11-01",
            total = 100_000,
            accountId = acc1,
            note = (string?)null
        });
        await AssertValidationErrorAsync(fehlt, "distribution");

        var nullListe = await _client.PostAsJsonAsync("/api/v1/opening-balances", new
        {
            id = "init-n2",
            date = "2026-11-01",
            total = 100_000,
            accountId = acc1,
            note = (string?)null,
            distribution = (object[]?)null
        });
        await AssertValidationErrorAsync(nullListe, "distribution");

        var nullElement = await _client.PostAsJsonAsync("/api/v1/opening-balances", new
        {
            id = "init-n3",
            date = "2026-11-01",
            total = 100_000,
            accountId = acc1,
            note = (string?)null,
            distribution = new object?[] { null }
        });
        await AssertValidationErrorAsync(nullElement, "distribution");

        var list = await GetBookingsAsync();
        Assert.Equal(0, list.GetArrayLength());
    }

    [Fact]
    public async Task P2_Transfer_purposes_NullElement_400_keine_Buchung()
    {
        var (acc1, acc2, _, _, _, _, _) = await SeedMasterDataAsync();

        var response = await _client.PostAsJsonAsync("/api/v1/transfers", new
        {
            id = "tr-n1",
            date = "2026-11-05",
            fromAccountId = acc1,
            toAccountId = acc2,
            amount = 10_000,
            purposes = new object?[] { null },
            relatedSettlementId = (string?)null,
            note = (string?)null
        });
        await AssertValidationErrorAsync(response, "purposes");

        var list = await GetBookingsAsync();
        Assert.Equal(0, list.GetArrayLength());
    }

    [Fact]
    public async Task P2_Umwidmung_accounts_NullElement_400_keine_Buchung()
    {
        var (_, _, flex, freizeit, _, _, _) = await SeedMasterDataAsync();

        var response = await _client.PostAsJsonAsync("/api/v1/reassignments", new
        {
            id = "re-n1",
            date = "2026-11-06",
            fromPoolId = flex,
            toPoolId = freizeit,
            amount = 5_000,
            accounts = new object?[] { null },
            note = (string?)null
        });
        await AssertValidationErrorAsync(response, "accounts");

        var list = await GetBookingsAsync();
        Assert.Equal(0, list.GetArrayLength());
    }

    [Fact]
    public async Task P2_Korrektur_splits_NullElement_400_Revision_verbraucht_nicht()
    {
        var (acc1, _, flex, _, catA, _, _) = await SeedMasterDataAsync();
        await PostAsync("/api/v1/expenses", new
        {
            id = "exp-1",
            date = "2026-11-10",
            totalAmount = 8_500,
            accountId = acc1,
            note = (string?)null,
            splits = new object[]
            {
                new { id = "sp-1", amount = 8_500, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null }
            },
            expectedBaseRevision = (int?)null
        });

        var response = await _client.PostAsJsonAsync(
            "/api/v1/bookings/exp-1/corrections", new
        {
            operationId = "corr-n1",
            expectedBaseRevision = 1,
            date = (string?)null,
            note = (string?)null,
            accountId = (string?)null,
            totalAmount = 8_000,
            splits = new object?[] { null },
            allocations = (object[]?)null
        });
        await AssertValidationErrorAsync(response, "splits");

        // Die Revision blieb unverbraucht: dieselbe Korrektur mit
        // erwarteter Revision 1 geht jetzt (mit gültigen Splits) durch.
        var original = (await (await _client.GetAsync("/api/v1/bookings/exp-1"))
            .Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(1, original.GetProperty("baseRevision").GetInt32());

        var wiederholt = await _client.PostAsJsonAsync(
            "/api/v1/bookings/exp-1/corrections", new
        {
            operationId = "corr-n2",
            expectedBaseRevision = 1,
            date = (string?)null,
            note = (string?)null,
            accountId = (string?)null,
            totalAmount = 8_000,
            splits = new object[]
            {
                new { id = "sp-1", amount = 8_000, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null }
            },
            allocations = (object[]?)null
        });
        // 201: der erste erfolgreiche Aufruf legt die Korrektur-Buchung an.
        Assert.Equal(HttpStatusCode.Created, wiederholt.StatusCode);
    }

    [Fact]
    public async Task P2_Erstattung_lines_fehlt_null_oder_NullElement_400_keine_Buchung()
    {
        var (acc1, _, flex, _, catA, _, _) = await SeedMasterDataAsync();
        await PostAsync("/api/v1/expenses", new
        {
            id = "exp-1",
            date = "2026-11-10",
            totalAmount = 8_500,
            accountId = acc1,
            note = (string?)null,
            splits = new object[]
            {
                new { id = "sp-1", amount = 8_500, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null }
            },
            expectedBaseRevision = (int?)null
        });

        var fehlt = await _client.PostAsJsonAsync("/api/v1/bookings/exp-1/refunds", new
        {
            id = "ref-n1",
            accountId = acc1,
            date = "2026-11-20",
            note = (string?)null
        });
        await AssertValidationErrorAsync(fehlt, "lines");

        var nullListe = await _client.PostAsJsonAsync("/api/v1/bookings/exp-1/refunds", new
        {
            id = "ref-n2",
            accountId = acc1,
            date = "2026-11-20",
            lines = (object[]?)null,
            note = (string?)null
        });
        await AssertValidationErrorAsync(nullListe, "lines");

        var nullElement = await _client.PostAsJsonAsync("/api/v1/bookings/exp-1/refunds", new
        {
            id = "ref-n3",
            accountId = acc1,
            date = "2026-11-20",
            lines = new object?[] { null },
            note = (string?)null
        });
        await AssertValidationErrorAsync(nullElement, "lines");

        // Nur die Ausgangs-Ausgabe existiert.
        var list = await GetBookingsAsync();
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("exp-1", list.EnumerateArray().Single()
            .GetProperty("id").GetString());
    }

    // ---------- P3: Storno-Total = Negation des Original-Totals ----------

    [Fact]
    public async Task P3_Storno_Ausgabe_Total_negiert_Organal()
    {
        var (acc1, _, flex, _, catA, _, _) = await SeedMasterDataAsync();
        await PostAsync("/api/v1/expenses", new
        {
            id = "exp-1",
            date = "2026-11-10",
            totalAmount = 8_500,
            accountId = acc1,
            note = (string?)null,
            splits = new object[]
            {
                new { id = "sp-1", amount = 8_500, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null }
            },
            expectedBaseRevision = (int?)null
        });

        // Vorher (Review-Befund): Kontoachse +8500 und Poolachse −8500
        // wurden addiert → +17.000. Erwartet: −8.500.
        var response = await _client.PostAsJsonAsync(
            "/api/v1/bookings/exp-1/reversals", new
        {
            id = "rev-1",
            expectedBaseRevision = 1,
            date = "2026-11-11",
            note = (string?)null
        });
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(-8_500, created.GetProperty("booking")
            .GetProperty("totalAmount").GetInt64());

        // Detail-Endpunkt.
        var detail = (await (await _client.GetAsync("/api/v1/bookings/rev-1"))
            .Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(-8_500, detail.GetProperty("totalAmount").GetInt64());

        // Liste: Storno −8.500, Original unverändert +8.500.
        var items = (await GetBookingsAsync()).EnumerateArray()
            .ToDictionary(b => b.GetProperty("id").GetString()!);
        Assert.Equal(-8_500, items["rev-1"].GetProperty("totalAmount").GetInt64());
        Assert.Equal(8_500, items["exp-1"].GetProperty("totalAmount").GetInt64());
    }

    [Fact]
    public async Task P3_Storno_Einnahme_Total_negiert_Organal()
    {
        var (acc1, _, flex, _, _, _, _) = await SeedMasterDataAsync();
        await PostAsync("/api/v1/income", new
        {
            id = "inc-1",
            date = "2026-11-30",
            amount = 10_000,
            accountId = acc1,
            note = "Gehalt",
            fundingMonth = (string?)null,
            allocations = new object[] { new { poolId = flex, amount = 10_000 } },
            expectedBaseRevision = (int?)null
        });

        var response = await _client.PostAsJsonAsync(
            "/api/v1/bookings/inc-1/reversals", new
        {
            id = "rev-1",
            expectedBaseRevision = 1,
            date = "2026-12-01",
            note = (string?)null
        });
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(-10_000, created.GetProperty("booking")
            .GetProperty("totalAmount").GetInt64());

        var detail = (await (await _client.GetAsync("/api/v1/bookings/rev-1"))
            .Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(-10_000, detail.GetProperty("totalAmount").GetInt64());
    }

    [Fact]
    public async Task P3_Storno_Transfer_Total_negiert_Organal()
    {
        var (acc1, acc2, flex, _, _, _, _) = await SeedMasterDataAsync();
        await PostAsync("/api/v1/opening-balances", new
        {
            id = "init-1",
            date = "2026-11-01",
            total = 500_000,
            accountId = acc1,
            distribution = new object[] { new { poolId = flex, amount = 500_000 } },
            note = (string?)null
        });
        await PostAsync("/api/v1/transfers", new
        {
            id = "tr-1",
            date = "2026-11-05",
            fromAccountId = acc1,
            toAccountId = acc2,
            amount = 20_000,
            purposes = (object[]?)null,
            relatedSettlementId = (string?)null,
            note = (string?)null
        });

        var response = await _client.PostAsJsonAsync(
            "/api/v1/bookings/tr-1/reversals", new
        {
            id = "rev-1",
            expectedBaseRevision = 1,
            date = "2026-11-06",
            note = (string?)null
        });
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(-20_000, created.GetProperty("booking")
            .GetProperty("totalAmount").GetInt64());

        var detail = (await (await _client.GetAsync("/api/v1/bookings/rev-1"))
            .Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(-20_000, detail.GetProperty("totalAmount").GetInt64());
    }

    [Fact]
    public async Task P3_Storno_Umwidmung_Total_negiert_Organal()
    {
        var (acc1, _, flex, freizeit, _, _, _) = await SeedMasterDataAsync();
        await PostAsync("/api/v1/opening-balances", new
        {
            id = "init-1",
            date = "2026-11-01",
            total = 500_000,
            accountId = acc1,
            distribution = new object[]
            {
                new { poolId = flex, amount = 300_000 },
                new { poolId = freizeit, amount = 200_000 }
            },
            note = (string?)null
        });
        await PostAsync("/api/v1/reassignments", new
        {
            id = "re-1",
            date = "2026-11-06",
            fromPoolId = flex,
            toPoolId = freizeit,
            amount = 15_000,
            accounts = (object[]?)null,
            note = (string?)null
        });

        var response = await _client.PostAsJsonAsync(
            "/api/v1/bookings/re-1/reversals", new
        {
            id = "rev-1",
            expectedBaseRevision = 1,
            date = "2026-11-07",
            note = (string?)null
        });
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(-15_000, created.GetProperty("booking")
            .GetProperty("totalAmount").GetInt64());

        var detail = (await (await _client.GetAsync("/api/v1/bookings/rev-1"))
            .Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(-15_000, detail.GetProperty("totalAmount").GetInt64());
    }

    [Fact]
    public async Task P3_Widerruf_von_Storno_Total_wieder_positiv()
    {
        var (acc1, _, flex, _, catA, _, _) = await SeedMasterDataAsync();
        await PostAsync("/api/v1/expenses", new
        {
            id = "exp-1",
            date = "2026-11-10",
            totalAmount = 8_500,
            accountId = acc1,
            note = (string?)null,
            splits = new object[]
            {
                new { id = "sp-1", amount = 8_500, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null }
            },
            expectedBaseRevision = (int?)null
        });
        await PostAsync("/api/v1/bookings/exp-1/reversals", new
        {
            id = "rev-1",
            expectedBaseRevision = 1,
            date = "2026-11-11",
            note = "Doppelte Buchung"
        });

        // Widerruf des Stornos: Negation von −8.500 → +8.500. Die
        // Revision ist die Buchungsrevision des Stornos (Start 1).
        var response = await _client.PostAsJsonAsync(
            "/api/v1/bookings/rev-1/reversals", new
        {
            id = "rev-2",
            expectedBaseRevision = 1,
            date = "2026-11-12",
            note = "Storno war falsch"
        });
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(8_500, created.GetProperty("booking")
            .GetProperty("totalAmount").GetInt64());

        // Original und erstes Storno unverändert.
        var items = (await GetBookingsAsync()).EnumerateArray()
            .ToDictionary(b => b.GetProperty("id").GetString()!);
        Assert.Equal(8_500, items["exp-1"].GetProperty("totalAmount").GetInt64());
        Assert.Equal(-8_500, items["rev-1"].GetProperty("totalAmount").GetInt64());
        Assert.Equal(8_500, items["rev-2"].GetProperty("totalAmount").GetInt64());
    }

    [Fact]
    public async Task P3_Kontostandskorrektur_ist_nicht_stornierbar()
    {
        // Design Schritt 6 §3.2: Kontostandskorrekturen werden durch eine
        // neue, dokumentierte Korrektur ersetzt — nicht storniert. Der
        // Negationsvertrag P3 greift für sie daher nicht.
        var (acc1, _, _, _, _, _, _) = await SeedMasterDataAsync();
        await PostAsync("/api/v1/balance-corrections", new
        {
            id = "bc-1",
            accountId = acc1,
            amount = 500,
            date = "2026-11-15",
            note = "Bankdifferenz"
        });

        var response = await _client.PostAsJsonAsync(
            "/api/v1/bookings/bc-1/reversals", new
        {
            id = "rev-1",
            expectedBaseRevision = 1,
            date = "2026-11-16",
            note = (string?)null
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Die Kontostandskorrektur bleibt bestehen.
        var list = await GetBookingsAsync();
        Assert.Equal(1, list.GetArrayLength());
    }

    // ---------- Helfer ----------

    private static async Task AssertValidationErrorAsync(
        HttpResponseMessage response, string expectedField)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(body);
        Assert.Equal("validation_error", body!.Error.Code);
        Assert.Contains(expectedField, body.Error.Message);
    }

    private async Task<JsonElement> PostAsync(string url, object payload)
    {
        var response = await _client.PostAsJsonAsync(url, payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private async Task<JsonElement> GetBookingsAsync()
    {
        var response = await _client.GetAsync("/api/v1/bookings");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private async Task<(string acc1, string acc2, string flex, string freizeit,
        string catA, string catB, string tax19)> SeedMasterDataAsync()
    {
        var client = CreateAdminClient();
        var account = (await (await client.PostAsJsonAsync("/api/v1/accounts", new
        {
            id = "acc-1",
            name = "Konto Eins",
            type = "bank",
            currency = "EUR",
            color = (string?)null
        })).Content.ReadFromJsonAsync<JsonElement>())!;
        var account2 = (await (await client.PostAsJsonAsync("/api/v1/accounts", new
        {
            id = "acc-2",
            name = "Konto Zwei",
            type = "savings",
            currency = "EUR",
            color = (string?)null
        })).Content.ReadFromJsonAsync<JsonElement>())!;

        var poolFlex = (await (await client.PostAsJsonAsync("/api/v1/pools", new
        {
            id = "flex",
            name = "Pool Flex",
            type = "flexible",
            currency = "EUR",
            color = (string?)null
        })).Content.ReadFromJsonAsync<JsonElement>())!;
        var poolFreizeit = (await (await client.PostAsJsonAsync("/api/v1/pools", new
        {
            id = "freizeit",
            name = "Pool Freizeit",
            type = "flexible",
            currency = "EUR",
            color = (string?)null
        })).Content.ReadFromJsonAsync<JsonElement>())!;

        var catA = (await (await client.PostAsJsonAsync("/api/v1/categories", new
        {
            id = "cat-a",
            name = "Kategorie A",
            type = "expense",
            parentId = (string?)null,
            color = (string?)null
        })).Content.ReadFromJsonAsync<JsonElement>())!;
        var catB = (await (await client.PostAsJsonAsync("/api/v1/categories", new
        {
            id = "cat-b",
            name = "Kategorie B",
            type = "expense",
            parentId = (string?)null,
            color = (string?)null
        })).Content.ReadFromJsonAsync<JsonElement>())!;

        var tax19 = (await (await client.PostAsJsonAsync("/api/v1/tax-categories", new
        {
            id = "tax-19",
            name = "Steuer 19",
            rate = 19.0
        })).Content.ReadFromJsonAsync<JsonElement>())!;

        return (
            account.GetProperty("id").GetString()!,
            account2.GetProperty("id").GetString()!,
            poolFlex.GetProperty("id").GetString()!,
            poolFreizeit.GetProperty("id").GetString()!,
            catA.GetProperty("id").GetString()!,
            catB.GetProperty("id").GetString()!,
            tax19.GetProperty("id").GetString()!);
    }

    private HttpClient CreateAdminClient()
    {
        var client = _factory.CreateClient();
        var login = LoginAsync(client).GetAwaiter().GetResult();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.Token);
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
