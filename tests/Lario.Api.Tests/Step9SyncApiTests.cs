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
/// Sync-Protokoll (Schritt 9, Batch A1): Pairing/Widerruf (A5),
/// Bootstrap-Snapshot (4.1), Push mit atomarer Fachbuchung +
/// Idempotenz (4.2/A2/A3) und Changelog-Pull (4.3).
///
/// Referenzfall A1 (20-fache Wiederholung): 20 verschiedene
/// Operationen, eine davon zweimal gesendet — es dürfen genau 20
/// Buchungen mit korrekten Salden entstehen (kein Verlust, keine
/// Doppelbuchung).
/// </summary>
public class Step9SyncApiTests : IDisposable
{
    private readonly LarioApiFactory _factory;
    private readonly HttpClient _sessionClient;

    public Step9SyncApiTests()
    {
        _factory = new LarioApiFactory();

        var login = LoginAsync(_factory.CreateClient()).GetAwaiter().GetResult();
        _sessionClient = _factory.CreateClient();
        _sessionClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.Token);
    }

    public void Dispose() => _factory.Dispose();

    // ---------- Pairing und Widerruf (A5) ----------

    [Fact]
    public async Task Pair_liefert_EinmalToken_und_GeraeteEintrag_ohne_Token()
    {
        var response = await _sessionClient.PostAsJsonAsync("/api/v1/sync/pair",
            new { name = "Testgerät" });
        response.EnsureSuccessStatusCode();
        var dto = (await response.Content.ReadFromJsonAsync<JsonElement>())!;

        var id = dto.GetProperty("id").GetString()!;
        var token = dto.GetProperty("token").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(id));
        // 32 Zufallsbytes als Hex = 64 Zeichen (wie Sitzungstokens).
        Assert.Equal(64, token.Length);
        Assert.Matches("^[0-9A-F]+$", token);

        var list = await (await _sessionClient.GetAsync("/api/v1/sync/devices"))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal(1, list.GetArrayLength());
        var entry = list[0];
        Assert.Equal(id, entry.GetProperty("id").GetString());
        Assert.Equal("Testgerät", entry.GetProperty("name").GetString());
        // Das Klartext-Token erscheint nie wieder (nur der Hash persistiert).
        Assert.Equal(string.Empty, entry.GetProperty("token").GetString());
        Assert.True(entry.GetProperty("revokedAtUtc").ValueKind == JsonValueKind.Null);
        Assert.True(entry.GetProperty("lastUsedAtUtc").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Pair_mit_leerem_Namen_fällt_auf_Default_zur_und_ablehnt_zu_lange_Namen()
    {
        // Leerer Name ist erlaubt und wird auf den Default-Namen
        // gemappt (kein 400-Fehlerpfad).
        var leere = await (await _sessionClient.PostAsJsonAsync("/api/v1/sync/pair",
            new { name = "   " })).Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal("Android-Gerät", leere.GetProperty("name").GetString());

        var zuLange = await _sessionClient.PostAsJsonAsync("/api/v1/sync/pair",
            new { name = new string('x', 257) });
        Assert.Equal(HttpStatusCode.BadRequest, zuLange.StatusCode);
    }

    [Fact]
    public async Task SyncEndpunkte_verweigern_SessionToken_und_ohne_Token()
    {
        // Session-Tokens an den Sync-Endpunkten (A5: eindeutige 401er).
        foreach (var url in new[]
                 {
                     "/api/v1/sync/bootstrap",
                     "/api/v1/sync/changes?cursor=0",
                 })
        {
            var mitSession = await _sessionClient.GetAsync(url);
            Assert.Equal(HttpStatusCode.Unauthorized, mitSession.StatusCode);
        }

        var pushMitSession = await _sessionClient.PostAsJsonAsync(
            "/api/v1/sync/push", ValidPushPayload("op-x", "acc-1", "flex", "cat-a"));
        Assert.Equal(HttpStatusCode.Unauthorized, pushMitSession.StatusCode);

        // Ohne Token ebenfalls 401 (keine Anonym-Zugriffe).
        var ohneToken = _factory.CreateClient();
        var bootstrapOhneToken = await ohneToken.PostAsJsonAsync("/api/v1/sync/bootstrap", new {});
        Assert.Equal(HttpStatusCode.Unauthorized, bootstrapOhneToken.StatusCode);
    }

    [Fact]
    public async Task GeraetToken_an_SessionEndpunkt_wird_verworfen()
    {
        var (deviceId, deviceToken) = await PairAsync();
        _ = deviceId;

        var deviceClient = CreateDeviceClient(deviceToken);
        var accounts = await deviceClient.GetAsync("/api/v1/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, accounts.StatusCode);
    }

    [Fact]
    public async Task Widerruf_sperrt_Gerätetoken_und_ist_idempotent()
    {
        var (deviceId, deviceToken) = await PairAsync();
        var deviceClient = CreateDeviceClient(deviceToken);

        var vorher = await deviceClient.PostAsJsonAsync("/api/v1/sync/bootstrap", new {});
        Assert.Equal(HttpStatusCode.OK, vorher.StatusCode);

        var revoked = await _sessionClient.DeleteAsync($"/api/v1/sync/devices/{deviceId}");
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        // Token ist danach endgültig ungültig (Bootstrap, Push, Changelog).
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await deviceClient.PostAsJsonAsync("/api/v1/sync/bootstrap", new {})).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await deviceClient.PostAsJsonAsync(
                "/api/v1/sync/push", ValidPushPayload("op-w", "acc-1", "flex", "cat-a"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await deviceClient.GetAsync("/api/v1/sync/changes?cursor=0")).StatusCode);

        // Zweiter Widerruf bleibt idempotent (204, kein Fehler).
        var erneut = await _sessionClient.DeleteAsync($"/api/v1/sync/devices/{deviceId}");
        Assert.Equal(HttpStatusCode.NoContent, erneut.StatusCode);

        // Die Zeile bleibt zur Nachvollziehbarkeit, mit Widerrufzeitpunkt.
        var list = await (await _sessionClient.GetAsync("/api/v1/sync/devices"))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        var entry = list[0];
        Assert.Equal(deviceId, entry.GetProperty("id").GetString());
        Assert.True(entry.GetProperty("revokedAtUtc").ValueKind == JsonValueKind.String);

        // Unbekanntes Gerät: 400 invalid_reference (konsistent mit
        // Stammdaten-Endpunkten).
        var unbekannt = await _sessionClient.DeleteAsync("/api/v1/sync/devices/xyz");
        Assert.Equal(HttpStatusCode.BadRequest, unbekannt.StatusCode);
    }

    [Fact]
    public async Task LastUsedAtUtc_wird_nach_SyncZugriff_gesetzt()
    {
        var (deviceId, deviceToken) = await PairAsync();
        var deviceClient = CreateDeviceClient(deviceToken);

        async Task<DateTime?> LastUsedAsync()
        {
            var list = await (await _sessionClient.GetAsync("/api/v1/sync/devices"))
                .Content.ReadFromJsonAsync<JsonElement>()!;
            var value = list[0].GetProperty("lastUsedAtUtc");
            return value.ValueKind == JsonValueKind.Null
                ? null
                : DateTime.Parse(value.GetString()!);
        }

        Assert.Null(await LastUsedAsync());
        await deviceClient.PostAsJsonAsync("/api/v1/sync/bootstrap", new {});
        var afterBootstrap = await LastUsedAsync();
        Assert.NotNull(afterBootstrap);
    }

    // ---------- Bootstrap (4.1) ----------

    [Fact]
    public async Task Bootstrap_liefert_Vollstaendigen_Bestand_mit_Archivierten_und_konsistentem_Cursor()
    {
        var (acc1, acc2, pool, cat, tax) = await SeedMasterDataAsync();
        // Archiviertes Konto gehört weiterhin zum Bestand (4.1).
        var archived = await _sessionClient.PostAsync(
            $"/api/v1/accounts/{acc2}/archive", new StringContent(string.Empty));
        archived.EnsureSuccessStatusCode();

        var (deviceId, deviceToken) = await PairAsync();
        _ = deviceId;
        var deviceClient = CreateDeviceClient(deviceToken);

        var response = await deviceClient.PostAsJsonAsync("/api/v1/sync/bootstrap", new {});
        response.EnsureSuccessStatusCode();
        var snapshot = (await response.Content.ReadFromJsonAsync<JsonElement>())!;

        Assert.Equal(1, snapshot.GetProperty("protocolGeneration").GetInt64());
        Assert.Equal(1, snapshot.GetProperty("dbGeneration").GetInt64());

        var accounts = snapshot.GetProperty("accounts").EnumerateArray()
            .Select(e => (e.GetProperty("id").GetString()!,
                          e.GetProperty("status").GetString()!)).ToList();
        Assert.Contains((acc1, "active"), accounts);
        Assert.Contains((acc2, "archived"), accounts);

        Assert.Contains(snapshot.GetProperty("pools").EnumerateArray(),
            e => e.GetProperty("id").GetString() == pool);
        Assert.Contains(snapshot.GetProperty("categories").EnumerateArray(),
            e => e.GetProperty("id").GetString() == cat);
        Assert.Contains(snapshot.GetProperty("taxCategories").EnumerateArray(),
            e => e.GetProperty("id").GetString() == tax);

        // Konsistenz (D4): der Cursor deckt alle Changelog-Zeilen ab, die
        // der Snapshot bereits enthält — ein Pull ab dem Cursor liefert
        // nichts Altes zurück.
        var cursor = snapshot.GetProperty("cursor").GetInt64();
        var rest = await GetChangesAsync(deviceClient, cursor);
        Assert.Equal(0, rest.GetProperty("changes").GetArrayLength());
        Assert.True(rest.GetProperty("complete").GetBoolean());
        Assert.False(rest.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Bootstrap_mit_leerem_Bestand_liefert_Cursor_0_und_leere_Listen()
    {
        var (_, deviceToken) = await PairAsync();
        var deviceClient = CreateDeviceClient(deviceToken);

        var response = await deviceClient.PostAsJsonAsync("/api/v1/sync/bootstrap", new {});
        response.EnsureSuccessStatusCode();
        var snapshot = (await response.Content.ReadFromJsonAsync<JsonElement>())!;

        Assert.Equal(0, snapshot.GetProperty("cursor").GetInt64());
        Assert.Equal(0, snapshot.GetProperty("accounts").GetArrayLength());
        Assert.Equal(0, snapshot.GetProperty("pools").GetArrayLength());
        Assert.Equal(0, snapshot.GetProperty("categories").GetArrayLength());
        Assert.Equal(0, snapshot.GetProperty("taxCategories").GetArrayLength());
    }

    [Fact]
    public async Task Bootstrap_liefert_dieselben_DTOs_wie_StammdatenEndpunkte()
    {
        await SeedMasterDataAsync();

        var (_, deviceToken) = await PairAsync();
        var deviceClient = CreateDeviceClient(deviceToken);

        var snapshot = (await (await deviceClient
            .PostAsJsonAsync("/api/v1/sync/bootstrap", new {}))
            .Content.ReadFromJsonAsync<JsonElement>())!;

        // Vergleich über kanonisches JSON (schlüsselgeordnet) — der
        // Bootstrap muss exakt denselben Inhalt liefern wie die
        // Stammdaten-Endpunkte.
        static string Canonical(JsonElement element) =>
            JsonSerializer.Serialize(element,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = null
                });

        var accountsViaSync = snapshot.GetProperty("accounts").EnumerateArray()
            .Select(Canonical).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var accountsViaApi = (await (await _sessionClient
            .GetAsync("/api/v1/accounts"))
            .Content.ReadFromJsonAsync<JsonElement>())!
            .EnumerateArray().Select(Canonical)
            .OrderBy(s => s, StringComparer.Ordinal).ToList();

        Assert.Equal(accountsViaApi, accountsViaSync);
    }

    // ---------- Push: akzeptiert, Idempotenz (4.2 / A2 / A3) ----------

    [Fact]
    public async Task Push_accepted_erzeugt_Buchung_und_Changelogzeile()
    {
        var (acc, _, pool, cat, _) = await SeedMasterDataAsync();
        var (_, deviceToken) = await PairAsync();
        var deviceClient = CreateDeviceClient(deviceToken);

        var response = await deviceClient.PostAsJsonAsync(
            "/api/v1/sync/push", ValidPushPayload("op-1", acc, pool, cat));
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<JsonElement>())!;

        Assert.Equal("accepted", result.GetProperty("status").GetString());
        Assert.Equal("exp-op-1", result.GetProperty("bookingId").GetString());
        Assert.Equal(1, result.GetProperty("protocolGeneration").GetInt64());

        // Die Buchung ist fachlich wirksam: Salden zeigen den Abzug.
        var balances = await (await _sessionClient.GetAsync("/api/v1/balances"))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal(-8_500, balances.GetProperty("accounts").EnumerateArray()
            .Single(a => a.GetProperty("accountId").GetString() == acc)
            .GetProperty("balance").GetInt64());
        Assert.Equal(-8_500, balances.GetProperty("pools").EnumerateArray()
            .Single(p => p.GetProperty("poolId").GetString() == pool)
            .GetProperty("balance").GetInt64());

        // Und sie ist im Changelog nachvollziehbar (entityType booking,
        // entityId = BookingId).
        var changes = await GetChangesAsync(deviceClient, 0);
        var changesArray = changes.GetProperty("changes").EnumerateArray().ToList();
        var bookingChange = Assert.Single(changesArray,
            c => c.GetProperty("entityType").GetString() == "booking");
        Assert.Equal("exp-op-1", bookingChange.GetProperty("entityId").GetString());
        Assert.True(bookingChange.GetProperty("revision").GetInt32() > 0);
    }

    [Fact]
    public async Task Gleicher_Push_dreimal_erzeugt_genau_eine_Buchung()
    {
        var (acc, _, pool, cat, _) = await SeedMasterDataAsync();
        var (_, deviceToken) = await PairAsync();
        var deviceClient = CreateDeviceClient(deviceToken);

        var payload = ValidPushPayload("op-idem", acc, pool, cat);

        var erste = await (await deviceClient
            .PostAsJsonAsync("/api/v1/sync/push", payload))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal("accepted", erste.GetProperty("status").GetString());
        var bookingId = erste.GetProperty("bookingId").GetString()!;

        for (var runde = 2; runde <= 3; runde++)
        {
            var weitere = await (await deviceClient
                .PostAsJsonAsync("/api/v1/sync/push", payload))
                .Content.ReadFromJsonAsync<JsonElement>()!;
            var status = weitere.GetProperty("status").GetString();
            Assert.True(status == "already_processed", $"Runde {runde}: erwartet duplicate, war '{status}'");
            Assert.Equal(bookingId, weitere.GetProperty("bookingId").GetString());
        }

        // Genau eine Buchung: Konto- und Pool-Saldo zeigen den einmaligen
        // Abzug; im Changelog genau eine Booking-Zeile.
        var balances = await (await _sessionClient.GetAsync("/api/v1/balances"))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal(-8_500, balances.GetProperty("accounts").EnumerateArray()
            .Single(a => a.GetProperty("accountId").GetString() == acc)
            .GetProperty("balance").GetInt64());
        Assert.Equal(-8_500, balances.GetProperty("pools").EnumerateArray()
            .Single(p => p.GetProperty("poolId").GetString() == pool)
            .GetProperty("balance").GetInt64());

        var changes = await GetChangesAsync(deviceClient, 0);
        var bookingZeilen = changes.GetProperty("changes").EnumerateArray()
            .Count(c => c.GetProperty("entityType").GetString() == "booking");
        Assert.Equal(1, bookingZeilen);
    }

    /// <summary>
    /// Referenzfall A3: die Antwort geht verloren (Netzwerkabbruch nach
    /// Server-Commit), das Gerät sendet dieselbe Operation erneut — es
    /// entsteht keine zweite Buchung.
    /// </summary>
    [Fact]
    public async Task A3_Wiederholung_nach_verlorener_Antwort_doppelbuchung_frei()
    {
        var (acc, _, pool, cat, _) = await SeedMasterDataAsync();
        var (_, deviceToken) = await PairAsync();
        var deviceClient = CreateDeviceClient(deviceToken);

        var payload = ValidPushPayload("op-verify", acc, pool, cat);

        var erste = await (await deviceClient
            .PostAsJsonAsync("/api/v1/sync/push", payload))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal("accepted", erste.GetProperty("status").GetString());
        var bookingId = erste.GetProperty("bookingId").GetString()!;

        // „Verlorene" Antwort → Gerät wiederholt exakt dieselbe Operation.
        var wiederholt = await (await deviceClient
            .PostAsJsonAsync("/api/v1/sync/push", payload))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal("already_processed", wiederholt.GetProperty("status").GetString());
        Assert.Equal(bookingId, wiederholt.GetProperty("bookingId").GetString());

        // Journal zeigt genau eine Buchung für die Operation.
        var bookings = await (await _sessionClient.GetAsync("/api/v1/bookings"))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal(1, bookings.GetArrayLength());
        Assert.Equal(bookingId,
            bookings[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Gleiche_OperationId_mit_an_dem_Inhalt_liefert_409_und_aendert_nichts()
    {
        var (acc, _, pool, cat, _) = await SeedMasterDataAsync();
        var (_, deviceToken) = await PairAsync();
        var deviceClient = CreateDeviceClient(deviceToken);

        var erste = await (await deviceClient
            .PostAsJsonAsync("/api/v1/sync/push",
                ValidPushPayload("op-mm", acc, pool, cat, totalAmount: 8_500)))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal("accepted", erste.GetProperty("status").GetString());

        // Gleiche Operations-ID, anderer Inhalt (anderer Betrag) → Fehler.
        var abweichend = await deviceClient.PostAsJsonAsync("/api/v1/sync/push",
            ValidPushPayload("op-mm", acc, pool, cat, totalAmount: 9_999));
        Assert.Equal(HttpStatusCode.Conflict, abweichend.StatusCode);
        var body = await abweichend.Content.ReadFromJsonAsync<JsonElement>()!;
        // Verbindliches API-Fehlerformat: error.code = payload_mismatch.
        Assert.Equal("payload_mismatch",
            body.GetProperty("error").GetProperty("code").GetString());

        // Die ursprüngliche Buchung bleibt unverändert.
        var balances = await (await _sessionClient.GetAsync("/api/v1/balances"))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal(-8_500, balances.GetProperty("accounts").EnumerateArray()
            .Single(a => a.GetProperty("accountId").GetString() == acc)
            .GetProperty("balance").GetInt64());
    }

    [Fact]
    public async Task Ungueltige_Payloads_liefern_400_und_persistieren_nichts()
    {
        var (acc, _, pool, cat, tax) = await SeedMasterDataAsync();
        var (_, deviceToken) = await PairAsync();
        var deviceClient = CreateDeviceClient(deviceToken);

        var ungültig = new (string Name, object Payload)[]
        {
            ("unbekanntes Konto", ValidPushPayload("u-1", "acc-unkannt", pool, cat)),
            ("unbekannter Pool", ValidPushPayload("u-2", acc, "pool-unkannt", cat)),
            ("unbekannte Kategorie", ValidPushPayload("u-3", acc, pool, "cat-unkannt")),
            ("Split-Summe ungleich Gesamt",
                ValidPushPayload("u-4", acc, pool, cat,
                    splitAmount: 1_000, totalAmount: 2_000)),
            ("Gesamtbetrag null",
                ValidPushPayload("u-5", acc, pool, cat, totalAmount: 0)),
            ("Split-Betrag null",
                ValidPushPayload("u-6", acc, pool, cat, splitAmount: 0)),
            ("ungültiges Datum",
                ValidPushPayload("u-7", acc, pool, cat, date: "31.12.2026")),
            ("unbekannter Typ",
                ValidPushPayload("u-8", acc, pool, cat, type: "transfer")),
            ("Steuerkategorie bei relevant=false",
                ValidPushPayload("u-9", acc, pool, cat,
                    tax: new { relevant = false, taxCategoryId = tax, markedAmount = (long?)100 })),
            ("Teilbetrag größer als Split",
                ValidPushPayload("u-10", acc, pool, cat,
                    tax: new { relevant = true, taxCategoryId = tax, markedAmount = (long?)99_999 })),
        };

        foreach (var (name, payload) in ungültig)
        {
            var response = await deviceClient
                .PostAsJsonAsync("/api/v1/sync/push", payload);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
                $"{name}: erwartet 400, war {(int)response.StatusCode}");
        }

        // Nichts wurde persistiert: dieselbe Operations-ID wie ein
        // zuvor abgelehnter Versuch ist jetzt frei für einen gültigen
        // Inhalt (kein Idempotenznachweis aus dem 400-Pfad).
        var nachtrag = await (await deviceClient
            .PostAsJsonAsync("/api/v1/sync/push",
                ValidPushPayload("u-4", acc, pool, cat)))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal("accepted", nachtrag.GetProperty("status").GetString());

        var balances = await (await _sessionClient.GetAsync("/api/v1/balances"))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal(-8_500, balances.GetProperty("accounts").EnumerateArray()
            .Single(a => a.GetProperty("accountId").GetString() == acc)
            .GetProperty("balance").GetInt64());
    }

    [Fact]
    public async Task Push_mit_archivierter_Stammdaten_referenz_liefert_400()
    {
        var (acc, acc2, pool, cat, _) = await SeedMasterDataAsync();
        var archived = await _sessionClient.PostAsync(
            $"/api/v1/accounts/{acc2}/archive", new StringContent(string.Empty));
        archived.EnsureSuccessStatusCode();

        var (_, deviceToken) = await PairAsync();
        var deviceClient = CreateDeviceClient(deviceToken);

        var response = await deviceClient.PostAsJsonAsync(
            "/api/v1/sync/push", ValidPushPayload("op-arch", acc2, pool, cat));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Parallele_Doppelsendung_ergibt_genau_eine_Buchung()
    {
        var (acc, _, pool, cat, _) = await SeedMasterDataAsync();
        var (_, deviceToken) = await PairAsync();
        var deviceClient = CreateDeviceClient(deviceToken);

        var payload = ValidPushPayload("op-parallel", acc, pool, cat);
        var ergebnisse = await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => deviceClient.PostAsJsonAsync("/api/v1/sync/push", payload)));

        var statuses = ergebnisse
            .Select(async r => (await r.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("status").GetString()!)
            .Select(t => t.GetAwaiter().GetResult()).ToList();

        Assert.Equal(1, statuses.Count(s => s == "accepted"));
        Assert.Equal(2, statuses.Count(s => s == "already_processed"));

        var balances = await (await _sessionClient.GetAsync("/api/v1/balances"))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal(-8_500, balances.GetProperty("accounts").EnumerateArray()
            .Single(a => a.GetProperty("accountId").GetString() == acc)
            .GetProperty("balance").GetInt64());
    }

    [Fact]
    public async Task Push_übernimmt_Steuerkennzeichnung_ausdem_Payload()
    {
        var (acc, _, pool, cat, tax) = await SeedMasterDataAsync();
        var (_, deviceToken) = await PairAsync();
        var deviceClient = CreateDeviceClient(deviceToken);

        var payload = ValidPushPayload("op-tax", acc, pool, cat,
            tax: new { relevant = true, taxCategoryId = tax, markedAmount = (long?)1_900 });
        var response = await (await deviceClient
            .PostAsJsonAsync("/api/v1/sync/push", payload))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal("accepted", response.GetProperty("status").GetString());
        var bookingId = response.GetProperty("bookingId").GetString()!;

        var booking = await (await _sessionClient.GetAsync($"/api/v1/bookings/{bookingId}"))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        var splits = booking.GetProperty("splits").EnumerateArray().ToList();
        var steuer = splits[0].GetProperty("tax");
        Assert.True(steuer.ValueKind != JsonValueKind.Null);
        Assert.True(steuer.GetProperty("relevant").GetBoolean());
        Assert.Equal(1_900, steuer.GetProperty("markedAmount").GetInt64());
    }

    // ---------- Changelog (4.3) ----------

    [Fact]
    public async Task Stammdaten_Aenderungen_erzeugen_ChangelogZeilen_in_Reihenfolge()
    {
        var (acc, _, pool, _, _) = await SeedMasterDataAsync();

        var renamed = await _sessionClient.PutAsJsonAsync(
            $"/api/v1/accounts/{acc}/name", new { name = "Umbenannt" });
        renamed.EnsureSuccessStatusCode();

        var archived = await _sessionClient.PostAsync(
            $"/api/v1/pools/{pool}/archive", new StringContent(string.Empty));
        archived.EnsureSuccessStatusCode();

        var (_, deviceToken) = await PairAsync();
        var deviceClient = CreateDeviceClient(deviceToken);

        var changes = await GetChangesAsync(deviceClient, 0);
        var array = changes.GetProperty("changes").EnumerateArray().ToList();

        // 5 Erzeugungen (Seed) + 1 Umbenennung + 1 Archivierung.
        Assert.Equal(7, array.Count);
        Assert.Equal(
            new[]
            {
                ("account", "created"), ("account", "created"),
                ("pool", "created"), ("category", "created"),
                ("taxCategory", "created"),
                ("account", "changed"), ("pool", "changed")
            },
            array.Select(e => (
                e.GetProperty("entityType").GetString()!,
                e.GetProperty("id").GetInt64() <= 5 ? "created" : "changed")));

        // Reihenfolge ist die Änderungsreihenfolge (aufsteigende Ids).
        var ids = array.Select(e => e.GetProperty("id").GetInt64()).ToList();
        Assert.Equal(ids.OrderBy(i => i), ids);

        // Pull ab der letzten Id: leer, vollständig, nicht abgeschnitten.
        var rest = await GetChangesAsync(deviceClient, ids.Last());
        Assert.Equal(0, rest.GetProperty("changes").GetArrayLength());
        Assert.True(rest.GetProperty("complete").GetBoolean());
        Assert.False(rest.GetProperty("truncated").GetBoolean());
        Assert.Equal(ids.Last(), rest.GetProperty("cursor").GetInt64());
    }

    [Fact]
    public async Task Changelog_Truncated_bei_limit_und_Weiterziehen_funktioniert()
    {
        // 6 Stammdaten-Änderungen erzeugen (Seed = 4, dazu 2 Umbenennungen).
        var (acc, _, pool, _, _) = await SeedMasterDataAsync();
        var r1 = await _sessionClient.PutAsJsonAsync(
            $"/api/v1/accounts/{acc}/name", new { name = "Runde 1" });
        r1.EnsureSuccessStatusCode();
        var r2 = await _sessionClient.PutAsJsonAsync(
            $"/api/v1/pools/{pool}/name", new { name = "Runde 2" });
        r2.EnsureSuccessStatusCode();

        var (_, deviceToken) = await PairAsync();
        var deviceClient = CreateDeviceClient(deviceToken);

        // 7 Zeilen insgesamt; der Cursor zeigt immer auf das größte Id
        // der Tabelle, nicht auf das letzte Id der Seite.
        var seite1 = await GetChangesAsync(deviceClient, 0, limit: 2);
        Assert.Equal(2, seite1.GetProperty("changes").GetArrayLength());
        Assert.True(seite1.GetProperty("truncated").GetBoolean());
        Assert.False(seite1.GetProperty("complete").GetBoolean());
        Assert.Equal(7, seite1.GetProperty("cursor").GetInt64());

        var seite2 = await GetChangesAsync(deviceClient, 2, limit: 2);
        Assert.Equal(2, seite2.GetProperty("changes").GetArrayLength());
        Assert.True(seite2.GetProperty("truncated").GetBoolean());

        var seite3 = await GetChangesAsync(deviceClient, 4, limit: 2);
        Assert.Equal(2, seite3.GetProperty("changes").GetArrayLength());
        Assert.True(seite3.GetProperty("truncated").GetBoolean());

        var seite4 = await GetChangesAsync(deviceClient, 6, limit: 2);
        Assert.Equal(1, seite4.GetProperty("changes").GetArrayLength());
        Assert.False(seite4.GetProperty("truncated").GetBoolean());
        Assert.True(seite4.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task Changelog_ablehnt_negativen_Cursor()
    {
        var (_, deviceToken) = await PairAsync();
        var deviceClient = CreateDeviceClient(deviceToken);

        var response = await deviceClient.GetAsync("/api/v1/sync/changes?cursor=-1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- Referenzfall A1: 20-fache Wiederholung ----------

    /// <summary>
    /// 20 verschiedene Ausgaben werden gepusht, eine davon wird
    /// (simuliert nach verlorener Antwort) erneut gesendet. Ergebnis:
    /// genau 20 Buchungen, Salden stimmen auf den Cent, das Changelog
    /// enthält 20 Booking-Zeilen und die Stammdaten-Änderungen.
    /// </summary>
    [Fact]
    public async Task A1_Zwanzig_Pushes_eins_doppelt_erzeugen_zwanzig_Buchungen()
    {
        var (acc, _, pool, cat, _) = await SeedMasterDataAsync();
        var (_, deviceToken) = await PairAsync();
        var deviceClient = CreateDeviceClient(deviceToken);

        const int anzahl = 20;
        long gesamt = 0;

        var payloade = Enumerable.Range(1, anzahl).Select(i =>
        {
            var betrag = 1_000L * i; // 10,00 € bis 200,00 €
            gesamt += betrag;
            return new
            {
                operationId = $"op-a1-{i}",
                type = "expense",
                payload = new
                {
                    id = $"exp-a1-{i}",
                    date = "2026-11-10",
                    totalAmount = betrag,
                    accountId = acc,
                    note = (string?)null,
                    splits = new object[]
                    {
                        new
                        {
                            id = "sp-1",
                            amount = betrag,
                            poolId = pool,
                            categoryId = cat,
                            note = (string?)null,
                            tax = (object?)null
                        }
                    },
                    expectedBaseRevision = (int?)null
                },
                payloadHash = "0".PadLeft(64, '0'),
                createdAtUtc = "2026-11-10T12:00:00Z"
            };
        }).ToList();

        var statuses = new List<string>();
        foreach (var payload in payloade)
        {
            var result = await (await deviceClient
                .PostAsJsonAsync("/api/v1/sync/push", payload))
                .Content.ReadFromJsonAsync<JsonElement>()!;
            Assert.Equal("accepted", result.GetProperty("status").GetString());
            statuses.Add(result.GetProperty("status").GetString()!);
        }

        // Wiederholung einer bereits angenommenen Operation (A3-Szenario
        // mitten im Verkehr): exakt dieselbe Payload, gleicher Inhalt.
        var duplicate = await (await deviceClient
            .PostAsJsonAsync("/api/v1/sync/push", payloade[3]))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal("already_processed", duplicate.GetProperty("status").GetString());
        Assert.Equal("exp-a1-4", duplicate.GetProperty("bookingId").GetString());

        // Salden: Konto und Pool zeigen die einmalige Summe aller 20.
        var balances = await (await _sessionClient.GetAsync("/api/v1/balances"))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal(-gesamt, balances.GetProperty("accounts").EnumerateArray()
            .Single(a => a.GetProperty("accountId").GetString() == acc)
            .GetProperty("balance").GetInt64());
        Assert.Equal(-gesamt, balances.GetProperty("pools").EnumerateArray()
            .Single(p => p.GetProperty("poolId").GetString() == pool)
            .GetProperty("balance").GetInt64());

        // Journal: 20 Buchungen (keine 19, keine 21).
        var bookings = await (await _sessionClient.GetAsync("/api/v1/bookings"))
            .Content.ReadFromJsonAsync<JsonElement>()!;
        Assert.Equal(anzahl, bookings.GetArrayLength());

        // Changelog: 5 Stammdaten-Zeilen (Seed) + 20 Booking-Zeilen.
        var changes = await GetChangesAsync(deviceClient, 0);
        var changesArray = changes.GetProperty("changes").EnumerateArray().ToList();
        Assert.Equal(25, changesArray.Count);
        Assert.Equal(20, changesArray.Count(
            c => c.GetProperty("entityType").GetString() == "booking"));
        Assert.True(changes.GetProperty("complete").GetBoolean());
        Assert.False(changes.GetProperty("truncated").GetBoolean());
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

    private static async Task<JsonElement> GetChangesAsync(
        HttpClient deviceClient, long after, int limit = 500)
    {
        var response = await deviceClient
            .GetAsync($"/api/v1/sync/changes?cursor={after}&limit={limit}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    /// <summary>
    /// Gültiges Push-Envelope (Kontrakt D5): <c>payload</c> mit
    /// ExpenseCommand-Shape (exakt wie mobile Outbox, expense-payload.ts),
    /// <c>payloadHash</c> als Geräte-Prüfsumme (audit-only — die
    /// Idempotenzentscheidung nutzt den serverseitig berechneten
    /// Kanonik-Hash).
    /// </summary>
    private static object ValidPushPayload(
        string operationId, string accountId, string poolId, string categoryId,
        long splitAmount = 8_500, long totalAmount = 8_500,
        string date = "2026-11-10", string type = "expense", object? tax = null)
    {
        return new
        {
            operationId,
            type,
            payload = new
            {
                id = $"exp-{operationId}",
                date,
                totalAmount,
                accountId,
                note = (string?)null,
                splits = new object[]
                {
                    new
                    {
                        id = "sp-1",
                        amount = splitAmount,
                        poolId,
                        categoryId,
                        note = (string?)null,
                        tax
                    }
                },
                expectedBaseRevision = (int?)null
            },
            payloadHash = "0".PadLeft(64, '0'),
            createdAtUtc = "2026-11-10T12:00:00Z"
        };
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

    /// <summary>
    /// Legt 2 Konten, 1 Pool, 1 Kategorie und 1 Steuerkategorie an
    /// (Session-API) und liefert die Ids zurück.
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
