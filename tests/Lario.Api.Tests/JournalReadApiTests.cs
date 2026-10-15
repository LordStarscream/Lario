using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lario.Application;
using Lario.Application.Journal;
using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;
using Lario.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lario.Api.Tests;

/// <summary>
/// Journal-Leseendpunkte (Schritt 7, T3): Buchungsliste + Detail,
/// Kontostand (Balances), Ausgleichspositionen (Settlements).
///
/// Die Buchungen werden über die Application-Services aus dem Host-DI
/// direkt in dieselbe SQLite-DB geschrieben (gleicher Mechanismus wie
/// die Write-Endpunkte in T4), die Prüfungen laufen gegen die HTTP-
/// Oberfläche. Masterdaten (Konten/Pools/Kategorien) legt jeder Test
/// über die T2-Endpunkte an.
/// </summary>
public class JournalReadApiTests : IDisposable
{
    private readonly LarioApiFactory _factory;
    private readonly HttpClient _client;

    public JournalReadApiTests()
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

    // ---------- Buchungsliste ----------

    [Fact]
    public async Task Liste_ohne_Buchungen_liefert_leeres_Array()
    {
        var response = await _client.GetAsync("/api/v1/bookings");
        response.EnsureSuccessStatusCode();

        var list = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.True(list.ValueKind == System.Text.Json.JsonValueKind.Array);
        Assert.Equal(0, list.GetArrayLength());
    }

    [Fact]
    public async Task Liste_trägt_Namen_Beträge_und_ist_nach_Datum_absteigend_sortiert()
    {
        var (acc1, acc2, flex, freizeit, catA, catB, tax19) = await SeedMasterDataAsync();

        await SeedOpeningBalanceAsync(
            "init-1", new DateOnly(2026, 11, 1), acc1, 100_000, (flex, 60_000));
        await SeedIncomeAsync(
            "inc-1", new DateOnly(2026, 11, 5), acc1, 50_000, (flex, 50_000));
        await SeedExpenseAsync(
            "exp-1", new DateOnly(2026, 11, 10), acc1, 8_500,
            (splitId: "sp-1", flex, catA, 6_500, tax: null),
            (splitId: "sp-2", freizeit, catB, 2_000,
                tax: new TaxMarking { Relevant = true, TaxCategoryId = tax19, MarkedAmount = new Money(1_900) }));

        var response = await _client.GetAsync("/api/v1/bookings");
        response.EnsureSuccessStatusCode();
        var list = (await response.Content.ReadFromJsonAsync<JsonElement>())!;

        Assert.Equal(3, list.GetArrayLength());

        var entries = list.EnumerateArray().ToList();

        // Sortierung: (Datum, Id) absteigend — die Ausgabe (11-10) zuerst.
        Assert.Equal("exp-1", entries[0].GetProperty("id").GetString());
        Assert.Equal("inc-1", entries[1].GetProperty("id").GetString());
        Assert.Equal("init-1", entries[2].GetProperty("id").GetString());

        // Ausgabe: Typ, Datum, Gesamtbetrag positiv, Konto- und
        // Split-Namen aufgelöst, Steuerkennzeichnung sichtbar.
        Assert.Equal("Expense", entries[0].GetProperty("type").GetString());
        Assert.Equal("2026-11-10", entries[0].GetProperty("date").GetString());
        Assert.Equal(8_500, entries[0].GetProperty("totalAmount").GetInt64());
        Assert.Equal(acc1, entries[0].GetProperty("accountId").GetString());
        Assert.Equal("Konto Eins", entries[0].GetProperty("accountName").GetString());
        var splits = entries[0].GetProperty("splits").EnumerateArray().ToList();
        Assert.Equal(2, splits.Count);
        Assert.Equal(2_000, splits[1].GetProperty("amount").GetInt64());
        Assert.Equal("Pool Freizeit", splits[1].GetProperty("poolName").GetString());
        Assert.Equal("Kategorie B", splits[1].GetProperty("categoryName").GetString());
        Assert.True(splits[1].GetProperty("taxRelevant").GetBoolean());
        Assert.Equal("Steuer 19", splits[1].GetProperty("taxCategoryName").GetString());
        Assert.Contains("Pool Freizeit",
            entries[0].GetProperty("poolNames").EnumerateArray()
                .Select(e => e.GetString()).ToList());

        // Einnahme: positiver Gesamtbetrag, Finanzierungsmonat wird im
        // Detail geprüft (T3: Liste ohne FundingMonth).
        Assert.Equal(50_000, entries[1].GetProperty("totalAmount").GetInt64());
        Assert.Equal("Income", entries[1].GetProperty("type").GetString());

        // Anfangsbestand: positiver Gesamtbetrag.
        Assert.Equal(100_000, entries[2].GetProperty("totalAmount").GetInt64());
        Assert.Equal("OpeningBalance", entries[2].GetProperty("type").GetString());
        Assert.False(entries[0].GetProperty("effectivelyReversed").GetBoolean());
    }

