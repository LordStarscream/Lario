using Lario.Infrastructure;

namespace Lario.Infrastructure.Tests;

/// <summary>
/// Verbindliche Regel (Schritt 1): Neustart darf keine leere zweite
/// Datenablage erzeugen — das Datenverzeichnis ist deterministisch.
/// </summary>
public class LarioDataPathsTests
{
    [Fact(DisplayName = "Explizite Konfiguration gewinnt und wird vollqualifiziert")]
    public void Resolve_ConfiguredDirectory_Wins()
    {
        LarioDataPaths paths = LarioDataPaths.Resolve(
            configuredDirectory: "/tmp/lario-test/data",
            homeDirectory: "/home/user",
            xdgDataHome: "/home/user/xdg");

        Assert.Equal("/tmp/lario-test/data", paths.Root);
        Assert.Equal("/tmp/lario-test/data/lario.sqlite", paths.DatabaseFilePath);
    }

    [Fact(DisplayName = "Ohne Konfiguration: XDG_DATA_HOME/lario (XDG-Standard)")]
    public void Resolve_XdgDataHome_IsUsedWhenSet()
    {
        LarioDataPaths paths = LarioDataPaths.Resolve(
            configuredDirectory: null,
            homeDirectory: "/home/user",
            xdgDataHome: "/home/user/.local/share");

        Assert.Equal("/home/user/.local/share/lario", paths.Root);
    }

    [Fact(DisplayName = "Ohne Konfiguration und ohne XDG_DATA_HOME: ~/.local/share/lario")]
    public void Resolve_DefaultXdgPath_WhenNothingSet()
    {
        string? original = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", null);

            LarioDataPaths paths = LarioDataPaths.Resolve(
                configuredDirectory: null,
                homeDirectory: "/home/user",
                xdgDataHome: null);

            Assert.Equal("/home/user/.local/share/lario", paths.Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", original);
        }
    }

    [Fact(DisplayName = "Host-Fall: XDG_DATA_HOME wird aus der Umgebung gelesen (ohne Parameter)")]
    public void Resolve_ReadsXdgDataHomeFromEnvironment()
    {
        string? original = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string envBase = Path.Combine(Path.GetTempPath(), "lario-xdg-env", Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", envBase);

            // Exakt der Host-Aufruf (Program.cs): keine Konfiguration, kein
            // XDG-Parameter — die Umgebung entscheidet.
            LarioDataPaths paths = LarioDataPaths.Resolve();

            Assert.Equal(Path.Combine(envBase, "lario"), paths.Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", original);
            if (Directory.Exists(envBase))
            {
                Directory.Delete(envBase, recursive: true);
            }
        }
    }

    [Fact(DisplayName = "Expliziter XDG-Parameter gewinnt über XDG_DATA_HOME in der Umgebung")]
    public void Resolve_ExplicitXdgParameter_WinsOverEnvironment()
    {
        string? original = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", "/tmp/lario-xdg-env-irrelevant");

            LarioDataPaths paths = LarioDataPaths.Resolve(
                configuredDirectory: null,
                homeDirectory: "/home/user",
                xdgDataHome: "/home/user/xdg");

            Assert.Equal("/home/user/xdg/lario", paths.Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", original);
        }
    }

    [Fact(DisplayName = "Leere Konfiguration wird ignoriert (fallback auf XDG)")]
    public void Resolve_WhiteSpaceConfig_FallsBackToXdg()
    {
        string? original = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", "/home/user/xdg");

            LarioDataPaths paths = LarioDataPaths.Resolve(
                configuredDirectory: "   ",
                homeDirectory: "/home/user",
                xdgDataHome: null);

            Assert.Equal("/home/user/xdg/lario", paths.Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", original);
        }
    }

    [Fact(DisplayName = "Neustart: zweiter Start findet exakt dieselbe Ablage (keine zweite Datenablage)")]
    public void Resolve_Restart_IsDeterministic()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "lario-datapaths-restart", Guid.NewGuid().ToString("N"));
        try
        {
            // Erster Start: Ablage wird angelegt.
            LarioDataPaths firstStart = LarioDataPaths.Resolve(configuredDirectory: tempBase).EnsureCreated();

            // Zweiter Start (Neustart): dieselbe Konfiguration, vorhandene Ablage.
            LarioDataPaths secondStart = LarioDataPaths.Resolve(configuredDirectory: tempBase).EnsureCreated();

            Assert.Equal(firstStart.Root, secondStart.Root);
            Assert.Equal(firstStart.DatabaseFilePath, secondStart.DatabaseFilePath);
            Assert.True(Directory.Exists(firstStart.Root));
        }
        finally
        {
            if (Directory.Exists(tempBase))
            {
                Directory.Delete(tempBase, recursive: true);
            }
        }
    }

    [Fact(DisplayName = "EnsureCreated legt das Verzeichnis an und ist idempotent")]
    public void EnsureCreated_CreatesDirectory_ExactlyOnce()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "lario-datapaths-test", Guid.NewGuid().ToString("N"));
        try
        {
            LarioDataPaths paths = LarioDataPaths.Resolve(configuredDirectory: tempRoot);
            Assert.False(Directory.Exists(tempRoot));

            paths.EnsureCreated();
            Assert.True(Directory.Exists(tempRoot));

            paths.EnsureCreated(); // Neustart
            Assert.True(Directory.Exists(tempRoot));
            // Keine zweite Ablage: nur das eine Verzeichnis existiert.
            Assert.Equal(tempRoot, paths.Root);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
