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
/// Regressionstests für PR #9 Review R3:
///
/// P1 — Anfangsbestand: nach verlorener Antwort (Commit, aber keine
///      Antwort) erzeugte die Client-Logik bei erneutem Klick eine neue
///      Operation-Id und damit einen zweiten, doppelten
///      Anfangsbestand. Fix liegt clientseitig (gleiche Id + Payload
///      beim Retry, neue Id erst nach definitiver Ablehnung oder
///      Reset) — hier wird der serverseitige Haltepunkt geprüft:
///      Idempotente Wiederholung desselben Anfangsbestands quittiert
///      ohne zweite Buchung.
///
/// P2 — Erstattung: das Client-Formular setzte pro Split-Zeile den vollen
///      Splitbetrag als Deckel (mehrere Splits desselben Pools wurden
///      nicht aggregiert, bereits erstattete Beträge blieben außer
///      Betracht) — nach einer Teilerstattung ließ sich derselbe Betrag
///      erneut erstatten, bis der serverseitige pro-Pool-Deckel
///      zufällig griff. Fix: Detail trägt <c>refundedByPool</c>
///      (bereits gewährte Erstattungen je Ursprungs-Pool), der Client
///      aggregiert Splits pro Pool und setzt den verbleibenden Deckel.
///      Hier: Detail-Feld, Deckel nach Teilerstattung und Idempotenz.
///
/// P2 — Korrektur: die Client-Vorbelegung baute Split-Ids neu auf und
///      verwies Steuer-Notizen — eine reine Skalar-Korrektur (nur
///      Buchungsnote) ersetzte die Splits still. Fix: Client sendet
///      bei unveränderter Aufteilung <c>splits: null</c> (Server hält
///      die Original-Splits). Hier: Skalar-Korrektur bewahrt
///      Split-Iden und Steuer-Notizen.
/// </summary>
public class Step7ReviewR3Tests : IDisposable
{
    private readonly LarioApiFactory _factory;
    private readonly HttpClient _client;

    public Step7ReviewR3Tests()
    {
        _factory = new LarioApiFactory();
        _client = AuthenticatedClient(_factory);
    }

    public void Dispose() => _factory.Dispose();

    // ---------- P1: Anfangsbestand — Idempotenz des Retries ----------

