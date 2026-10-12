using System.Net;
using System.Reflection;
using Lario.Application;
using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;
using Lario.Infrastructure;
using Microsoft.EntityFrameworkCore;

// Content-Root auf den App-Output legen, damit die statische Weboberfläche
// (wwwroot aus frontend/dist/host, siehe Lario.Host.csproj) unabhängig vom
// Startverzeichnis gefunden wird.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// LAN-Prototyp (Schritt 2): Die Kestrel-Bindung ist explizit und
// konfigurierbar, damit das Verhalten unabhängig von launchSettings /
// ASPNETCORE_URLS deterministisch ist:
//   Lario:Http:Address  (Default 127.0.0.1; "0.0.0.0" für den Heimnetz-Zugang)
//   Lario:Http:Port     (Default 8080)
//   Lario:Https:Address (Default 127.0.0.1)
//   Lario:Https:Port    (Default 8443)
//   Lario:Https:CertificateFile / Lario:Https:CertificateKeyFile
//     (PEM; der HTTPS-Endpunkt wird nur aktiviert, wenn beide gesetzt sind).
// Zertifikatsvertrauen auf dem Gerät: das Zertifikat (oder dessen CA) muss
// im Truststore des Android-Geräts installiert sein.
var httpAddress = builder.Configuration.GetValue("Lario:Http:Address", "127.0.0.1");
var httpPort = builder.Configuration.GetValue("Lario:Http:Port", 8080);
var httpsAddress = builder.Configuration.GetValue("Lario:Https:Address", "127.0.0.1");
var httpsPort = builder.Configuration.GetValue("Lario:Https:Port", 8443);
var certificateFile = builder.Configuration["Lario:Https:CertificateFile"];
var certificateKeyFile = builder.Configuration["Lario:Https:CertificateKeyFile"];
var useHttps = !string.IsNullOrWhiteSpace(certificateFile)
               && !string.IsNullOrWhiteSpace(certificateKeyFile);
var httpsCertificate = useHttps
    // Explizit laden, damit Zertifikat und privater Schlüssel garantiert
    // zusammen geladen werden (HasPrivateKey=true).
    ? System.Security.Cryptography.X509Certificates.X509Certificate2
        .CreateFromPemFile(certificateFile!, certificateKeyFile!)
    : null;

builder.WebHost.UseKestrel(options =>
{
    options.Listen(IPAddress.Parse(httpAddress), httpPort);
    if (useHttps && httpsCertificate is not null)
    {
        options.Listen(IPAddress.Parse(httpsAddress), httpsPort, listen => listen.UseHttps(httpsCertificate));
    }
});

builder.Services.AddOpenApi();

// Datenschicht (Schritt 3): SQLite-Datenbank in der deterministischen
// lokalen Datenablage (Konfiguration, XDG_DATA_HOME oder ~/.local/share).
var dataPaths = LarioDataPaths.Resolve(builder.Configuration["Lario:DataDirectory"]).EnsureCreated();
builder.Services.AddLarioData($"Data Source={dataPaths.DatabaseFilePath}");

var app = builder.Build();

// Erste Migration (und spätere) anwenden; ist ohne offene Migration ein No-op.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LarioDbContext>();
    db.Database.Migrate();
}

var assembly = Assembly.GetExecutingAssembly();
var version = assembly.GetName().Version?.ToString(3) ?? "0.0.0";

// Fachliche Fehler werden als JSON-Antworten statt als 500 zurückgegeben
// (Schritt 3: ungültige Referenzen, Zyklen und Konflikte sichtbar machen).
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (DomainConflictException ex)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (LarioDomainException ex)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});

app.MapGet("/api/health", () => Results.Ok(new HealthResponse("ok", dataPaths.Root)))
    .WithName("getHealth")
    .Produces<HealthResponse>(StatusCodes.Status200OK);

app.MapGet("/api/version", () => Results.Ok(new VersionResponse("lario", version)))
    .WithName("getVersion")
    .Produces<VersionResponse>(StatusCodes.Status200OK);

// ---------------------------------------------------------------------------
// Konten (Schritt 3)
// ---------------------------------------------------------------------------

app.MapGet("/api/accounts", async (IMasterDataRepository repo) =>
        (await repo.LoadCatalogAsync()).Accounts
            .Select(a => new AccountDto(a.Id, a.Name, a.Type.ToString().ToLowerInvariant(), a.IsInterestBearing, a.Status.ToString().ToLowerInvariant(), a.BaseRevision))
            .ToList())
    .WithName("listAccounts")
    .Produces<List<AccountDto>>(StatusCodes.Status200OK);

