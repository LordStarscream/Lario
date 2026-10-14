namespace Lario.Host.Api;

/// <summary>
/// In-Memory-Ablage der lokalen Sitzungstokens (Schritt 7, Design D1):
/// ein lokaler Benutzer, kein Persistieren (ein lokaler Einzel-User-Host
/// muss nach einem Neustart nur neu einloggen). Thread-sicher (Lock);
/// ein Token sind 32 Zufallsbytes als Hex (256 Bit Entropie).
///
/// Schritt 9 erweitert die Zugangsdaten auf widerrufbare Gerätekopplung —
/// die Struktur (Issue/TryValidate/Revoke) bleibt die Grundlage dafür.
/// </summary>
public sealed class AuthTokenStore
{
    private sealed record TokenEntry(DateTimeOffset ExpiresAtUtc);

    private readonly object _gate = new();
    private readonly Dictionary<string, TokenEntry> _tokens = new(StringComparer.Ordinal);
    private readonly TimeSpan _defaultValidity;
    private readonly Func<DateTimeOffset> _now;

    /// <param name="defaultValidity">Gültigkeit neu ausgestellter Tokens (Default 12 h).</param>
    /// <param name="now">Zeitquelle (Default <see cref="DateTimeOffset.UtcNow"/>); in Tests steuerbar.</param>
    public AuthTokenStore(TimeSpan? defaultValidity = null, Func<DateTimeOffset>? now = null)
    {
        _defaultValidity = defaultValidity ?? TimeSpan.FromHours(12);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Gültigkeit neu ausgestellter Tokens in Minuten (für die Login-Antwort).</summary>
    public int DefaultValidityMinutes => (int)Math.Ceiling(_defaultValidity.TotalMinutes);

    /// <summary>Stellt ein neues Token aus; liefert Token und Ablaufzeitpunkt (UTC).</summary>
    public (string Token, DateTimeOffset ExpiresAtUtc) Issue(TimeSpan? validity = null)
    {
        var token = Convert.ToHexString(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var expiresAt = _now().Add(validity ?? _defaultValidity);
        lock (_gate)
        {
            _tokens[token] = new TokenEntry(expiresAt);
        }

        return (token, expiresAt);
    }

    /// <summary>
    /// Prüft Vorhandensein und Gültigkeit; abgelaufene Tokens werden
    /// entfernt (ein abgelaufenes Token ist nicht wieder lebbar).
    /// </summary>
    public bool TryValidate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var now = _now();
        lock (_gate)
        {
            if (!_tokens.TryGetValue(token, out var entry))
            {
                return false;
            }

            if (entry.ExpiresAtUtc <= now)
            {
                _tokens.Remove(token);
                return false;
            }

            return true;
        }
    }

    /// <summary>Entwertet ein Token (Logout; idempotent).</summary>
    public void Revoke(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        lock (_gate)
        {
            _tokens.Remove(token);
        }
    }
}
