using Lario.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lario.Api.Tests;

/// <summary>
/// WebApplicationFactory für den echten Host (Schritt 7, Task T1):
/// jeder Instanz ein eigenes temporäres Datenverzeichnis (isolierte
/// SQLite-DB), standardmäßig konfigurierte Test-Credentials. Für den
/// Neustart-Test (A4) lässt sich dieselbe Datenverzeichnis über
/// <c>dataDirectory</c> weiterreichen — dann gehört es dem Test.
/// </summary>
public sealed class LarioApiFactory : WebApplicationFactory<Program>
{
    public const string TestUsername = "testuser";
    public const string TestPassword = "test-password-123";

    private readonly string _dataDirectory;
    private readonly bool _ownsDataDirectory;
    private readonly string? _username;
    private readonly string? _password;
    private readonly IReadOnlyDictionary<string, string> _settings;

    /// <param name="dataDirectory">Bestehendes Verzeichnis (z. B. für Neustart-Test); Default: neue Temp-Ablage.</param>
    /// <param name="configuredCredentials">true: Test-Credentials per Konfiguration (keine credentials.json); false: Host erzeugt selbst.</param>
    /// <param name="settings">Weitere Host-Konfiguration (z. B. <c>Lario:Attachments:MaxBytes</c> für Größen-Tests).</param>
    public LarioApiFactory(
        string? dataDirectory = null,
        bool configuredCredentials = true,
        IReadOnlyDictionary<string, string>? settings = null)
    {
        _ownsDataDirectory = dataDirectory is null;
        _dataDirectory = dataDirectory ?? Directory.CreateTempSubdirectory("lario-api-test-").FullName;
        _username = configuredCredentials ? TestUsername : null;
        _password = configuredCredentials ? TestPassword : null;
        _settings = settings ?? new Dictionary<string, string>();
    }

    public string DataDirectory => _dataDirectory;

    public string CredentialFilePath => Path.Combine(_dataDirectory, "credentials.json");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Lario:DataDirectory", _dataDirectory);
        if (_username is not null && _password is not null)
        {
            builder.UseSetting("Lario:Auth:Username", _username);
            builder.UseSetting("Lario:Auth:Password", _password);
        }
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (_ownsDataDirectory)
        {
            try
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Temp-Verzeichnis bleibt liegen (z. B. noch offene Datei-
                // handles) — es ist kein Testergebnis.
            }
        }
    }
}
