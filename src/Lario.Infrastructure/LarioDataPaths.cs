namespace Lario.Infrastructure;

/// <summary>
/// Löst das lokale Lario-Datenverzeichnis ein und hält die kanonischen
/// Pfade der lokalen Datenablage.
///
/// Verbindliche Regel (Schritt 1): Neustart darf keine leere zweite
/// Datenablage erzeugen — das Verzeichnis ist daher deterministisch:
/// 1. explizite Konfiguration <c>Lario:DataDirectory</c> (appsettings oder
///    Umgebungsvariable Lario__DataDirectory), falls gesetzt;
/// 2. sonst XDG-Standard: <c>$XDG_DATA_HOME/lario</c> bzw.
///    <c>~/.local/share/lario</c>.
/// </summary>
public sealed class LarioDataPaths
{
    /// <summary>Name der Datenbankdatei innerhalb des Datenverzeichnisses (ab Schritt 3 genutzt).</summary>
    public const string DatabaseFileName = "lario.sqlite";

    private LarioDataPaths(string root) => Root = root;

    /// <summary>Absolutes Wurzelverzeichnis der lokalen Datenablage.</summary>
    public string Root { get; }

    /// <summary>Kanonicaler Pfad der SQLite-Datenbank (Datei entsteht ab Schritt 3).</summary>
    public string DatabaseFilePath => Path.Combine(Root, DatabaseFileName);

    /// <summary>
    /// Löst das Datenverzeichnis deterministisch ein (siehe Klassendokumentation).
    /// Alle Parameter können null sein; explizit übergebene Werte gewinnen
    /// vor dem Umgebungsleser, damit Aufrufer (Host, Tests) die Quelle kontrollieren.
    /// </summary>
    public static LarioDataPaths Resolve(
        string? configuredDirectory = null,
        string? homeDirectory = null,
        string? xdgDataHome = null)
    {
        homeDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            throw new InvalidOperationException("Kann das Home-Verzeichnis nicht ermitteln.");
        }

        string root = !string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.GetFullPath(configuredDirectory)
            : Path.Combine(
                string.IsNullOrWhiteSpace(xdgDataHome)
                    ? Path.Combine(homeDirectory, ".local", "share")
                    : xdgDataHome,
                "lario");

        return new LarioDataPaths(Path.GetFullPath(root));
    }

    /// <summary>
    /// Erstellt das Datenverzeichnis, falls es noch nicht existiert.
    /// Idempotent: ein Neustart findet das bestehende Verzeichnis wieder
    /// und erzeugt keine zweite Ablage.
    /// </summary>
    public LarioDataPaths EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        return this;
    }
}
