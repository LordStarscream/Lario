using Lario.Host.Api;

namespace Lario.Api.Tests;

/// <summary>
/// Credential-Verwaltung (Schritt 7, Design D2): konfigurierte
/// Credentials, erzeugtes Passwort mit PBKDF2-Hash, Neuladen bei Neustart.
/// </summary>
public class AuthServiceTests
{
    private static string NewTempDir() =>
        Directory.CreateTempSubdirectory("lario-auth-test-").FullName;

    [Fact]
    public void Konfigurierte_Credentials_werden_geprueft()
    {
        var service = new AuthService("user-a", "pw-a", dataDirectory: null);

        Assert.True(service.Verify("user-a", "pw-a"));
        Assert.False(service.Verify("user-a", "falsch"));
        Assert.False(service.Verify("user-b", "pw-a"));
        Assert.False(service.Verify(null, "pw-a"));
        Assert.False(service.Verify("user-a", null));
    }

    [Fact]
    public void Ohne_Passwort_wird_am_ersten_Start_ein_Passwort_erzeugt_und_als_Hash_gespeichert()
    {
        var dataDirectory = NewTempDir();
        var output = new StringWriter();
        var original = Console.Out;

        try
        {
            Console.SetOut(output);
            var service = new AuthService(username: null, password: null, dataDirectory);
            var generatedPassword = ExtractGeneratedPassword(output.ToString());

            // credentials.json mit PBKDF2-Hinhalten und 0600-Rechten
            var path = Path.Combine(dataDirectory, "credentials.json");
            Assert.True(File.Exists(path));
            var json = File.ReadAllText(path);
            Assert.Contains("\"salt\"", json);
            Assert.Contains("\"pbkdf2Sha256\"", json);
            Assert.Contains("100000", json);
            Assert.DoesNotContain(generatedPassword, json); // kein Klartext in der Datei
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(path));
            }

            // Einmalige Anzeige auf der Konsole
            Assert.Contains("einmalige Anzeige", output.ToString());

            // Das erzeugte Passwort verifiziert, andere scheitern
            Assert.True(service.Verify("lario", generatedPassword));
            Assert.False(service.Verify("lario", "irgendwas"));
        }
        finally
        {
            Console.SetOut(original);
            TryDelete(dataDirectory);
        }
    }

    [Fact]
    public void Nach_Neustart_gilt_der_Passwort_hash_weiter()
    {
        var dataDirectory = NewTempDir();
        var output = new StringWriter();
        var original = Console.Out;

        try
        {
            Console.SetOut(output);
            _ = new AuthService(username: null, password: null, dataDirectory);
            var generatedPassword = ExtractGeneratedPassword(output.ToString());
            Console.SetOut(original);

            // Zweiter "Start": neuer Service, gleiche Ablage, keine Ausgabe.
            Console.SetOut(output);
            var restarted = new AuthService(username: null, password: null, dataDirectory);
            var secondOutput = output.ToString();
            Console.SetOut(original);
            Assert.Equal(1, secondOutput.Split("einmalige Anzeige").Length - 1);

            Assert.True(restarted.Verify("lario", generatedPassword));
            Assert.False(restarted.Verify("lario", "anderes"));
        }
        finally
        {
            Console.SetOut(original);
            TryDelete(dataDirectory);
        }
    }

    private static string ExtractGeneratedPassword(string consoleOutput)
    {
        const string marker = "einmalige Anzeige): ";
        var index = consoleOutput.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "Konsole muss das erzeugte Passwort anzeigen.");
        var rest = consoleOutput[(index + marker.Length)..];
        var end = rest.IndexOf('\n', StringComparison.Ordinal);
        var password = end < 0 ? rest : rest[..end];
        Assert.False(string.IsNullOrWhiteSpace(password));
        return password.Trim();
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Temp-Verzeichnis bleibt liegen — kein Testergebnis.
        }
    }
}