    [Fact]
    public async Task Liste_FilterTyp_case_insensitiv_nur_passende_Buchungen()
    {
        var (acc1, _, flex, freizeit, catA, _, _) = await SeedMasterDataAsync();
        await SeedIncomeAsync("inc-1", new DateOnly(2026, 11, 5), acc1, 5_000, (flex, 5_000));
        await SeedExpenseAsync("exp-1", new DateOnly(2026, 11, 10), acc1, 8_500,
            ("sp-1", flex, catA, 6_500, null),
            ("sp-2", freizeit, catA, 2_000, null));

        var response = await _client.GetAsync("/api/v1/bookings?type=expense");
        response.EnsureSuccessStatusCode();
        var list = (await response.Content.ReadFromJsonAsync<JsonElement>())!;

        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("exp-1", list.EnumerateArray().First().GetProperty("id").GetString());
    }

    [Fact]
    public async Task Liste_unbekannter_Typ_liefert_400_validation_error()
    {
        var response = await _client.GetAsync("/api/v1/bookings?type=Foo");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Lario.Host.Api.ErrorResponse>();
        Assert.Equal("validation_error", body!.Error.Code);
    }

    [Fact]
    public async Task Liste_FilterVonUndBis_inklusive_der_Grenzen()
    {
        var (acc1, _, flex, _, catA, _, _) = await SeedMasterDataAsync();
        await SeedIncomeAsync("inc-1", new DateOnly(2026, 11, 5), acc1, 5_000, (flex, 5_000));
        await SeedExpenseAsync("exp-1", new DateOnly(2026, 11, 10), acc1, 8_500,
            ("sp-1", flex, catA, 8_500, null));
        await SeedExpenseAsync("exp-2", new DateOnly(2026, 12, 1), acc1, 1_000,
            ("sp-1", flex, catA, 1_000, null));

        var response = await _client.GetAsync(
            "/api/v1/bookings?from=2026-11-05&to=2026-11-10");
        response.EnsureSuccessStatusCode();
        var list = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        var ids = list.EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()).ToList();

