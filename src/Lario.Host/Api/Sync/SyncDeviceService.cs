using System.Security.Cryptography;
using System.Text;
using Lario.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Lario.Host.Api.Sync;

/// <summary>
/// Gerätekopplung des Sync-Protokolls (Schritt 9, Batch A1):
/// Pairing erzeugt ein Gerätetoken (32 Zufallsbytes als Hex, wie die
/// Sitzungstokens), dessen SHA-256-Hash (klein, über UTF-8) persistiert
/// wird. Das Klartext-Token erscheint genau einmal — in der Pair-Antwort.
/// Geräte sind widerrufbar (RevokedAtUtc); widerrufene Tokens werden
/// nicht mehr akzeptiert.
/// </summary>
public sealed class SyncDeviceService(LarioDbContext db)
{
    private const string TokenHashAlgorithm = "SHA256";

    /// <summary>
    /// Koppelt ein Gerät: erzeugt Id (GUID), Token (256 Bit Zufall als
    /// Hex) und speichert den Token-Hash. Liefert das Klartext-Token —
    /// es wird danach nirgends wieder verfügbar sein.
    /// </summary>
    public async Task<(string DeviceId, string Name, string Token, DateTime CreatedAtUtc)> PairAsync(
        string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var token = NewToken();
        var device = new SyncDevice
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            TokenHash = HashToken(token),
            CreatedAtUtc = DateTime.UtcNow,
            RevokedAtUtc = null,
            LastUsedAtUtc = null
        };

        db.SyncDevices.Add(device);
        await db.SaveChangesAsync(cancellationToken);
        return (device.Id, device.Name, token, device.CreatedAtUtc);
    }

    /// <summary>Listet alle Geräte (inkl. widerrufener) in Kopplungsreihenfolge.</summary>
    public async Task<IReadOnlyList<SyncDevice>> ListAsync(CancellationToken cancellationToken = default)
        => await db.SyncDevices.AsNoTracking()
            .OrderBy(d => d.CreatedAtUtc)
            .ThenBy(d => d.Id)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Widerruft ein Gerät (idempotent: bereits widerrufene Geräte bleiben
    /// widerrufen). Ein Widerruf ist endgültig — das Token ist unwiderrufbar
    /// ungültig, die Zeile bleibt der Nachvollziehbarkeit erhalten.
    /// </summary>
    public async Task RevokeAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var device = await db.SyncDevices.FirstOrDefaultAsync(
            d => d.Id == deviceId, cancellationToken)
            ?? throw new Lario.Domain.LarioDomainException(
                $"Unbekanntes Gerät '{deviceId}'.");

        if (device.RevokedAtUtc is null)
        {
            device.RevokedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Validiert ein Gerätetoken: liefert das Gerät nur, wenn der Hash
    /// übereinstimmt und das Gerät aktiv ist (RevokedAtUtc null).
    /// </summary>
    public async Task<SyncDevice?> TryValidateDeviceTokenAsync(
        string? token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var hash = HashToken(token);
        return await db.SyncDevices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.TokenHash == hash && d.RevokedAtUtc == null,
                cancellationToken);
    }

    /// <summary>
    /// Prüft, ob ein Token zu einem (aktiven oder widerrufenen) Gerät
    /// gehört — für die Auth-Trennung (Gerätetoken an
    /// Session-Endpunkten ist eindeutig erkennbar).
    /// </summary>
    public async Task<bool> IsDeviceTokenAsync(
        string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var hash = HashToken(token);
        return await db.SyncDevices.AsNoTracking()
            .AnyAsync(d => d.TokenHash == hash, cancellationToken);
    }

    /// <summary>Zeichnet einen erfolgreichen Sync-Zugriff (UTC) ein.</summary>
    public async Task TouchLastUsedAsync(
        string deviceId, CancellationToken cancellationToken = default)
    {
        var device = await db.SyncDevices.FirstOrDefaultAsync(
            d => d.Id == deviceId, cancellationToken);
        if (device is null)
        {
            return;
        }

        device.LastUsedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string NewToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();
}
