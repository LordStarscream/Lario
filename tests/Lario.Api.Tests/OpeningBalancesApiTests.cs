using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lario.Application;
using Lario.Domain;
using Lario.Domain.Journal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lario.Api.Tests;

/// <summary>
/// Anfangsbestände-Liste (Zwischenstep 7.5, A3/A4):
/// <c>GET /api/v1/opening-balances</c> mit Datum, Konto, Gesamtbetrag,
/// Verteilung und Notiz; Korrekturweg über
/// <c>POST /api/v1/bookings/{id}/corrections</c> (Einplatz-Korrektur,
/// Basisrevision++); Einmaligkeitsregel (zweiter Anfangsbestand pro
/// Konto → 409, Konto-Wechsel auf belegtes Konto → 409).
/// </summary>
public class OpeningBalancesApiTests : IDisposable
{
    private readonly LarioApiFactory _factory;
    private readonly HttpClient _client;

    public OpeningBalancesApiTests()
    {
        _factory = new LarioApiFactory();
        var login = LoginAsync(_factory.CreateClient()).GetAwaiter().GetResult();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.Token);
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Liste_ohne_Anfangsbestaende_liefert_leeres_Array()
    {
        var response = await _client.GetAsync("/api/v1/opening-balances");
        response.EnsureSuccessStatusCode();

        var list = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
        Assert.Equal(0, list.GetArrayLength());
    }

    [Fact]
    public async Task Liste_tragt_Datum_Konto_Betrag_Verteilung_Notiz_und_Revision()
    {
        var (acc1, acc2, flex, freizeit) = await SeedAsync();

        await CreateOpeningBalanceAsync(
            "init-1", DateOnly.Parse("2026-02-01"), acc1, 100_000,
            note: "Stand zum Umzug",
            distribution: new[] { (flex, 60_000L) });
        await CreateOpeningBalanceAsync(
            "init-2", DateOnly.Parse("2026-01-01"), acc2, 30_000,
            note: null,
            distribution: Array.Empty<(string, long)>());

        var response = await _client.GetAsync("/api/v1/opening-balances");
        response.EnsureSuccessStatusCode();
        var list = (await response.Content.ReadFromJsonAsync<JsonElement>())!;

        // Sortierung aufsteigend (Stichtag, Id): init-2 (01-01) zuerst.
        Assert.Equal(2, list.GetArrayLength());
        var entries = list.EnumerateArray().ToList();
        Assert.Equal("init-2", entries[0].GetProperty("id").GetString());
        Assert.Equal("init-1", entries[1].GetProperty("id").GetString());

        // init-2: unverteilt (keine Verteilung), Notiz null.
        Assert.Equal("2026-01-01", entries[0].GetProperty("date").GetString());
        Assert.Equal(acc2, entries[0].GetProperty("accountId").GetString());
        Assert.Equal("Konto Zwei", entries[0].GetProperty("accountName").GetString());
        Assert.Equal(30_000, entries[0].GetProperty("total").GetInt64());
        Assert.Equal(0, entries[0].GetProperty("distribution").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, entries[0].GetProperty("note").ValueKind);
        Assert.Equal(1, entries[0].GetProperty("baseRevision").GetInt32());

        // init-1: Verteilung mit Pool-Namen, Notiz sichtbar.
        Assert.Equal("2026-02-01", entries[1].GetProperty("date").GetString());
        Assert.Equal(acc1, entries[1].GetProperty("accountId").GetString());
        Assert.Equal("Konto Eins", entries[1].GetProperty("accountName").GetString());
        Assert.Equal(100_000, entries[1].GetProperty("total").GetInt64());
        var distribution = entries[1].GetProperty("distribution").EnumerateArray().ToList();
        Assert.Single(distribution);
        Assert.Equal(flex, distribution[0].GetProperty("poolId").GetString());
        Assert.Equal("Pool Flex", distribution[0].GetProperty("poolName").GetString());
        Assert.Equal(60_000, distribution[0].GetProperty("amount").GetInt64());
        Assert.Equal("Stand zum Umzug", entries[1].GetProperty("note").GetString());
    }