app.MapPost("/api/accounts", async (CreateAccountRequest req, MasterDataAdmin admin) =>
    {
        if (!Enum.TryParse<AccountType>(req.Type, ignoreCase: true, out var type))
        {
            throw new LarioDomainException($"Unbekannter Kontotyp '{req.Type}'.");
        }

        var account = await admin.CreateAccountAsync(
            req.Id ?? GenerateId("acc"), req.Name, type, req.IsInterestBearing ?? false);
        return Results.Created($"/api/accounts/{account.Id}", ToAccountDto(account));
    })
    .WithName("createAccount")
    .Produces<AccountDto>(StatusCodes.Status201Created)
    .Produces(StatusCodes.Status400BadRequest);

app.MapPost("/api/accounts/{id}/archive", async (string id, MasterDataAdmin admin) =>
    {
        await admin.ArchiveAccountAsync(id);
        return Results.NoContent();
    })
    .WithName("archiveAccount")
    .Produces(StatusCodes.Status204NoContent)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status409Conflict);

// ---------------------------------------------------------------------------
// Pools (Schritt 3)
// ---------------------------------------------------------------------------

app.MapGet("/api/pools", async (IMasterDataRepository repo) =>
        (await repo.LoadCatalogAsync()).Pools
            .Select(p => new PoolDto(p.Id, p.Name, p.Status.ToString().ToLowerInvariant(), p.BaseRevision))
            .ToList())
    .WithName("listPools")
    .Produces<List<PoolDto>>(StatusCodes.Status200OK);

app.MapPost("/api/pools", async (CreatePoolRequest req, MasterDataAdmin admin) =>
    {
        var pool = await admin.CreatePoolAsync(req.Id ?? GenerateId("pool"), req.Name);
        return Results.Created($"/api/pools/{pool.Id}", ToPoolDto(pool));
    })
    .WithName("createPool")
    .Produces<PoolDto>(StatusCodes.Status201Created)
    .Produces(StatusCodes.Status400BadRequest);

app.MapPost("/api/pools/{id}/archive", async (string id, MasterDataAdmin admin) =>
    {
        await admin.ArchivePoolAsync(id);
        return Results.NoContent();
    })
    .WithName("archivePool")
    .Produces(StatusCodes.Status204NoContent)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status409Conflict);

// ---------------------------------------------------------------------------
// Kategorien (Schritt 3)
// ---------------------------------------------------------------------------

app.MapGet("/api/categories", async (IMasterDataRepository repo) =>
    {
        var catalog = await repo.LoadCatalogAsync();
        return catalog.Categories
            .Select(c => ToCategoryDto(c, catalog.ResolveEffectivePreset(c.Id)))
            .ToList();
    })
    .WithName("listCategories")
    .Produces<List<CategoryDto>>(StatusCodes.Status200OK);

app.MapPost("/api/categories", async (CreateCategoryRequest req, MasterDataAdmin admin) =>
    {
        var category = await admin.CreateCategoryAsync(
            req.Id ?? GenerateId("cat"), req.Name, req.ParentId);
        return Results.Created($"/api/categories/{category.Id}", ToCategoryDto(category, null));
    })
    .WithName("createCategory")
    .Produces<CategoryDto>(StatusCodes.Status201Created)
    .Produces(StatusCodes.Status400BadRequest);

app.MapPost("/api/categories/{id}/archive", async (string id, MasterDataAdmin admin) =>
    {
        await admin.ArchiveCategoryAsync(id);
        return Results.NoContent();
    })
    .WithName("archiveCategory")
    .Produces(StatusCodes.Status204NoContent)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status409Conflict);

app.MapPut("/api/categories/{id}/tax-preset", async (string id, SetTaxPresetRequest req, MasterDataAdmin admin) =>
    {
        await admin.SetTaxPresetAsync(id, req.TaxRelevant, req.TaxCategoryId, req.InheritToChildren);
        return Results.NoContent();
    })
    .WithName("setTaxPreset")
    .Produces(StatusCodes.Status204NoContent)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status409Conflict);

app.MapDelete("/api/categories/{id}/tax-preset", async (string id, MasterDataAdmin admin) =>
    {
        await admin.RemoveTaxPresetAsync(id);
        return Results.NoContent();
    })
    .WithName("removeTaxPreset")
    .Produces(StatusCodes.Status204NoContent)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status409Conflict);

// ---------------------------------------------------------------------------
// Steuerkategorien (Schritt 3)
// ---------------------------------------------------------------------------

