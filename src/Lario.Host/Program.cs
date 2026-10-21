using System.Net;
using System.Reflection;
using Lario.Application.Journal;
using Lario.Host.Api;
using Lario.Host.Api.Attachments;
using Lario.Host.Api.Journal;
using Lario.Host.Api.MasterData;
using Lario.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
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
//   Lario:Https:Address  (Default 127.0.0.1)
//   Lario:Https:Port     (Default 8443)
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

// Beleg-Upload-Limit (Schritt 7a, Design D3): die maximale Bilddatei
// (Default 10 MB) bestimmt zusätzlich das Request-Body-Limit (Datei plus
// Multipart-Overhead), damit überdimensionierte Uploads bereits in Kestrel
// mit 413 abgewiesen werden, bevor sie gepuffert oder verarbeitet werden.
var attachmentMaxBytes = builder.Configuration.GetValue("Lario:Attachments:MaxBytes", 10_000_000L);
var maxRequestBodySize = attachmentMaxBytes + 1_000_000;

builder.WebHost.UseKestrel(options =>
{
    options.Listen(IPAddress.Parse(httpAddress), httpPort);
    options.Limits.MaxRequestBodySize = maxRequestBodySize;
    if (useHttps && httpsCertificate is not null)
    {
        options.Listen(IPAddress.Parse(httpsAddress), httpsPort, listen => listen.UseHttps(httpsCertificate));
    }
});

// Multipart-Parsing auf dasselbe Limit ausrichten (sonst Default 128 MB).
builder.Services.Configure<FormOptions>(options =>
    options.MultipartBodyLengthLimit = maxRequestBodySize);

builder.Services.AddOpenApi();

// Datenablage deterministisch auflösen (Konfiguration, XDG_DATA_HOME oder
// ~/.local/share) und beim Start anlegen (idempotent: Neustart findet die
// bestehende Ablage wieder, es entsteht keine zweite). Die SQLite-Datei
// selbst liegt ab Schritt 3 dort.
var dataPaths = LarioDataPaths.Resolve(builder.Configuration["Lario:DataDirectory"]).EnsureCreated();

// Datenschicht (Schritt 3): EF Core / SQLite + Repositories +
// Anwendungsfälle. Die produktive REST-API folgt in Schritt 7.
builder.Services.AddLarioData(
    $"Data Source={dataPaths.DatabaseFilePath}",
    dataPaths.AttachmentsDirectory);

// Lokale Authentifizierung (Schritt 7, Design D1/D2): ein lokaler
// Benutzer, Bearer-Token für /api/v1. Sind Username/Password
// konfiguriert, gelten sie; andernfalls erzeugt der Host beim ersten
// Start credentials.json im Datenverzeichnis und zeigt das Passwort
// einmalig auf der Konsole an (Design D2).
var authTokenValidityMinutes = Math.Max(1, builder.Configuration.GetValue("Lario:Auth:TokenValidityMinutes", 720));
builder.Services.AddSingleton(new AuthTokenStore(TimeSpan.FromMinutes(authTokenValidityMinutes)));
builder.Services.AddSingleton(new AuthService(
    builder.Configuration.GetValue<string>("Lario:Auth:Username"),
    builder.Configuration.GetValue<string>("Lario:Auth:Password"),
    dataPaths.Root));

var app = builder.Build();

// Erste Migration (und spätere) anwenden; ist ohne offene Migration ein No-op.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LarioDbContext>();
    db.Database.Migrate();

    // Upgrade-Backfill (Schritt-5-Review P2d): Datenbanken aus Zeiten
    // vor der Positionsverwaltung (Schritt 5) können offene
    // Ausgleichspositionen ohne Zeile tragen. Der Backfill führt sie
    // nach — atomar, idempotent, vor der ersten Abfrage.
    var backfill = scope.ServiceProvider.GetRequiredService<SettlementPositionBackfillService>();
    var backfilledRows = await backfill.EnsureConsistentAsync();
    if (backfilledRows > 0)
    {
        Console.WriteLine(
            $"Lario: {backfilledRows} offene Ausgleichsposition(en) nachgeführt (Upgrade-Backfill)." +
            " Siehe docs/step-5/ — die Datenbasis war vor der Positionsverwaltung angelegt.");
    }

    // Beleg-Abgleich beim Serverstart (Schritt 7a, Design D9b): finale
    // Dateien ohne Metadatenzeile (Waisen) und offene .part-Dateien
    // (Abbruch-Reste) werden entfernt; Metadaten ohne Datei bleiben
    // (Download liefert 410).
    var reconcile = scope.ServiceProvider.GetRequiredService<Lario.Application.Attachments.AttachmentService>();
    var reconcileResult = await reconcile.ReconcileAsync();
    if (reconcileResult.OrphanedFilesDeleted > 0 || reconcileResult.PartFilesDeleted > 0)
    {
        Console.WriteLine(
            $"Lario: Beleg-Abgleich: {reconcileResult.OrphanedFilesDeleted} Waisen-Datei(en) und " +
            $"{reconcileResult.PartFilesDeleted} Abbruch-Rest(e) entfernt, " +
            $"{reconcileResult.MetadataWithoutFile} Metadatenzeile(n) ohne Datei (Download liefert 410).");
    }
}

// Fehlerformat + Bearer-Authentifizierung für die API (Schritt 7,
// Design §2.3/D1). Statische Dateien und das SPA-Fallback bleiben
// unberührt (die Prüfung greift nur auf /api/v1 außer /auth/login).
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseApiAuth();

// Authentifizierungs-Endpunkte (Design D1).
app.MapAuth();

// Stammdaten (Schritt 7, T2) — /api/v1/accounts|pools|categories|tax-categories.
app.MapMasterData();

// Belege (Schritt 7a, Design D3) — /api/v1/attachments + bookings/{id}/attachments.
app.MapAttachments();

// Journal-Leseendpunkte (Schritt 7, T3) — /api/v1/bookings|balances|settlements.
app.MapJournalRead();

// Journal-Schreibendpunkte (Schritt 7, T4).
app.MapJournalWrite();

var assembly = Assembly.GetExecutingAssembly();
var version = assembly.GetName().Version?.ToString(3) ?? "0.0.0";

// Produces<T> teilt dem ApiExplorer den Antworttyp mit, damit der
// OpenAPI-Generator vollständige Schemata für die TypeScript-Client-
// Generierung erzeugen kann.
app.MapGet("/api/health", () => Results.Ok(new HealthResponse("ok", dataPaths.Root)))
    .WithName("getHealth")
    .Produces<HealthResponse>(StatusCodes.Status200OK);

app.MapGet("/api/version", () => Results.Ok(new VersionResponse("lario", version)))
    .WithName("getVersion")
    .Produces<VersionResponse>(StatusCodes.Status200OK);

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

/// <summary>
/// Öffentliche Teilklasse, damit WebApplicationFactory&lt;Program&gt; in den
/// API-Tests (tests/Lario.Api.Tests) den Host starten kann.
/// </summary>
public partial class Program { }

/// <summary>Antwort von /api/health.</summary>
public sealed record HealthResponse(string Status, string DataDirectory);

/// <summary>Antwort von /api/version.</summary>
public sealed record VersionResponse(string Name, string Version);