    [Fact]
    public async Task P1_Anfangsbestand_wiederholt_gleiche_Id_Idempotente_Quittierung()
    {
        var (acc, pool, _, _, _, _) = await SeedAsync();

        // Gleiche Id + gleicher Inhalt (der Client sendet bei verlorener
        // Antwort exakt diese Operation erneut).
        var payload = OpeningBalance(acc, pool, "open-r3-1", 500_000, 300_000);

        var erste = await _client.PostAsJsonAsync("/api/v1/opening-balances", payload);
        Assert.Equal(HttpStatusCode.Created, erste.StatusCode);
        var firstBody = (await erste.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.False(firstBody.GetProperty("idempotent").GetBoolean());

        var zweite = await _client.PostAsJsonAsync("/api/v1/opening-balances", payload);
        Assert.Equal(HttpStatusCode.OK, zweite.StatusCode);
        var secondBody = (await zweite.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.True(secondBody.GetProperty("idempotent").GetBoolean());
        Assert.Equal("open-r3-1", secondBody.GetProperty("booking")
            .GetProperty("id").GetString());

        // Genau ein Anfangsbestand — keine doppelte Buchung.
        var list = await GetBookingsAsync("OpeningBalance");
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal(500_000, list.EnumerateArray().Single()
            .GetProperty("totalAmount").GetInt64());
    }

    [Fact]
    public async Task P1_Anfangsbestand_gleiche_Id_andere_Inhalt_ist_Konflikt()
    {
        var (acc, pool, _, _, _, _) = await SeedAsync();

        await PostAsync(
            "/api/v1/opening-balances", OpeningBalance(acc, pool, "open-r3-2", 500_000, 300_000));

        // Gleiche Id, anderer Inhalt → kein stiller Zweiter, sondern
        // ein sichtbarer Konflikt (die vorhandene bleibt unverändert).
        var response = await _client.PostAsJsonAsync(
            "/api/v1/opening-balances", OpeningBalance(acc, pool, "open-r3-2", 999_000, 300_000));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("domain_conflict", body!.Error.Code);

        var list = await GetBookingsAsync("OpeningBalance");
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal(500_000, list.EnumerateArray().Single()
            .GetProperty("totalAmount").GetInt64());
    }

    // ---------- P2: Erstattung — Deckel und refundedByPool ----------

    [Fact]
    public async Task P2_Detail_traegt_bereits_erstattete_Betraege_pro_Pool()
    {
        var (acc, flex, freizeit, catA, _, _) = await SeedAsync();

        // Ausgabe 85 €: 65 € Flex, 20 € Freizeit (zwei Splits).
        await PostAsync("/api/v1/expenses",
            Expense("exp-r3-1", acc, 8_500, Splits(6_500, flex, catA, 2_000, freizeit, catA)));

        // Vor der Erstattung: leer (kein Array, das Werte suggeriert).
        var vorher = await GetDetailAsync("exp-r3-1");
        Assert.Equal(0, vorher.GetProperty("refundedByPool").GetArrayLength());

        // Teilrückzahlung 10 €: 8 € Flex, 2 € Freizeit.
        await PostAsync("/api/v1/bookings/exp-r3-1/refunds",
            Refund("ref-r3-1", acc, "2026-11-20", Lines(800, flex, 200, freizeit)));

        // Zweite Teilrückzahlung 10 € Flex → Summe je Pool wird
        // aggregiert (nicht nur die letzte Erstattung).
        await PostAsync("/api/v1/bookings/exp-r3-1/refunds",
            Refund("ref-r3-2", acc, "2026-11-21", Lines(1_000, flex)));

        var detail = await GetDetailAsync("exp-r3-1");
        var refunded = detail.GetProperty("refundedByPool").EnumerateArray()
            .ToDictionary(r => r.GetProperty("poolId").GetString()!,
                           r => r.GetProperty("amount").GetInt64());
        Assert.Equal(2, refunded.Count);
        Assert.Equal(1_800, refunded[flex]);
        Assert.Equal(200, refunded[freizeit]);

        // Die Erstattung selbst trägt kein refundedByPool (sie ist
        // keine Ursprungs-Ausgabe).
        var refundDetail = await GetDetailAsync("ref-r3-1");
        Assert.Equal(0, refundDetail.GetProperty("refundedByPool").GetArrayLength());
    }

    [Fact]
    public async Task P2_Nach_Teilerstattung_ist_nur_der_Rest_erstattbar()
    {
        var (acc, flex, _, catA, _, _) = await SeedAsync();

        await PostAsync("/api/v1/expenses",
            Expense("exp-r3-3", acc, 8_500, Splits(8_500, flex, catA, null)));

        // Teilerstattung 50 €.
        await PostAsync("/api/v1/bookings/exp-r3-3/refunds",
            Refund("ref-r3-3-1", acc, "2026-11-20", Lines(5_000, flex)));

        // Der Detail-Endpunkt zeigt die Größenordnung des
        // verbleibenden Deckels: Split-Position 8.500 − erstattet
        // 5.000 = 3.500.
        var detail = await GetDetailAsync("exp-r3-3");
        var refunded = detail.GetProperty("refundedByPool").EnumerateArray().Single();
        Assert.Equal(5_000, refunded.GetProperty("amount").GetInt64());

        // Genau der Rest ist erstattungsfähig (35 € → Deckel erreicht).
        var exakt = await _client.PostAsJsonAsync("/api/v1/bookings/exp-r3-3/refunds",
            Refund("ref-r3-3-2", acc, "2026-11-21", Lines(3_500, flex)));
        Assert.Equal(HttpStatusCode.Created, exakt.StatusCode);

        // Cent darüber → 400 (der alte Bug: das Formular hätte erneut
        // den vollen 85-€-Betrag als Deckel vorgegeben).
        var zuviel = await _client.PostAsJsonAsync("/api/v1/bookings/exp-r3-3/refunds",
            Refund("ref-r3-3-3", acc, "2026-11-22", Lines(1, flex)));
        Assert.Equal(HttpStatusCode.BadRequest, zuviel.StatusCode);
        var body = await zuviel.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("validation_error", body!.Error.Code);
        Assert.Contains("Deckelung", body.Error.Message);

        // Die abgelehnte Erstattung hat nichts verändert.
        var nachher = await GetDetailAsync("exp-r3-3");
        var nachherRefunded = nachher.GetProperty("refundedByPool").EnumerateArray().Single();
        Assert.Equal(8_500, nachherRefunded.GetProperty("amount").GetInt64());
    }

    [Fact]
    public async Task P2_Mehrere_Splits_desselben_Pools_Deckel_gilt_fuer_die_Summe()
    {
        var (acc, flex, _, catA, catB, _) = await SeedAsync();

        // Zwei Splits desselben Pools (je 40 €) → Ursprungsposition
        // des Pools ist 80 €, nicht 40 € je Split.
        await PostAsync("/api/v1/expenses",
            Expense("exp-r3-4", acc, 8_000, Splits(4_000, flex, catA, 4_000, flex, catB)));

        // 80 € (volle Poolposition) ist erstattungsfähig …
        var voll = await _client.PostAsJsonAsync("/api/v1/bookings/exp-r3-4/refunds",
            Refund("ref-r3-4-1", acc, "2026-11-20", Lines(8_000, flex)));
        Assert.Equal(HttpStatusCode.Created, voll.StatusCode);

        // … und ein Cent mehr ist über dem Deckel.
        var zuviel = await _client.PostAsJsonAsync("/api/v1/bookings/exp-r3-4/refunds",
            Refund("ref-r3-4-2", acc, "2026-11-21", Lines(1, flex)));
        Assert.Equal(HttpStatusCode.BadRequest, zuviel.StatusCode);
    }

    [Fact]
    public async Task P2_Idempotente_Wiederholung_der_Erstattung_keine_zweite_Buchung()
    {
        var (acc, flex, _, catA, _, _) = await SeedAsync();

        await PostAsync("/api/v1/expenses",
            Expense("exp-r3-5", acc, 8_500, Splits(8_500, flex, catA, null)));

        var payload = Refund("ref-r3-5", acc, "2026-11-20", Lines(3_000, flex));
        var erste = await _client.PostAsJsonAsync("/api/v1/bookings/exp-r3-5/refunds", payload);
        Assert.Equal(HttpStatusCode.Created, erste.StatusCode);

        // Gleiche Id + Inhalt (Client-Retry nach verlorener Antwort).
        var zweite = await _client.PostAsJsonAsync("/api/v1/bookings/exp-r3-5/refunds", payload);
        Assert.Equal(HttpStatusCode.OK, zweite.StatusCode);
        var body = (await zweite.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.True(body.GetProperty("idempotent").GetBoolean());

        // Der Pool steigt nur einmal (30 €): Ausgangs-Balance
        // −8.500 (Ausgabe) + 3.000 (eine Erstattung) = −5.500 — bei
        // doppelter Buchung wäre es −2.500. Es gibt nur eine Erstattung.
        var balances = await (await _client.GetAsync("/api/v1/balances"))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal(-5_500, balances.GetProperty("pools").EnumerateArray()
            .Single(p => p.GetProperty("poolId").GetString() == flex)
            .GetProperty("balance").GetInt64());
        Assert.Equal(1, (await GetBookingsAsync("Refund")).GetArrayLength());
    }

    // ---------- P2: Korrektur — Splits null bewahrt Ids und Notizen ----------

    [Fact]
    public async Task P2_Skalar_Korrektur_bewahrt_SplitIds_und_Steuernotiz()
    {
        var (acc, flex, freizeit, catA, catB, tax19) = await SeedAsync();

        // Ausgabe 85 €: 65 € Flex (unmarkiert), 20 € Freizeit mit
        // Steuerkennzeichnung inkl. Notiz.
        await PostAsync("/api/v1/expenses", new
        {
            id = "exp-r3-6",
            date = "2026-11-10",
            totalAmount = 8_500,
            accountId = acc,
            note = "Miete + Strom",
            splits = new object[]
            {
                new
                {
                    id = "sp-r3-6-1",
                    amount = 6_500,
                    poolId = flex,
                    categoryId = catA,
                    note = (string?)null,
                    tax = (object?)null
                },
                new
                {
                    id = "sp-r3-6-2",
                    amount = 2_000,
                    poolId = freizeit,
                    categoryId = catB,
                    note = "Stromanteil",
                    tax = new
                    {
                        relevant = true,
                        taxCategoryId = tax19,
                        markedAmount = 1_900,
                        note = "Vorsteuer 11/2026"
                    }
                }
            },
            expectedBaseRevision = (int?)null
        });

        // Reine Skalar-Korrektur: nur die Buchungsnote — splits,
        // totalAmount, date, accountId, allocations bleiben null
        // (der Pfad, den die korrigierte Client-Vorbelegung jetzt
        // für unveränderte Aufteilungen nimmt).
        var correction = await _client.PostAsJsonAsync(
            "/api/v1/bookings/exp-r3-6/corrections", new
        {
            operationId = "corr-r3-6",
            expectedBaseRevision = 1,
            date = (string?)null,
            note = "Miete + Strom (Stand 21.11.)",
            accountId = (string?)null,
            totalAmount = (long?)null,
            splits = (object[]?)null,
            allocations = (object[]?)null
        });
        Assert.Equal(HttpStatusCode.Created, correction.StatusCode);

        // Die Original-Splits bleiben erhalten: Ids, Beträge, Notizen
        // und die Steuerkennzeichnung mit ihrer Notiz.
        var detail = await GetDetailAsync("exp-r3-6");
        Assert.Equal(
            "Miete + Strom (Stand 21.11.)", detail.GetProperty("note").GetString());
        var splits = detail.GetProperty("splits").EnumerateArray().ToList();
        Assert.Equal(2, splits.Count);
        Assert.Equal("sp-r3-6-1", splits[0].GetProperty("id").GetString());
        Assert.Equal("sp-r3-6-2", splits[1].GetProperty("id").GetString());
        Assert.Equal(6_500, splits[0].GetProperty("amount").GetInt64());
        Assert.Equal(2_000, splits[1].GetProperty("amount").GetInt64());
        Assert.Equal("Stromanteil", splits[1].GetProperty("note").GetString());
        var tax = splits[1].GetProperty("tax");
        Assert.True(tax.GetProperty("relevant").GetBoolean());
        Assert.Equal(tax19, tax.GetProperty("taxCategoryId").GetString());
        Assert.Equal(1_900, tax.GetProperty("markedAmount").GetInt64());
        Assert.Equal("Vorsteuer 11/2026", tax.GetProperty("note").GetString());
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

    private async Task<JsonElement> GetBookingsAsync(string type)
    {
        var response = await _client.GetAsync($"/api/v1/bookings?type={type}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static object OpeningBalance(
        string acc, string pool, string id, long total, long distribution) => new
    {
        id,
        date = "2026-11-01",
        total,
        accountId = acc,
        distribution = new object[]
        {
            new { poolId = pool, amount = distribution }
        },
        note = (string?)null
    };

    private static object Expense(string id, string acc, long total, object splits) => new
    {
        id,
        date = "2026-11-10",
        totalAmount = total,
        accountId = acc,
        note = (string?)null,
        splits,
        expectedBaseRevision = (int?)null
    };

    /// <summary>
    /// Split-Liste (anonyme Objekte) aus einer oder zwei Zeilen —
    /// doppelte Pools sind erlaubt (Mehrfach-Split-Fall: der Deckel
    /// gilt pro Pool über die Summe der Poolpositionen). Die zweite
    /// Zeile ist optional (null → Einzel-Split).
    /// </summary>
    private static object Splits(
        long a, string poolA, string catA,
        long? b = null, string? poolB = null, string? catB = null)
    {
        var list = new object[]
        {
            new { id = "sp-1", amount = a, poolId = poolA, categoryId = catA, note = (string?)null, tax = (object?)null }
        };

        if (b is not null)
        {
            list = new object[]
            {
                list[0],
                new { id = "sp-2", amount = b.Value, poolId = poolB, categoryId = catB, note = (string?)null, tax = (object?)null }
            };
        }

        return list;
    }

    private static object Refund(
        string id, string acc, string date, object lines) => new
    {
        id,
        accountId = acc,
        date,
        lines,
        note = (string?)null
    };

    private static object Lines(
        long amount, string pool, long? second = null, string? secondPool = null)
    {
        if (second is null)
        {
            return new object[] { new { poolId = pool, amount } };
        }

        return new object[]
        {
            new { poolId = pool, amount },
            new { poolId = secondPool, amount = second.Value }
        };
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
