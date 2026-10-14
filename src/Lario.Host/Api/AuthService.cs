using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lario.Host.Api;

/// <summary>
/// Lokale Benutzerkredenzials (Schritt 7, Design D2):
/// - sind <c>Lario:Auth:Username</c>/<c>Lario:Auth:Password</c> konfiguriert,
///   gelten sie (keine Datei wird angelegt);
/// - andernfalls erzeugt der Host beim ersten Start ein zufälliges
///   16-Zeichen-Passwort, speichert es als PBKDF2-SHA256-Hash mit
///   100.000 Iterationen in <c>{DataDirectory}/credentials.json</c>
///   (Dateirechte 0600) und gibt das Klartext-Passwort einmalig auf der
///   Konsole aus. Der Hash wird bei jedem Start neu geladen.
/// </summary>
public sealed class AuthService
{
    private const int Pbkdf2Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int GeneratedPasswordLength = 16;

    /// <summary>Unmissverständliches Alphabet (kein 0/O, kein 1/I/l).</summary>
    private static readonly char[] PasswordAlphabet =
        "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789".ToCharArray();

    public string Username { get; }

    private readonly string? _configuredPassword;
    private readonly byte[]? _storedSalt;
    private readonly byte[]? _storedHash;

    public AuthService(string? username, string? password, string? dataDirectory)
    {
        Username = string.IsNullOrWhiteSpace(username) ? "lario" : username!;
        _configuredPassword = string.IsNullOrWhiteSpace(password) ? null : password!;

        if (_configuredPassword is null && dataDirectory is not null)
        {
            var (salt, hash) = LoadOrCreateCredentials(
                Path.Combine(dataDirectory, "credentials.json"), Username);
            _storedSalt = salt;
            _storedHash = hash;
        }
    }

    /// <summary>
    /// Prüft Benutzername + Passwort. Vergleich ist laufzeitstabil
    /// (FixedTimeEquals bei gleicher Länge; unterschiedliche Längen scheitern
    /// bereits an der Hashlänge).
    /// </summary>
    public bool Verify(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        if (!string.Equals(username, Username, StringComparison.Ordinal))
        {
            return false;
        }

        if (_configuredPassword is not null)
        {
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(password),
                Encoding.UTF8.GetBytes(_configuredPassword));
        }

        return _storedSalt is not null
            && _storedHash is not null
            && VerifyAgainstStored(password, _storedSalt, _storedHash);
    }

    private static bool VerifyAgainstStored(string password, byte[] salt, byte[] expectedHash)
    {
        var actual = Derive(password, salt);
        return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }

    private static (byte[] Salt, byte[] Hash) LoadOrCreateCredentials(string path, string username)
    {
        if (File.Exists(path))
        {
            var json = JsonNode.Parse(File.ReadAllText(path))
                ?? throw new InvalidOperationException(
                    $"Auth-Konfigurationsdatei {path} ist leer oder kein JSON.");
            var salt = Convert.FromBase64String(
                json["salt"]?.GetValue<string>()
                ?? throw new InvalidOperationException(
                    $"Auth-Konfigurationsdatei {path} enthält kein Feld 'salt'."));
            var hash = Convert.FromBase64String(
                json["pbkdf2Sha256"]?.GetValue<string>()
                ?? throw new InvalidOperationException(
                    $"Auth-Konfigurationsdatei {path} enthält kein Feld 'pbkdf2Sha256'."));
            return (salt, hash);
        }

        var generatedPassword = GeneratePassword(GeneratedPasswordLength);
        var generatedSalt = RandomNumberGenerator.GetBytes(SaltSize);
        var generatedHash = Derive(generatedPassword, generatedSalt);

        var document = new JsonObject
        {
            ["username"] = username,
            ["iterations"] = Pbkdf2Iterations,
            ["salt"] = Convert.ToBase64String(generatedSalt),
            ["pbkdf2Sha256"] = Convert.ToBase64String(generatedHash),
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        File.WriteAllText(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        TrySetOwnerOnlyPermissions(path);

        // Einmalige Anzeige (Design D2): der Host zeigt das Passwort genau
        // einmal beim ersten Start; danach existiert nur noch der Hash.
        Console.WriteLine($"Lario: Lokales Passwort erzeugt (einmalige Anzeige): {generatedPassword}");
        Console.WriteLine($"Lario: Gespeichert als PBKDF2-Hash in {path}");

        return (generatedSalt, generatedHash);
    }

    private static void TrySetOwnerOnlyPermissions(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            HashSize);

    private static string GeneratePassword(int length)
    {
        // Rejection-Sampling: Byte >= 256 - (256 % Alphabetlänge) werden
        // verworfen, damit jede Alphabetzeichen eine exakt gleich große
        // Chance hat (kein Modulo-Bias).
        var limit = 256 - (256 % PasswordAlphabet.Length);
        var buffer = new byte[1];
        var characters = new char[length];
        for (var i = 0; i < length; i++)
        {
            do
            {
                RandomNumberGenerator.Fill(buffer);
            }
            while (buffer[0] >= limit);
            characters[i] = PasswordAlphabet[buffer[0] % PasswordAlphabet.Length];
        }

        return new string(characters);
    }
}