app.MapGet("/api/tax-categories", async (IMasterDataRepository repo) =>
        (await repo.LoadCatalogAsync()).TaxCategories
            .Select(t => new TaxCategoryDto(t.Id, t.Name, t.ParentId, t.Status.ToString().ToLowerInvariant(), t.BaseRevision))
            .ToList())
    .WithName("listTaxCategories")
    .Produces<List<TaxCategoryDto>>(StatusCodes.Status200OK);

app.MapPost("/api/tax-categories", async (CreateTaxCategoryRequest req, MasterDataAdmin admin) =>
    {
        var taxCategory = await admin.CreateTaxCategoryAsync(
            req.Id ?? GenerateId("tax"), req.Name, req.ParentId);
        return Results.Created($"/api/tax-categories/{taxCategory.Id}", ToTaxCategoryDto(taxCategory));
    })
    .WithName("createTaxCategory")
    .Produces<TaxCategoryDto>(StatusCodes.Status201Created)
    .Produces(StatusCodes.Status400BadRequest);

app.MapPost("/api/tax-categories/{id}/archive", async (string id, MasterDataAdmin admin) =>
    {
        await admin.ArchiveTaxCategoryAsync(id);
        return Results.NoContent();
    })
    .WithName("archiveTaxCategory")
    .Produces(StatusCodes.Status204NoContent)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status409Conflict);

// ---------------------------------------------------------------------------
// Anfangsbestände (Schritt 3)
// ---------------------------------------------------------------------------

app.MapGet("/api/opening-balances", async (IMasterDataRepository repo, IBookingRepository bookings) =>
    {
        var catalog = await repo.LoadCatalogAsync();
        var all = await bookings.GetAllAsync();
        return all
            .Where(b => b.Type == BookingType.OpeningBalance)
            .Select(b => ToOpeningBalanceDto(catalog, b))
            .ToList();
    })
    .WithName("listOpeningBalances")
    .Produces<List<OpeningBalanceDto>>(StatusCodes.Status200OK);

app.MapPost("/api/opening-balances", async (CreateOpeningBalanceRequest req, OpeningBalanceService service, IMasterDataRepository repo) =>
    {
        var spec = new OpeningBalanceSpec(
            req.Id ?? GenerateId("init"),
            DateOnly.Parse(req.Date),
            req.AccountId,
            new Money(req.TotalCents),
            (req.Distribution ?? []).Select(d => new PoolAllocation(d.PoolId, new Money(d.AmountCents))).ToList(),
            req.Note);

        var result = await service.CreateAsync(spec);
        var catalog = await repo.LoadCatalogAsync();
        var dto = ToOpeningBalanceDto(catalog, result.Booking);
        return result.Created
            ? Results.Created($"/api/opening-balances/{dto.Id}", dto)
            : Results.Ok(dto); // Idempotenz: gleicher Inhalt, keine zweite Buchung
    })
    .WithName("createOpeningBalance")
    .Produces<OpeningBalanceDto>(StatusCodes.Status201Created)
    .Produces<OpeningBalanceDto>(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status409Conflict);

// ---------------------------------------------------------------------------
// Salden (Schritt 3)
// ---------------------------------------------------------------------------

app.MapGet("/api/balances", async (BalanceQueryService query) =>
    {
        var balances = await query.GetBalancesAsync();
        return new BalanceOverviewDto(
            balances.Accounts.Select(a => new AccountBalanceDto(a.AccountId, a.Name, a.Status.ToString().ToLowerInvariant(), a.Balance.Cents)).ToList(),
            balances.Pools.Select(p => new PoolBalanceDto(p.PoolId, p.Name, p.Status.ToString().ToLowerInvariant(), p.Balance.Cents)).ToList(),
            balances.TotalAccountBalance.Cents,
            balances.TotalPoolBalance.Cents,
            balances.TotalIncome.Cents,
            balances.AccountPoolGap.Cents,
            balances.UnallocatedOpeningBalances
                .Select(u => new UnallocatedOpeningBalanceDto(u.OpeningBalanceId, u.Date, u.AccountId, u.Unallocated.Cents))
                .ToList());
    })
    .WithName("getBalances")
    .Produces<BalanceOverviewDto>(StatusCodes.Status200OK);

app.MapOpenApi();

// Statische Weboberfläche: das vorab gebaute Frontend liegt über die
// Lario.Host.csproj in der wwwroot des Host-Outputs. Ohne Build-Artefakt
// bleibt die API unverändert nutzbar.
if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")))
{
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html");
}

app.Run();

