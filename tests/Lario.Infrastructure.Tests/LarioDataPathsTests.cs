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
        LarioDataPaths paths = LarioDataPaths.Resolve(
            configuredDirectory: null,
            homeDirectory: "/home/user",
            xdgDataHome: null);

        Assert.Equal("/home/user/.local/share/lario", paths.Root);
    }

    [Fact(DisplayName = "Leere Konfiguration wird ignoriert (fallback auf XDG)")]
    public void Resolve_WhiteSpaceConfig_FallsBackToXdg()
    {
        LarioDataPaths paths = LarioDataPaths.Resolve(
            configuredDirectory: "   ",
            homeDirectory: "/home/user",
            xdgDataHome: null);

        Assert.Equal("/home/user/.local/share/lario", paths.Root);
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
