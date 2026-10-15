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
/// Journal-Schreibendpunkte (Schritt 7, T4): Ausgaben, Einnahmen,
/// Anfangsbestände, Transfers/Umverteilungen, Korrekturen, Stornos,
/// Erstattungen, Kontostandskorrekturen — über die HTTP-Oberfläche
/// gegen die Application-Services (Idempotenz, Validierung, Revision,
/// Fachregeln bleiben serverseitig).
///
/// Abgedeckt sind die Referenzfälle aus dem Plan „Prüfen" (A3):
/// Korrektur 85 € → 80 €, Teilrückzahlung 10 €, Korrektur nach
/// beglichem Ausgleich (neuer Ausgleichsbedarf), Kontostandskorrektur
/// als dokumentierte Differenz (nicht als Einnahme) — plus das
/// Fehlerformat (400/409) und die Idempotenz.
/// </summary>
public class JournalWriteApiTests : IDisposable
{
    private readonly LarioApiFactory _factory;
    private readonly HttpClient _client;

    public JournalWriteApiTests()
    {
        _factory = new LarioApiFactory();

        var login = LoginAsync(_factory.CreateClient()).GetAwaiter().GetResult();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.Token);
    }

    public void Dispose() => _factory.Dispose();

    // ---------- Ausgabe: Anlegen, Idempotenz, Validierung ----------

    [Fact]
    public async Task Ausgabe_anlegen_liefert_Detail_mit_Splits_Steuer_und_Namen()
    {
        var (acc1, _, flex, freizeit, catA, catB, tax19) = await SeedMasterDataAsync();

        var response = await _client.PostAsJsonAsync("/api/v1/expenses", new
        {
            id = "exp-1",
            date = "2026-11-10",
            totalAmount = 8_500,
            accountId = acc1,
            note = "Miete + Strom",
            splits = new object[]
            {
                new { id = "sp-1", amount = 6_500, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null },
                new
                {
                    id = "sp-2",
                    amount = 2_000,
                    poolId = freizeit,
                    categoryId = catB,
                    tax = new
                    {
                        relevant = true,
                        taxCategoryId = tax19,
                        markedAmount = 1_900,
                        note = (string?)null
                    }
                }
            },
            expectedBaseRevision = (int?)null
        });
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<JsonElement>())!;

        Assert.False(created.GetProperty("idempotent").GetBoolean());
        var booking = created.GetProperty("booking");
        Assert.Equal("exp-1", booking.GetProperty("id").GetString());
        Assert.Equal("Expense", booking.GetProperty("type").GetString());
        Assert.Equal(8_500, booking.GetProperty("totalAmount").GetInt64());
        Assert.Equal(1, booking.GetProperty("baseRevision").GetInt32());
        Assert.Equal(2, booking.GetProperty("splits").GetArrayLength());
        var splits = booking.GetProperty("splits").EnumerateArray().ToList();
        Assert.Equal("sp-2", splits[1].GetProperty("id").GetString());
        Assert.Equal("Pool Freizeit", splits[1].GetProperty("poolName").GetString());
        Assert.Equal("Kategorie B", splits[1].GetProperty("categoryName").GetString());
        var tax = splits[1].GetProperty("tax");
        Assert.True(tax.GetProperty("relevant").GetBoolean());
        Assert.Equal("Steuer 19", tax.GetProperty("taxCategoryName").GetString());
        Assert.Equal(1_900, tax.GetProperty("markedAmount").GetInt64());
        // Das Konto zahlt 85,00 €.
        Assert.Equal(-8_500, booking.GetProperty("accountPostings").EnumerateArray()
            .Single().GetProperty("amount").GetInt64());

        // Lesen über den Read-Endpunkt liefert dasselbe.
        var read = (await (await _client.GetAsync("/api/v1/bookings/exp-1"))
            .Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(8_500, read.GetProperty("totalAmount").GetInt64());
    }

    [Fact]
    public async Task Ausgabe_Wiederholung_gleiche_Inhalt_idempotent_keine_zweite_Buchung()
    {
        var (acc1, _, flex, _, catA, _, _) = await SeedMasterDataAsync();
        var payload = new
        {
            id = "exp-1",
            date = "2026-11-10",
            totalAmount = 8_500,
            accountId = acc1,
            note = "Miete",
            splits = new object[]
            {
                new { id = "sp-1", amount = 8_500, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null }
            },
            expectedBaseRevision = (int?)null
        };

        var erste = await _client.PostAsJsonAsync("/api/v1/expenses", payload);
        erste.EnsureSuccessStatusCode();
        Assert.False((await erste.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("idempotent").GetBoolean());

        var zweite = await _client.PostAsJsonAsync("/api/v1/expenses", payload);
        zweite.EnsureSuccessStatusCode();
        var second = (await zweite.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.True(second.GetProperty("idempotent").GetBoolean());
        Assert.Equal("exp-1", second.GetProperty("booking").GetProperty("id").GetString());

        var list = (await (await _client.GetAsync("/api/v1/bookings"))
            .Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(1, list.GetArrayLength());
    }

    [Fact]
    public async Task Ausgabe_gleiche_Id_andere_Inhalt_409_domain_conflict()
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

        var response = await _client.PostAsJsonAsync("/api/v1/expenses", new
        {
            id = "exp-1",
            date = "2026-11-10",
            totalAmount = 9_000,
            accountId = acc1,
            note = (string?)null,
            splits = new object[]
            {
                new { id = "sp-1", amount = 9_000, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null }
            },
            expectedBaseRevision = (int?)null
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Lario.Host.Api.ErrorResponse>();
        Assert.Equal("domain_conflict", body!.Error.Code);
    }

    [Fact]
    public async Task Ausgabe_SplitSumme_ungleich_Gesamt_400()
    {
        var (acc1, _, flex, _, catA, _, _) = await SeedMasterDataAsync();

        var response = await _client.PostAsJsonAsync("/api/v1/expenses", new
        {
            id = "exp-1",
            date = "2026-11-10",
            totalAmount = 10_000,
            accountId = acc1,
            note = (string?)null,
            splits = new object[]
            {
                new { id = "sp-1", amount = 8_500, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null }
            },
            expectedBaseRevision = (int?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Lario.Host.Api.ErrorResponse>();
        Assert.Equal("validation_error", body!.Error.Code);
        Assert.Contains("85,00", body.Error.Message);
    }

    [Fact]
    public async Task Ausgabe_unbekanntes_Konto_400_invalid_reference()
    {
        var (_, _, flex, _, catA, _, _) = await SeedMasterDataAsync();

        var response = await _client.PostAsJsonAsync("/api/v1/expenses", new
        {
            id = "exp-1",
            date = "2026-11-10",
            totalAmount = 10_000,
            accountId = "acc-unknown",
            note = (string?)null,
            splits = new object[]
            {
                new { id = "sp-1", amount = 10_000, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null }
            },
            expectedBaseRevision = (int?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Lario.Host.Api.ErrorResponse>();
        Assert.Equal("invalid_reference", body!.Error.Code);
    }

    [Fact]
    public async Task Ausgabe_ungültiges_Datum_und_negativer_Betrag_400()
    {
        var (acc1, _, flex, _, catA, _, _) = await SeedMasterDataAsync();

        var datum = await _client.PostAsJsonAsync("/api/v1/expenses", new
        {
            id = "exp-1",
            date = "10.11.2026",
            totalAmount = 10_000,
            accountId = acc1,
            note = (string?)null,
            splits = new object[]
            {
                new { id = "sp-1", amount = 10_000, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null }
            },
            expectedBaseRevision = (int?)null
        });
        Assert.Equal(HttpStatusCode.BadRequest, datum.StatusCode);
        var body = await datum.Content.ReadFromJsonAsync<Lario.Host.Api.ErrorResponse>();
        Assert.Equal("validation_error", body!.Error.Code);
        Assert.Contains("yyyy-MM-dd", body.Error.Message);

        var negativ = await _client.PostAsJsonAsync("/api/v1/expenses", new
        {
            id = "exp-2",
            date = "2026-11-10",
            totalAmount = -5_000,
            accountId = acc1,
            note = (string?)null,
            splits = new object[]
            {
                new { id = "sp-1", amount = -5_000, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null }
            },
            expectedBaseRevision = (int?)null
        });
        Assert.Equal(HttpStatusCode.BadRequest, negativ.StatusCode);
    }

    // ---------- Einnahme / Anfangsbestand / Transfers ----------

    [Fact]
    public async Task Einnahme_anlegen_mit_Finanzierungsmonat_und_Verteilung()
    {
        var (acc1, _, flex, _, _, _, _) = await SeedMasterDataAsync();

        var response = await _client.PostAsJsonAsync("/api/v1/income", new
        {
            id = "inc-1",
            date = "2026-11-30",
            amount = 2_000_000,
            accountId = acc1,
            note = "Gehalt",
            fundingMonth = (string?)null,
            allocations = new object[] { new { poolId = flex, amount = 2_000_000 } },
            expectedBaseRevision = (int?)null
        });
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<JsonElement>())!;

        Assert.False(created.GetProperty("idempotent").GetBoolean());
        // Standardregel: Ende November finanziert den Folgemonat.
        Assert.Equal("2026-12", created.GetProperty("booking").GetProperty("fundingMonth").GetString());

        var balances = await GetBalancesAsync();
        Assert.Equal(2_000_000, balances.GetProperty("totalIncome").GetInt64());
        var pools = balances.GetProperty("pools").EnumerateArray()
            .ToDictionary(p => p.GetProperty("poolId").GetString()!,
                          p => p.GetProperty("balance").GetInt64());
        Assert.Equal(2_000_000, pools[flex]);
    }

    [Fact]
    public async Task Anfangsbestand_teilweise_verteilt_Rest_unzugeordnet()
    {
        var (acc1, _, flex, _, _, _, _) = await SeedMasterDataAsync();

        var response = await _client.PostAsJsonAsync("/api/v1/opening-balances", new
        {
            id = "init-1",
            date = "2026-11-01",
            total = 1_000_000,
            accountId = acc1,
            distribution = new object[] { new { poolId = flex, amount = 600_000 } },
            note = (string?)null
        });
        response.EnsureSuccessStatusCode();

        var balances = await GetBalancesAsync();
        var unallocated = balances.GetProperty("unallocatedOpeningBalances")
            .EnumerateArray().Single();
        Assert.Equal("init-1", unallocated.GetProperty("openingBalanceId").GetString());
        Assert.Equal(400_000, unallocated.GetProperty("unallocated").GetInt64());
        Assert.Equal(1_000_000, balances.GetProperty("totalAccountBalance").GetInt64());
        Assert.Equal(600_000, balances.GetProperty("totalPoolBalance").GetInt64());
        Assert.Equal(400_000, balances.GetProperty("accountPoolGap").GetInt64());
    }

    [Fact]
    public async Task Transfer_Umverteilung_und_TransferUmverteilung_anlegen()
    {
        var (acc1, acc2, flex, freizeit, _, _, _) = await SeedMasterDataAsync();
        await PostAsync("/api/v1/opening-balances", new
        {
            id = "init-1",
            date = "2026-11-01",
            total = 500_000,
            accountId = acc1,
            distribution = new object[] { new { poolId = flex, amount = 500_000 } },
            note = (string?)null
        });

        // Transfer mit zweckgebundener Verteilung.
        var transfer = await _client.PostAsJsonAsync("/api/v1/transfers", new
        {
            id = "tr-1",
            date = "2026-11-05",
            fromAccountId = acc1,
            toAccountId = acc2,
            amount = 100_000,
            purposes = new object[] { new { poolId = freizeit, amount = 100_000 } },
            relatedSettlementId = (string?)null,
            note = "Spargeld anlegen"
        });
        transfer.EnsureSuccessStatusCode();
        Assert.False((await transfer.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("idempotent").GetBoolean());

        // Umverteilung Pool → Pool.
        var reassignment = await _client.PostAsJsonAsync("/api/v1/reassignments", new
        {
            id = "re-1",
            date = "2026-11-06",
            fromPoolId = flex,
            toPoolId = freizeit,
            amount = 50_000,
            accounts = (object[]?)null,
            note = (string?)null
        });
        reassignment.EnsureSuccessStatusCode();

        // Transfer-Umverteilung.
        var combo = await _client.PostAsJsonAsync("/api/v1/transfer-reassignments", new
        {
            id = "trr-1",
            date = "2026-11-07",
            fromAccountId = acc2,
            toAccountId = acc1,
            fromPoolId = freizeit,
            toPoolId = flex,
            amount = 20_000,
            note = (string?)null
        });
        combo.EnsureSuccessStatusCode();

        // Kontostände: 5000 − 1000 (Transfer) + 200 (Rücktransfer) = 4200 €.
        var balances = await GetBalancesAsync();
        var accounts = balances.GetProperty("accounts").EnumerateArray()
            .ToDictionary(a => a.GetProperty("accountId").GetString()!,
                          a => a.GetProperty("balance").GetInt64());
        Assert.Equal(420_000, accounts[acc1]);
        Assert.Equal(80_000, accounts[acc2]);

        // Poolbestände: Flex 5000 − 500 (Umverteilung) + 200 = 4700 €,
        // Freizeit 500 (Umverteilung) − 200 = 300 €. Der Transfer-Zweck
        // (1000 € Freizeit) dokumentiert die Konto-Pool-Zuordnung, bewegt
        // aber keine Poolbestände (Schritt-5-Design: „Poolbestände
        // bleiben unverändert").
        var pools = balances.GetProperty("pools").EnumerateArray()
            .ToDictionary(p => p.GetProperty("poolId").GetString()!,
                          p => p.GetProperty("balance").GetInt64());
        Assert.Equal(470_000, pools[flex]);
        Assert.Equal(30_000, pools[freizeit]);
    }

    // ---------- Korrektur (Referenzfall A3) ----------

    [Fact]
    public async Task Korrektur_85_auf_80_keine_zweite_Buchung_Saldo_korrekt()
    {
        var (acc1, _, flex, freizeit, catA, catB, _) = await SeedMasterDataAsync();
        await PostAsync("/api/v1/expenses", new
        {
            id = "exp-1",
            date = "2026-11-10",
            totalAmount = 8_500,
            accountId = acc1,
            note = "Miete + Strom",
            splits = new object[]
            {
                new { id = "sp-1", amount = 6_500, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null },
                new { id = "sp-2", amount = 2_000, poolId = freizeit, categoryId = catB, note = (string?)null, tax = (object?)null }
            },
            expectedBaseRevision = (int?)null
        });
        var before = await GetBalancesAsync();

        // 85 € → 80 €: Splits angepasst (6000 + 2000), Revision 1 erwartet.
        var response = await _client.PostAsJsonAsync(
            "/api/v1/bookings/exp-1/corrections", new
        {
            operationId = "corr-1",
            expectedBaseRevision = 1,
            date = (string?)null,
            note = "Stromrechnung kam höher",
            accountId = (string?)null,
            totalAmount = 8_000,
            splits = new object[]
            {
                new { id = "sp-1", amount = 6_000, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null },
                new { id = "sp-2", amount = 2_000, poolId = freizeit, categoryId = catB, note = (string?)null, tax = (object?)null }
            },
            allocations = (object[]?)null
        });
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.False(created.GetProperty("idempotent").GetBoolean());
        Assert.Equal(8_000, created.GetProperty("booking").GetProperty("totalAmount").GetInt64());
        Assert.Equal(2, created.GetProperty("booking").GetProperty("baseRevision").GetInt32());

        // Keine zweite Buchung.
        var list = (await (await _client.GetAsync("/api/v1/bookings"))
            .Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(1, list.GetArrayLength());

        // Saldo: Konto 50 € höher als vor der Korrektur (5 € weniger
        // ausgegeben), Flex-Pool 50 € höher.
        var after = await GetBalancesAsync();
        var beforeAccounts = before.GetProperty("accounts").EnumerateArray()
            .ToDictionary(a => a.GetProperty("accountId").GetString()!,
                          a => a.GetProperty("balance").GetInt64());
        var afterAccounts = after.GetProperty("accounts").EnumerateArray()
            .ToDictionary(a => a.GetProperty("accountId").GetString()!,
                          a => a.GetProperty("balance").GetInt64());
        Assert.Equal(beforeAccounts[acc1] + 500, afterAccounts[acc1]);
        var beforeFlex = before.GetProperty("pools").EnumerateArray()
            .First(p => p.GetProperty("poolId").GetString() == flex)
            .GetProperty("balance").GetInt64();
        var afterFlex = after.GetProperty("pools").EnumerateArray()
            .First(p => p.GetProperty("poolId").GetString() == flex)
            .GetProperty("balance").GetInt64();
        Assert.Equal(beforeFlex + 500, afterFlex);
    }

    [Fact]
    public async Task Korrektur_falsche_Revision_409_mit_Werten()
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
            operationId = "corr-1",
            expectedBaseRevision = 7,
            date = (string?)null,
            note = "Zu spät",
            accountId = (string?)null,
            totalAmount = 8_000,
            splits = (object[]?)null,
            allocations = (object[]?)null
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Lario.Host.Api.ErrorResponse>();
        Assert.Equal("domain_conflict", body!.Error.Code);
        Assert.Equal(7, body.Error.ExpectedBaseRevision);
        Assert.Equal(1, body.Error.ActualBaseRevision);
    }

    [Fact]
    public async Task Korrektur_Wiederholung_gleiche_Inhalt_idempotent()
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
        var command = new
        {
            operationId = "corr-1",
            expectedBaseRevision = 1,
            date = (string?)null,
            note = "Korrigiert",
            accountId = (string?)null,
            totalAmount = 8_000,
            splits = new object[]
            {
                new { id = "sp-1", amount = 8_000, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null }
            },
            allocations = (object[]?)null
        };

        var erste = await _client.PostAsJsonAsync(
            "/api/v1/bookings/exp-1/corrections", command);
        erste.EnsureSuccessStatusCode();
        var zweite = await _client.PostAsJsonAsync(
            "/api/v1/bookings/exp-1/corrections", command);
        zweite.EnsureSuccessStatusCode();
        Assert.True((await zweite.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("idempotent").GetBoolean());
    }

    // ---------- Storno ----------

    [Fact]
    public async Task Storno_anlegen_Original_storniert_wiederholtes_Storno_409()
    {
        var (acc1, _, flex, freizeit, catA, _, _) = await SeedMasterDataAsync();
        await PostAsync("/api/v1/expenses", new
        {
            id = "exp-1",
            date = "2026-11-10",
            totalAmount = 8_500,
            accountId = acc1,
            note = (string?)null,
            splits = new object[]
            {
                new { id = "sp-1", amount = 6_500, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null },
                new { id = "sp-2", amount = 2_000, poolId = freizeit, categoryId = catA, note = (string?)null, tax = (object?)null }
            },
            expectedBaseRevision = (int?)null
        });

        var response = await _client.PostAsJsonAsync(
            "/api/v1/bookings/exp-1/reversals", new
        {
            id = "rev-1",
            expectedBaseRevision = 1,
            date = "2026-11-11",
            note = "Doppelte Buchung"
        });
        response.EnsureSuccessStatusCode();
        Assert.False((await response.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("idempotent").GetBoolean());

        // Original wirksam storniert; die Buchung ist wirkungslos —
        // Kontostand und Poolbestände kehren auf den Stand vor der
        // Ausgabe zurück (0 €).
        var original = (await (await _client.GetAsync("/api/v1/bookings/exp-1"))
            .Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.True(original.GetProperty("effectivelyReversed").GetBoolean());
        var after = await GetBalancesAsync();
        Assert.Equal(0, after.GetProperty("totalPoolBalance").GetInt64());
        Assert.Equal(0, after.GetProperty("totalAccountBalance").GetInt64());

        // Wiederholtes Storno desselben Originals: abgelehnt (das
        // Original ist wirksam storniert).
        var doppelt = await _client.PostAsJsonAsync(
            "/api/v1/bookings/exp-1/reversals", new
        {
            id = "rev-2",
            expectedBaseRevision = 2,
            date = "2026-11-11",
            note = "Nochmal"
        });
        Assert.Equal(HttpStatusCode.Conflict, doppelt.StatusCode);
    }

    // ---------- Erstattung (Referenzfall A3) ----------

    [Fact]
    public async Task Teilrueckzahlung_10_Konto_und_Ursprungspool_steigen()
    {
        var (acc1, _, flex, freizeit, catA, _, _) = await SeedMasterDataAsync();
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
        await PostAsync("/api/v1/expenses", new
        {
            id = "exp-1",
            date = "2026-11-10",
            totalAmount = 8_500,
            accountId = acc1,
            note = (string?)null,
            splits = new object[]
            {
                new { id = "sp-1", amount = 6_500, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null },
                new { id = "sp-2", amount = 2_000, poolId = freizeit, categoryId = catA, note = (string?)null, tax = (object?)null }
            },
            expectedBaseRevision = (int?)null
        });
        var before = await GetBalancesAsync();

        // Teilrückzahlung 10 €: 8 € an Flex, 2 € an Freizeit (Cent;
        // jede Linie innerhalb ihres pro-Pool-Deckels: 800 ≤ 6500,
        // 200 ≤ 2000).
        var response = await _client.PostAsJsonAsync(
            "/api/v1/bookings/exp-1/refunds", new
        {
            id = "ref-1",
            accountId = acc1,
            date = "2026-11-20",
            lines = new object[]
            {
                new { poolId = flex, amount = 800 },
                new { poolId = freizeit, amount = 200 }
            },
            note = "Teilrückzahlung"
        });
        response.EnsureSuccessStatusCode();
        Assert.False((await response.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("idempotent").GetBoolean());

        var after = await GetBalancesAsync();
        Assert.Equal(before.GetProperty("totalAccountBalance").GetInt64() + 1_000,
            after.GetProperty("totalAccountBalance").GetInt64());
        var beforePools = before.GetProperty("pools").EnumerateArray()
            .ToDictionary(p => p.GetProperty("poolId").GetString()!,
                          p => p.GetProperty("balance").GetInt64());
        var afterPools = after.GetProperty("pools").EnumerateArray()
            .ToDictionary(p => p.GetProperty("poolId").GetString()!,
                          p => p.GetProperty("balance").GetInt64());
        Assert.Equal(beforePools[flex] + 800, afterPools[flex]);
        Assert.Equal(beforePools[freizeit] + 200, afterPools[freizeit]);

        // Eine Erstattung ist keine Einnahme.
        Assert.Equal(0, after.GetProperty("totalIncome").GetInt64());
    }

    [Fact]
    public async Task Erstattung_ueber_Deckel_409()
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

        // 90 € > 85 € Splitbetrag → über dem Deckel (Fachregel → 400).
        var response = await _client.PostAsJsonAsync(
            "/api/v1/bookings/exp-1/refunds", new
        {
            id = "ref-1",
            accountId = acc1,
            date = "2026-11-20",
            lines = new object[] { new { poolId = flex, amount = 9_000 } },
            note = (string?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Lario.Host.Api.ErrorResponse>();
        Assert.Equal("validation_error", body!.Error.Code);
        Assert.Contains("Deckelung", body.Error.Message);
    }

    // ---------- Kontostandskorrektur (Referenzfall A3) ----------

    [Fact]
    public async Task Kontostandskorrektur_Differenz_dokumentiert_nicht_Einnahme()
    {
        var (acc1, _, _, _, _, _, _) = await SeedMasterDataAsync();

        // Bankstand 5 € höher als geplant → Differenz +5,00 €.
        var response = await _client.PostAsJsonAsync("/api/v1/balance-corrections", new
        {
            id = "bc-1",
            accountId = acc1,
            amount = 500,
            date = "2026-11-15",
            note = "Bankdifferenz 14.11."
        });
        response.EnsureSuccessStatusCode();
        Assert.False((await response.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("idempotent").GetBoolean());

        var balances = await GetBalancesAsync();
        Assert.Equal(500, balances.GetProperty("totalAccountBalance").GetInt64());
        // Keine Einnahme.
        Assert.Equal(0, balances.GetProperty("totalIncome").GetInt64());
        Assert.Equal(500, balances.GetProperty("accountPoolGap").GetInt64());

        // Ohne Notiz: abgelehnt (dokumentierte Differenz ist Pflicht).
        var ohneNote = await _client.PostAsJsonAsync("/api/v1/balance-corrections", new
        {
            id = "bc-2",
            accountId = acc1,
            amount = 500,
            date = "2026-11-15",
            note = "  "
        });
        Assert.Equal(HttpStatusCode.BadRequest, ohneNote.StatusCode);

        // Nullbetrag: abgelehnt (ohne Differenz gibt es nichts zu
        // dokumentieren).
        var nullBetrag = await _client.PostAsJsonAsync("/api/v1/balance-corrections", new
        {
            id = "bc-3",
            accountId = acc1,
            amount = 0,
            date = "2026-11-15",
            note = "Kein Betrag"
        });
        Assert.Equal(HttpStatusCode.BadRequest, nullBetrag.StatusCode);
    }

    // ---------- Korrektur nach beglichem Ausgleich (Referenzfall A3) ----------

    [Fact]
    public async Task Korrektur_nach_beglichem_Ausgleich_erzeugt_neuen_Bedarf()
    {
        var (acc1, acc2, flex, freizeit, catA, _, _) = await SeedMasterDataAsync();
        await PostAsync("/api/v1/opening-balances", new
        {
            id = "init-1",
            date = "2026-11-01",
            total = 500_000,
            accountId = acc1,
            distribution = new object[] { new { poolId = flex, amount = 500_000 } },
            note = (string?)null
        });
        await PostAsync("/api/v1/opening-balances", new
        {
            id = "init-2",
            date = "2026-11-01",
            total = 200_000,
            accountId = acc2,
            distribution = new object[] { new { poolId = freizeit, amount = 200_000 } },
            note = (string?)null
        });

        // Ausgabe von Konto Eins finanziert auch Pool Freizeit (Konto
        // Zwei) → offene Ausgleichsposition.
        await PostAsync("/api/v1/expenses", new
        {
            id = "exp-1",
            date = "2026-11-10",
            totalAmount = 8_500,
            accountId = acc1,
            note = (string?)null,
            splits = new object[]
            {
                new { id = "sp-1", amount = 6_500, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null },
                new { id = "sp-2", amount = 2_000, poolId = freizeit, categoryId = catA, note = (string?)null, tax = (object?)null }
            },
            expectedBaseRevision = (int?)null
        });

        var open = await GetOpenSettlementsAsync();
        Assert.Equal(1, open.GetArrayLength());
        var positionId = open.EnumerateArray().Single().GetProperty("id").GetString()!;

        // Vorfinanzierung ausgleichen (20 € Konto Zwei → Konto Eins,
        // über den Endpunkt verknüpft).
        var transfer = await _client.PostAsJsonAsync("/api/v1/transfers", new
        {
            id = "tr-1",
            date = "2026-11-11",
            fromAccountId = acc2,
            toAccountId = acc1,
            amount = 2_000,
            purposes = (object[]?)null,
            relatedSettlementId = positionId,
            note = "Ausgleich"
        });
        transfer.EnsureSuccessStatusCode();
        Assert.Equal(0, (await GetOpenSettlementsAsync()).GetArrayLength());

        // Korrektur der Ausgabe: 85 € → 110 € (Freizeit-Split 45 €) —
        // der beglichene Ausgleich ist zu klein, es entsteht neuer
        // Bedarf.
        var correction = await _client.PostAsJsonAsync(
            "/api/v1/bookings/exp-1/corrections", new
        {
            operationId = "corr-1",
            expectedBaseRevision = 1,
            date = (string?)null,
            note = "Rechnung kam teurer",
            accountId = (string?)null,
            totalAmount = 11_000,
            splits = new object[]
            {
                new { id = "sp-1", amount = 6_500, poolId = flex, categoryId = catA, note = (string?)null, tax = (object?)null },
                new { id = "sp-2", amount = 4_500, poolId = freizeit, categoryId = catA, note = (string?)null, tax = (object?)null }
            },
            allocations = (object[]?)null
        });
        correction.EnsureSuccessStatusCode();

        var reopened = await GetOpenSettlementsAsync();
        Assert.Equal(1, reopened.GetArrayLength());
        var reopenedPosition = reopened.EnumerateArray().Single();
        Assert.Equal("open", reopenedPosition.GetProperty("status").GetString());
        // Die Position trägt jetzt die korrigierte Gesamtverpflichtung
        // (45 €) und der offene Rest ist um die 25 € Mehrbelastung
        // angewachsen (20 € bereits beglichen, 25 € jetzt offen).
        Assert.Equal(4_500,
            reopenedPosition.GetProperty("amount").GetInt64());
        Assert.Equal(4_500 - 2_000,
            reopenedPosition.GetProperty("restAmount").GetInt64());
    }

    // ---------- Helpers ----------

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
        // Ein Client pro Seed-Datenanfrage über dieselbe Factory —
        // die Stammdaten gehören zur selben Test-App wie _client.
        var client = _factory.CreateClient();
        var login = LoginAsync(client).GetAwaiter().GetResult();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.Token);
        return client;
    }

    private async Task<JsonElement> PostAsync(string url, object payload)
    {
        var response = await _client.PostAsJsonAsync(url, payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private async Task<JsonElement> GetBalancesAsync()
    {
        var response = await _client.GetAsync("/api/v1/balances");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private async Task<JsonElement> GetOpenSettlementsAsync()
    {
        var response = await _client.GetAsync("/api/v1/settlements/open");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

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