        // Beide Grenzen sind inklusive; die Dezember-Ausgabe fällt raus.
        Assert.Equal(2, ids.Count);
        Assert.Contains("inc-1", ids);
        Assert.Contains("exp-1", ids);
        Assert.DoesNotContain("exp-2", ids);
    }

    [Fact]
    public async Task Liste_Limit_unter_1_oder_über_1000_liefert_400_limit_1_liefert_neueste()
    {
        var (acc1, _, flex, _, catA, _, _) = await SeedMasterDataAsync();
        await SeedIncomeAsync("inc-1", new DateOnly(2026, 11, 5), acc1, 5_000, (flex, 5_000));
        await SeedExpenseAsync("exp-1", new DateOnly(2026, 11, 10), acc1, 8_500,
            ("sp-1", flex, catA, 8_500, null));

        var zuKlein = await _client.GetAsync("/api/v1/bookings?limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, zuKlein.StatusCode);
        var body1 = await zuKlein.Content.ReadFromJsonAsync<Lario.Host.Api.ErrorResponse>();
        Assert.Equal("validation_error", body1!.Error.Code);

        var zuGroß = await _client.GetAsync("/api/v1/bookings?limit=1001");
        Assert.Equal(HttpStatusCode.BadRequest, zuGroß.StatusCode);

        var limit1 = await _client.GetAsync("/api/v1/bookings?limit=1");
        limit1.EnsureSuccessStatusCode();
        var list = (await limit1.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("exp-1", list.EnumerateArray().First().GetProperty("id").GetString());
    }

    // ---------- Buchungsdetail ----------

    [Fact]
    public async Task Detail_unbekannte_Buchungsliefert_400_invalid_reference()
    {
        var response = await _client.GetAsync("/api/v1/bookings/bk-unknown");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Lario.Host.Api.ErrorResponse>();
        Assert.Equal("invalid_reference", body!.Error.Code);
    }

    [Fact]
    public async Task Detail_Ausgabe_trägt_Splits_Steuer_Konten_und_Poolposten()
    {
        var (acc1, _, flex, freizeit, catA, catB, tax19) = await SeedMasterDataAsync();
        await SeedExpenseAsync("exp-1", new DateOnly(2026, 11, 10), acc1, 8_500,
            ("sp-1", flex, catA, 6_500, null),
            ("sp-2", freizeit, catB, 2_000,
                new TaxMarking { Relevant = true, TaxCategoryId = tax19, MarkedAmount = new Money(1_900) }));

        var response = await _client.GetAsync("/api/v1/bookings/exp-1");
        response.EnsureSuccessStatusCode();
        var detail = (await response.Content.ReadFromJsonAsync<JsonElement>())!;

        Assert.Equal("exp-1", detail.GetProperty("id").GetString());
        Assert.Equal("Expense", detail.GetProperty("type").GetString());
        Assert.Equal("2026-11-10", detail.GetProperty("date").GetString());
        Assert.Equal(1, detail.GetProperty("baseRevision").GetInt32());
        Assert.False(detail.GetProperty("effectivelyReversed").GetBoolean());
        Assert.Empty(detail.GetProperty("reversalBookingIds").EnumerateArray());
        Assert.Equal(8_500, detail.GetProperty("totalAmount").GetInt64());

        // Konto-Posten: das Konto zahlt 85,00 € (negiert).
        var postings = detail.GetProperty("accountPostings").EnumerateArray().ToList();
        Assert.Single(postings);
        Assert.Equal(acc1, postings[0].GetProperty("accountId").GetString());
        Assert.Equal("Konto Eins", postings[0].GetProperty("accountName").GetString());
        Assert.Equal(-8_500, postings[0].GetProperty("amount").GetInt64());

        // Pool-Posten: beide Pools werden verbraucht (negiert).
        var poolPostings = detail.GetProperty("poolPostings").EnumerateArray().ToList();
        Assert.Equal(2, poolPostings.Count);
        Assert.Equal(-6_500, poolPostings[0].GetProperty("amount").GetInt64());
        Assert.Equal(flex, poolPostings[0].GetProperty("poolId").GetString());
        Assert.Equal(-2_000, poolPostings[1].GetProperty("amount").GetInt64());
        Assert.Equal(freizeit, poolPostings[1].GetProperty("poolId").GetString());

        // Splits: Beträge, Namen und die Steuerkennzeichnung mit Name.
        var splits = detail.GetProperty("splits").EnumerateArray().ToList();
        Assert.Equal(2, splits.Count);
        Assert.Equal("sp-2", splits[1].GetProperty("id").GetString());
        Assert.Equal(2_000, splits[1].GetProperty("amount").GetInt64());
        Assert.Equal("Pool Freizeit", splits[1].GetProperty("poolName").GetString());
        Assert.Equal("Kategorie B", splits[1].GetProperty("categoryName").GetString());
        var tax = splits[1].GetProperty("tax");
        Assert.Equal(System.Text.Json.JsonValueKind.Object, tax.ValueKind);
        Assert.True(tax.GetProperty("relevant").GetBoolean());
        Assert.Equal(tax19, tax.GetProperty("taxCategoryId").GetString());
        Assert.Equal("Steuer 19", tax.GetProperty("taxCategoryName").GetString());
        Assert.Equal(1_900, tax.GetProperty("markedAmount").GetInt64());

        // Der unmarkierte Split trägt keine Kennzeichnung.
        Assert.Equal(System.Text.Json.JsonValueKind.Null,
            splits[0].GetProperty("tax").ValueKind);
    }

    [Fact]
    public async Task Detail_Einnahme_trägt_Finanzierungsmonat()
    {
        var (acc1, _, flex, _, _, _, _) = await SeedMasterDataAsync();
        await SeedIncomeAsync("inc-1", new DateOnly(2026, 11, 30), acc1, 5_000, (flex, 5_000));

        var response = await _client.GetAsync("/api/v1/bookings/inc-1");
        response.EnsureSuccessStatusCode();
        var detail = (await response.Content.ReadFromJsonAsync<JsonElement>())!;

        // Default-Regel: Einnahme Ende November finanziert den Folgemonat.
        Assert.Equal("2026-12", detail.GetProperty("fundingMonth").GetString());
        Assert.Equal(5_000, detail.GetProperty("totalAmount").GetInt64());
        Assert.True(detail.GetProperty("accountPostings").EnumerateArray().First()
            .GetProperty("amount").GetInt64() > 0);
    }

    // ---------- Storno: effectivelyReversed ----------

    [Fact]
    public async Task Detail_nach_Storno_Original_wirksam_storniert_Storno_verknüpft_zurück()
    {
        var (acc1, _, flex, freizeit, catA, _, _) = await SeedMasterDataAsync();
        var before = await GetBalancesAsync();
        await SeedExpenseAsync("exp-1", new DateOnly(2026, 11, 10), acc1, 8_500,
            ("sp-1", flex, catA, 6_500, null),
            ("sp-2", freizeit, catA, 2_000, null));

        await using var scope = _factory.Services.CreateAsyncScope();
        var reversals = scope.ServiceProvider.GetRequiredService<ReversalService>();
        var result = await reversals.ReverseAsync(new ReversalSpec(
            "rev-1", "exp-1", ExpectedBaseRevision: 1,
            new DateOnly(2026, 11, 11), "Storno Test"));
        Assert.True(result.Created);

        // Original: wirksam storniert, Storno-Id verknüpft.
        var original = await (await _client.GetAsync("/api/v1/bookings/exp-1"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(original!.GetProperty("effectivelyReversed").GetBoolean());
        var reversalIds = original.GetProperty("reversalBookingIds")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["rev-1"], reversalIds);

        // Storno: verknüpft zurück zum Original.
        var reversal = await (await _client.GetAsync("/api/v1/bookings/rev-1"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Reversal", reversal!.GetProperty("type").GetString());
        Assert.Equal("exp-1", reversal.GetProperty("reversesBookingId").GetString());
        Assert.False(reversal.GetProperty("effectivelyReversed").GetBoolean());

        // Die Poolbestände sind wiederhergestellt (Storno negiert die
        // Posten des Originals).
        var after = await GetBalancesAsync();
        foreach (var pool in before.Pools)
        {
            Assert.Equal(pool.Balance, after.Pools.Single(p => p.PoolId == pool.PoolId).Balance);
        }
    }

    // ---------- Kontostand (Balances) ----------

    [Fact]
    public async Task Balances_Konten_Pools_Gap_und_unzugeordneter_Rest()
    {
        var (acc1, acc2, flex, freizeit, catA, _, _) = await SeedMasterDataAsync();
        await SeedOpeningBalanceAsync(
            "init-1", new DateOnly(2026, 11, 1), acc1, 100_000, (flex, 60_000));
        await SeedOpeningBalanceAsync(
            "init-2", new DateOnly(2026, 11, 1), acc2, 30_000, (freizeit, 30_000));
        await SeedExpenseAsync("exp-1", new DateOnly(2026, 11, 10), acc1, 8_500,
            ("sp-1", flex, catA, 6_500, null),
            ("sp-2", freizeit, catA, 2_000, null));

        var balances = await GetBalancesAsync();

        // Kontostände: 1000 € − 85 € bzw. 300 €.
        Assert.Equal(91_500, balances.Accounts.Single(a => a.AccountId == acc1).Balance.Cents);
        Assert.Equal(30_000, balances.Accounts.Single(a => a.AccountId == acc2).Balance.Cents);

        // Poolbestände: 600 € − 65 € bzw. 300 € − 20 €.
        Assert.Equal(53_500, balances.Pools.Single(p => p.PoolId == flex).Balance.Cents);
        Assert.Equal(28_000, balances.Pools.Single(p => p.PoolId == freizeit).Balance.Cents);

        // Summen: Konten 1 215 €, Pools 815 €, Lücke 400 € =
        // nicht zugeordneter Anfangsbestand auf Konto Eins.
        Assert.Equal(121_500, balances.TotalAccountBalance.Cents);
        Assert.Equal(81_500, balances.TotalPoolBalance.Cents);
        Assert.Equal(40_000, balances.AccountPoolGap.Cents);
        var unallocated = balances.UnallocatedOpeningBalances.Single();
        Assert.Equal("init-1", unallocated.OpeningBalanceId);
        Assert.Equal(40_000, unallocated.Unallocated.Cents);

        // Die HTTP-Antwort trägt dieselben Werte (Cent als long).
        var response = await _client.GetAsync("/api/v1/balances");
        response.EnsureSuccessStatusCode();
        var dto = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(121_500, dto.GetProperty("totalAccountBalance").GetInt64());
        Assert.Equal(81_500, dto.GetProperty("totalPoolBalance").GetInt64());
        Assert.Equal(40_000, dto.GetProperty("accountPoolGap").GetInt64());
        var unallocatedDto = dto.GetProperty("unallocatedOpeningBalances").EnumerateArray().Single();
        Assert.Equal(40_000, unallocatedDto.GetProperty("unallocated").GetInt64());
        Assert.Equal("Konto Eins", unallocatedDto.GetProperty("accountName").GetString());
    }

    [Fact]
    public async Task Balances_TotalIncome_zählt_nur_Einnahmen()
    {
        var (acc1, _, flex, _, _, _, _) = await SeedMasterDataAsync();
        await SeedIncomeAsync("inc-1", new DateOnly(2026, 11, 5), acc1, 50_000, (flex, 50_000));
        await SeedOpeningBalanceAsync(
            "init-1", new DateOnly(2026, 11, 1), acc1, 100_000, (flex, 100_000));

        var balances = await GetBalancesAsync();
        // Anfangsbestand ist keine Einnahme (Rechenvertrag §3.7).
        Assert.Equal(50_000, balances.TotalIncome.Cents);
    }

    // ---------- Ausgleichspositionen (Settlements) ----------

    [Fact]
    public async Task Settlements_Fremdkonto_Ausgabe_liefert_offene_Position()
    {
        var (acc1, acc2, flex, freizeit, catA, _, _) = await SeedMasterDataAsync();
        await SeedOpeningBalanceAsync(
            "init-1", new DateOnly(2026, 11, 1), acc1, 100_000, (flex, 100_000));
        await SeedOpeningBalanceAsync(
            "init-2", new DateOnly(2026, 11, 1), acc2, 30_000, (freizeit, 30_000));

        // Ausgabe von Konto Eins finanziert auch Pool Freizeit, der auf
        // Konto Zwei liegt — die negative Zelle (Konto Eins, Freizeit)
        // begründet die offene Ausgleichsposition.
        await SeedExpenseAsync("exp-1", new DateOnly(2026, 11, 10), acc1, 8_500,
            ("sp-1", flex, catA, 6_500, null),
            ("sp-2", freizeit, catA, 2_000, null));

        var response = await _client.GetAsync("/api/v1/settlements");
        response.EnsureSuccessStatusCode();
        var list = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(1, list.GetArrayLength());

        var position = list.EnumerateArray().Single();
        Assert.Equal("open", position.GetProperty("status").GetString());
        Assert.Equal(2_000, position.GetProperty("amount").GetInt64());
        Assert.Equal(2_000, position.GetProperty("restAmount").GetInt64());
        Assert.Equal(acc1, position.GetProperty("sourceAccountId").GetString());
        Assert.Equal("Konto Eins", position.GetProperty("sourceAccountName").GetString());
        Assert.Equal(freizeit, position.GetProperty("targetPoolId").GetString());
        Assert.Equal("exp-1", position.GetProperty("createdByBookingId").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null,
            position.GetProperty("settledByBookingId").ValueKind);
    }

    [Fact]
    public async Task Settlements_Ausgleichs_Transfer_schließt_Position()
    {
        var (acc1, acc2, flex, freizeit, catA, _, _) = await SeedMasterDataAsync();
        await SeedOpeningBalanceAsync(
            "init-1", new DateOnly(2026, 11, 1), acc1, 100_000, (flex, 100_000));
        await SeedOpeningBalanceAsync(
            "init-2", new DateOnly(2026, 11, 1), acc2, 30_000, (freizeit, 30_000));
        await SeedExpenseAsync("exp-1", new DateOnly(2026, 11, 10), acc1, 8_500,
            ("sp-1", flex, catA, 6_500, null),
            ("sp-2", freizeit, catA, 2_000, null));

        var list = (await (await _client.GetAsync("/api/v1/settlements"))
            .Content.ReadFromJsonAsync<JsonElement>())!;
        var positionId = list.EnumerateArray().Single().GetProperty("id").GetString()!;

        // Rückfluss der Vorfinanzierung: 20 € von Konto Zwei (dem Konto
        // des Positionspools) auf Konto Eins (Quellkonto der Position),
        // verknüpft mit der Position.
        await using var scope = _factory.Services.CreateAsyncScope();
        var transfers = scope.ServiceProvider.GetRequiredService<TransferService>();
        var result = await transfers.CreateAsync(new TransferSpec
        {
            Id = "tr-1",
            Date = new DateOnly(2026, 11, 11),
            FromAccountId = acc2,
            ToAccountId = acc1,
            Amount = new Money(2_000),
            RelatedSettlementId = positionId,
            Note = "Ausgleich"
        });
        Assert.True(result.Created);

        var after = (await (await _client.GetAsync("/api/v1/settlements"))
            .Content.ReadFromJsonAsync<JsonElement>())!;
        var settled = after.EnumerateArray().Single();
        Assert.Equal("settled", settled.GetProperty("status").GetString());
        Assert.Equal(0, settled.GetProperty("restAmount").GetInt64());
        Assert.Equal("tr-1", settled.GetProperty("settledByBookingId").GetString());

        // Die offene-Liste ist leer.
        var open = (await (await _client.GetAsync("/api/v1/settlements/open"))
            .Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(0, open.GetArrayLength());
    }

    // ---------- Seeder (Application-Services über Host-DI) ----------

    private async Task<(string acc1, string acc2, string flex, string freizeit,
        string catA, string catB, string tax19)> SeedMasterDataAsync()
    {
        var acc1 = await CreateAccountAsync("Konto Eins", "bank");
        var acc2 = await CreateAccountAsync("Konto Zwei", "savings");
        var flex = await CreatePoolAsync("Pool Flex");
        var freizeit = await CreatePoolAsync("Pool Freizeit");
        var catA = await CreateCategoryAsync("Kategorie A");
        var catB = await CreateCategoryAsync("Kategorie B");
        var tax19 = await CreateTaxCategoryAsync("Steuer 19");
        return (acc1, acc2, flex, freizeit, catA, catB, tax19);
    }

    private async Task<string> CreateAccountAsync(string name, string type)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/accounts",
            new { name, type });
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

    private async Task<string> CreateCategoryAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/categories", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("id").GetString()!;
    }

    private async Task<string> CreateTaxCategoryAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/tax-categories", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("id").GetString()!;
    }

    private async Task SeedOpeningBalanceAsync(
        string id, DateOnly date, string accountId, long totalCents,
        params (string pool, long amount)[] distribution)
    {
        await RunAsync(sp => sp.GetRequiredService<OpeningBalanceService>().CreateAsync(new OpeningBalanceSpec(
            id, date, accountId, new Money(totalCents),
            distribution
                .Select(d => new PoolAllocation(d.pool, new Money(d.amount)))
                .ToList())));
    }

    private async Task SeedIncomeAsync(
        string id, DateOnly date, string accountId, long amountCents,
        params (string pool, long amount)[] allocation)
    {
        await RunAsync(sp => sp.GetRequiredService<IncomeService>().CreateAsync(new IncomeSpec
        {
            Id = id,
            Date = date,
            AccountId = accountId,
            Amount = new Money(amountCents),
            Allocations = allocation
                .Select(a => new IncomeAllocationSpec(a.pool, new Money(a.amount)))
                .ToList()
        }));
    }

    private async Task SeedExpenseAsync(
        string id, DateOnly date, string accountId, long totalCents,
        params (string splitId, string pool, string category, long amount, TaxMarking? tax)[] splits)
    {
        await RunAsync(sp => sp.GetRequiredService<ExpenseService>().CreateAsync(new ExpenseSpec
        {
            Id = id,
            Date = date,
            AccountId = accountId,
            TotalAmount = new Money(totalCents),
            Splits = splits.Select(s => new ExpenseSplitSpec
            {
                Id = s.splitId,
                PoolId = s.pool,
                CategoryId = s.category,
                Amount = new Money(s.amount),
                Tax = s.tax
            }).ToList()
        }));
    }

    private Task<BalanceQueryService.Balances> GetBalancesAsync() =>
        RunAsync(sp => sp.GetRequiredService<BalanceQueryService>().GetBalancesAsync());

    /// <summary>
    /// Führt einen Application-Service-Aufruf in einem Host-DI-Scope aus
    /// (dieselbe Datenbank wie der Testserver).
    /// </summary>
    private async Task<T> RunAsync<T>(
        Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
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
}