    [Fact]
    public async Task Korrektur_wirkt_sich_auf_die_Liste_aus_Revision_steigt()
    {
        var (acc1, _, flex, freizeit) = await SeedAsync();
        await CreateOpeningBalanceAsync(
            "init-1", DateOnly.Parse("2026-01-01"), acc1, 100_000,
            note: "ursprünglich",
            distribution: new[] { (flex, 100_000L) });

        // Korrektur: Betrag 100 → 120 €, Verteilung auf zwei Pools,
        // Notiz geändert; Datum bleibt.
        var response = await _client.PostAsJsonAsync(
            "/api/v1/bookings/init-1/corrections", new
        {
            operationId = "corr-ob-1",
            expectedBaseRevision = 1,
            date = (string?)null,
            note = "korrigiert",
            accountId = (string?)null,
            totalAmount = 120_000L,
            splits = (object[]?)null,
            allocations = new object[]
            {
                new { poolId = flex, amount = 70_000L },
                new { poolId = freizeit, amount = 50_000L }
            }
        });
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.False(created.GetProperty("idempotent").GetBoolean());
        Assert.Equal(2, created.GetProperty("booking").GetProperty("baseRevision").GetInt32());

        var listResponse = await _client.GetAsync("/api/v1/opening-balances");
        listResponse.EnsureSuccessStatusCode();
        var entry = (await listResponse.Content.ReadFromJsonAsync<JsonElement>())!
            .EnumerateArray().Single();

        Assert.Equal("init-1", entry.GetProperty("id").GetString());
        Assert.Equal("2026-01-01", entry.GetProperty("date").GetString());
        Assert.Equal(120_000, entry.GetProperty("total").GetInt64());
        Assert.Equal("korrigiert", entry.GetProperty("note").GetString());
        Assert.Equal(2, entry.GetProperty("baseRevision").GetInt32());
        // Verteilung deterministisch nach PoolId sortiert (Repository).
        var distribution = entry.GetProperty("distribution").EnumerateArray().ToList();
        Assert.Equal(2, distribution.Count);
        var lineFlex = distribution.Single(d =>
            d.GetProperty("poolId").GetString() == flex);
        var lineFreizeit = distribution.Single(d =>
            d.GetProperty("poolId").GetString() == freizeit);
        Assert.Equal(70_000, lineFlex.GetProperty("amount").GetInt64());
        Assert.Equal(50_000, lineFreizeit.GetProperty("amount").GetInt64());
    }