// ---------------------------------------------------------------------------
// Hilfsfunktionen und DTOs
// ---------------------------------------------------------------------------

static string GenerateId(string prefix)
{
    return $"{prefix}-{Guid.NewGuid():N}"[..20];
}

static AccountDto ToAccountDto(Account a) =>
    new(a.Id, a.Name, a.Type.ToString().ToLowerInvariant(), a.IsInterestBearing, a.Status.ToString().ToLowerInvariant(), a.BaseRevision);

static PoolDto ToPoolDto(Pool p) =>
    new(p.Id, p.Name, p.Status.ToString().ToLowerInvariant(), p.BaseRevision);

static CategoryDto ToCategoryDto(Category c, TaxPreset? effectivePreset) =>
    new(c.Id, c.Name, c.ParentId, c.Status.ToString().ToLowerInvariant(), c.BaseRevision,
        ToPresetDto(c.TaxPreset), ToPresetDto(effectivePreset));

static TaxCategoryDto ToTaxCategoryDto(TaxCategory t) =>
    new(t.Id, t.Name, t.ParentId, t.Status.ToString().ToLowerInvariant(), t.BaseRevision);

static TaxPresetDto? ToPresetDto(TaxPreset? preset) =>
    preset is null ? null : new(preset.TaxRelevant, preset.TaxCategoryId, preset.InheritToChildren, preset.BaseRevision);

static OpeningBalanceDto ToOpeningBalanceDto(MasterDataCatalog catalog, Booking booking)
{
    var total = booking.AccountPostings.Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));
    var allocated = booking.PoolPostings.Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));
    var account = booking.AccountPostings.Count == 1 ? booking.AccountPostings[0] : null;

    return new(
        booking.Id,
        booking.Date,
        account?.AccountId ?? string.Empty,
        total.Cents,
        booking.PoolPostings
            .Select(p => new PoolAllocationDto(
                p.PoolId,
                catalog.FindPool(p.PoolId)?.Name ?? p.PoolId,
                p.Amount.Cents))
            .ToList(),
        booking.Note,
        total.Subtract(allocated).Cents);
}

/// <summary>Antwort von /api/health.</summary>
public sealed record HealthResponse(string Status, string DataDirectory);

/// <summary>Antwort von /api/version.</summary>
public sealed record VersionResponse(string Name, string Version);

public sealed record CreateAccountRequest(string? Id, string Name, string Type, bool? IsInterestBearing = null);
public sealed record CreatePoolRequest(string? Id, string Name);
public sealed record CreateCategoryRequest(string? Id, string Name, string? ParentId = null);
public sealed record CreateTaxCategoryRequest(string? Id, string Name, string? ParentId = null);
public sealed record SetTaxPresetRequest(bool? TaxRelevant, string? TaxCategoryId, bool InheritToChildren);
public sealed record CreateOpeningBalanceRequest(
    string? Id, string Date, string AccountId, long TotalCents,
    List<PoolAllocationDto>? Distribution = null, string? Note = null);

public sealed record AccountDto(string Id, string Name, string Type, bool IsInterestBearing, string Status, int BaseRevision);
public sealed record PoolDto(string Id, string Name, string Status, int BaseRevision);
public sealed record CategoryDto(
    string Id, string Name, string? ParentId, string Status, int BaseRevision,
    TaxPresetDto? TaxPreset, TaxPresetDto? EffectivePreset);
public sealed record TaxCategoryDto(string Id, string Name, string? ParentId, string Status, int BaseRevision);
public sealed record TaxPresetDto(bool? TaxRelevant, string? TaxCategoryId, bool InheritToChildren, int BaseRevision);
public sealed record PoolAllocationDto(string PoolId, string PoolName, long AmountCents);
public sealed record OpeningBalanceDto(
    string Id, DateOnly Date, string AccountId, long TotalCents,
    List<PoolAllocationDto> Distribution, string? Note, long UnallocatedCents);
public sealed record AccountBalanceDto(string AccountId, string Name, string Status, long BalanceCents);
public sealed record PoolBalanceDto(string PoolId, string Name, string Status, long BalanceCents);
public sealed record UnallocatedOpeningBalanceDto(
    string OpeningBalanceId, DateOnly Date, string AccountId, long UnallocatedCents);
public sealed record BalanceOverviewDto(
    List<AccountBalanceDto> Accounts,
    List<PoolBalanceDto> Pools,
    long TotalAccountBalanceCents,
    long TotalPoolBalanceCents,
    long TotalIncomeCents,
    long AccountPoolGapCents,
    List<UnallocatedOpeningBalanceDto> UnallocatedOpeningBalances);
