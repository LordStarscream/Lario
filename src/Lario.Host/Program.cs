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