    [Fact]
    public async Task Korrektur_mit_Kontowechsel_bewegt_die_Einmaligkeitszuordnung()
    {
        var (acc1, acc2, flex, _) = await SeedAsync();
        await CreateOpeningBalanceAsync(
            "init-1", DateOnly.Parse("2026-01-01"), acc1, 100_000,
            note: null,
            distribution: new[] { (flex, 100_000L) });

        // Konto-Wechsel auf das (noch) freie Konto Zwe: erlaubt; danach
        // trägt acc2 den Anfangsbestand und acc1 keinen mehr.
        var response = await _client.PostAsJsonAsync(
            "/api/v1/bookings/init-1/corrections", new
        {
            operationId = "corr-ob-move",
            expectedBaseRevision = 1,
            date = (string?)null,
            note = (string?)null,
            accountId = acc2,
            totalAmount = (long?)null,
            splits = (object[]?)null,
            allocations = (object[]?)null
        });
        response.EnsureSuccessStatusCode();

        var listResponse = await _client.GetAsync("/api/v1/opening-balances");
        listResponse.EnsureSuccessStatusCode();
        var entry = (await listResponse.Content.ReadFromJsonAsync<JsonElement>())!
            .EnumerateArray().Single();
        Assert.Equal(acc2, entry.GetProperty("accountId").GetString());
        Assert.Equal("Konto Zwei", entry.GetProperty("accountName").GetString());

        // Und: für acc1 ist jetzt ein neuer Anfangsbestand erlaubt
        // (Einmaligkeitsregel folgt dem wirksamen Konto).
        var createResponse = await _client.PostAsJsonAsync(
            "/api/v1/opening-balances", new
        {
            id = "init-3",
            date = "2026-01-01",
            total = 5_000L,
            accountId = acc1,
            distribution = new object[0],
            note = (string?)null
        });
        createResponse.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Korrektur_auf_bereits_belegtes_Konto_wird_abgelehnt()
    {
        var (acc1, acc2, flex, _) = await SeedAsync();
        await CreateOpeningBalanceAsync(
            "init-1", DateOnly.Parse("2026-01-01"), acc1, 100_000,
            note: null,
            distribution: new[] { (flex, 100_000L) });
        await CreateOpeningBalanceAsync(
            "init-2", DateOnly.Parse("2026-01-01"), acc2, 30_000,
            note: null,
            distribution: Array.Empty<(string, long)>());

        // init-1 auf acc2 umstellen: acc2 trägt bereits init-2.
        var response = await _client.PostAsJsonAsync(
            "/api/v1/bookings/init-1/corrections", new
        {
            operationId = "corr-ob-conflict",
            expectedBaseRevision = 1,
            date = (string?)null,
            note = (string?)null,
            accountId = acc2,
            totalAmount = (long?)null,
            splits = (object[]?)null,
            allocations = (object[]?)null
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Lario.Host.Api.ErrorResponse>();
        Assert.Equal("domain_conflict", body!.Error.Code);
        Assert.Contains("init-2", body.Error.Message);

        // Liste unverändert: init-1 bleibt auf acc1, Revision 1.
        var listResponse = await _client.GetAsync("/api/v1/opening-balances");
        listResponse.EnsureSuccessStatusCode();
        var entries = (await listResponse.Content.ReadFromJsonAsync<JsonElement>())!
            .EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        var entry1 = entries.Single(e => e.GetProperty("id").GetString() == "init-1");
        Assert.Equal(acc1, entry1.GetProperty("accountId").GetString());
        Assert.Equal(1, entry1.GetProperty("baseRevision").GetInt32());
    }

    [Fact]
    public async Task Zweiter_Anfangsbestand_desselben_Kontos_wird_abgelehnt()
    {
        var (acc1, _, flex, _) = await SeedAsync();
        await CreateOpeningBalanceAsync(
            "init-1", DateOnly.Parse("2026-01-01"), acc1, 100_000,
            note: null,
            distribution: new[] { (flex, 100_000L) });

        var response = await _client.PostAsJsonAsync(
            "/api/v1/opening-balances", new
        {
            id = "init-2",
            date = "2026-01-01",
            total = 40_000L,
            accountId = acc1,
            distribution = new object[] { new { poolId = flex, amount = 40_000L } },
            note = (string?)null
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Lario.Host.Api.ErrorResponse>();
        Assert.Equal("domain_conflict", body!.Error.Code);
        Assert.Contains("bereits einen Anfangsbestand", body.Error.Message);

        // Liste enthält weiterhin genau einen Eintrag.
        var listResponse = await _client.GetAsync("/api/v1/opening-balances");
        listResponse.EnsureSuccessStatusCode();
        var list = (await listResponse.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(1, list.GetArrayLength());
    }

    [Fact]
    public async Task Ohne_Token_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync("/api/v1/opening-balances");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- Hilfsmittel ----------

    private async Task<(string acc1, string acc2, string flex, string freizeit)> SeedAsync()
    {
        var acc1 = await CreateAccountAsync("Konto Eins");
        var acc2 = await CreateAccountAsync("Konto Zwei");
        var flex = await CreatePoolAsync("Pool Flex");
        var freizeit = await CreatePoolAsync("Pool Freizeit");
        return (acc1, acc2, flex, freizeit);
    }

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

    private async Task<string> CreateAccountAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/accounts",
            new { name, type = "bank" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("id").GetString()!;
    }

    private async Task<string> CreatePoolAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/pools", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("id").GetString()!;
    }

    private async Task CreateOpeningBalanceAsync(
        string id, DateOnly date, string accountId, long totalCents,
        string? note, (string pool, long amount)[] distribution)
    {
        await RunAsync(sp => sp.GetRequiredService<OpeningBalanceService>().CreateAsync(new OpeningBalanceSpec(
            id, date, accountId, new Money(totalCents),
            distribution
                .Select(d => new PoolAllocation(d.pool, new Money(d.amount)))
                .ToList(),
            note)));
    }

    private async Task<T> RunAsync<T>(
        Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }
}
