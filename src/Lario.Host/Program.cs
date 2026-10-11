using System.Net;
using System.Reflection;
using Lario.Infrastructure;

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

var app = builder.Build();

// Datenablage deterministisch auflösen (Konfiguration, XDG_DATA_HOME oder
// ~/.local/share) und beim Start anlegen (idempotent: Neustart findet die
// bestehende Ablage wieder, es entsteht keine zweite). Die SQLite-Datei
// selbst wird ab Schritt 3 dort abgelegt.
var dataPaths = LarioDataPaths.Resolve(builder.Configuration["Lario:DataDirectory"]).EnsureCreated();

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

/// <summary>Antwort von /api/health.</summary>
public sealed record HealthResponse(string Status, string DataDirectory);

/// <summary>Antwort von /api/version.</summary>
public sealed record VersionResponse(string Name, string Version);
