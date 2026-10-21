using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Lario.Application.Attachments;

namespace Lario.Infrastructure;

/// <summary>
/// Dateiablage für Belege (Schritt 7a, Design D2): ein Verzeichnis auf
/// dem lokalen Datenträger, außerhalb der öffentlichen Webressourcen
/// (der Host hängt das Verzeichnis bewusst nicht unter wwwroot).
///
/// Sicherheit:
/// <see cref="Attachment.StoredName"/>(s. Domain) ist serverseitig
/// erzeugt (<c>{32 hex}{.pdf|.jpg|.png}</c>). Jede öffentliche Methode
/// prüft den Namen gegen genau dieses Schema — Client-Eingaben können
/// so keinen Pfad abseits des Ablageverzeichnisses adressieren
/// (keine Path-Traversal, Design D2).
///
/// Schreibweise (Design D9b): erst unter <c>{name}.part</c> streamen
/// (Größe exakt gezählt, Fail-Fast über dem Limit), Fsync, SHA-256 und
/// Header aus dem Platteninhalt lesen, dann atomares Umbenennen. Bei
/// jedem Fehler wird die Teil-Datei entfernt — es bleiben keine
/// Waisen, und eine fertige Datei entsteht nie unvollständig.
/// </summary>
public sealed class SqliteAttachmentFileStore : IAttachmentFileStore
{
    /// <summary>Vollständiges Lario-Dateiname-Schema (finale Namen).</summary>
    private static readonly Regex FinalNameRegex =
        new(@"^[0-9a-f]{32}\.(pdf|jpg|png)$", RegexOptions.Compiled);

    /// <summary>Wie finale Namen, zusätzlich mit offener <c>.part</c>-Endung.</summary>
    private static readonly Regex DeletableNameRegex =
        new(@"^[0-9a-f]{32}\.(pdf|jpg|png)(\.part)?$", RegexOptions.Compiled);

    /// <summary>Anzahl der Header-Bytes, die für Magic-Byte-Prüfung gelesen werden.</summary>
    private const int HeaderBytes = 16;

    private readonly string _baseDirectory;

    public SqliteAttachmentFileStore(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ArgumentException("Ablageverzeichnis darf nicht leer sein.", nameof(baseDirectory));
        _baseDirectory = baseDirectory;
    }

    /// <summary>Ablageverzeichnis (für Tests und den Start-Abgleich).</summary>
    public string BaseDirectory => _baseDirectory;

    public async Task<AttachmentWriteResult> WriteAsync(
        string storedName,
        Stream content,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        EnsureSafeName(storedName);
        Directory.CreateDirectory(_baseDirectory);
        var partPath = Path.Combine(_baseDirectory, storedName + ".part");
        var finalPath = Path.Combine(_baseDirectory, storedName);
        try
        {
            long total = 0;
            var buffer = new byte[81_920];
            await using (var part = new FileStream(
                partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81_920, useAsync: true))
            {
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        // Fail fast: Teil-Datei entfernen und melden —
                        // der Rest des Streams wird nicht gelesen.
                        await part.DisposeAsync();
                        TryDelete(partPath);
                        throw new AttachmentTooLargeException(maxBytes, total);
                    }
                    await part.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await part.FlushAsync(cancellationToken);
                part.Flush(flushToDisk: true);
            }

            // SHA-256 und Header aus dem tatsächlich gespeicherten Inhalt
            // (Platte) berechnen — nie aus dem Eingabestream.
            (string sha256, byte[] header) = await HashAndReadHeaderAsync(partPath, cancellationToken);

            // Atomares Umbenennen (gleicher Datenträger). overwrite: false —
            // ein gleichnamiges Ziel ist ein Invariantenbruch (GUID-Namen)
            // und darf nicht still übergangen werden.
            File.Move(partPath, finalPath, overwrite: false);
            return new AttachmentWriteResult(total, sha256, header);
        }
        catch
        {
            // Bei jedem Fehler (Größe, I/O, Abbruch) keine Waise:
            TryDelete(partPath);
            throw;
        }
    }

    public Task<AttachmentFileContent> ReadAsync(
        string storedName,
        CancellationToken cancellationToken = default)
    {
        EnsureSafeName(storedName);
        var path = Path.Combine(_baseDirectory, storedName);
        if (!File.Exists(path))
            return Task.FromResult(AttachmentFileContent.Missing());
        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, useAsync: true);
        return Task.FromResult(
            AttachmentFileContent.Of(stream, new FileInfo(path).Length));
    }

    public Task DeleteAsync(string storedName, CancellationToken cancellationToken = default)
    {
        // Finale Namen und offene .part-Dateien (Abbruch-Reste, die der
        // Start-Abgleich beseitigt) — das strenge Schema bleibt erhalten.
        if (!DeletableNameRegex.IsMatch(storedName))
            throw new ArgumentException(
                "Ungültiger Beleg-Dateiname (erwartet: 32 hex + .pdf/.jpg/.png, optional .part): " + storedName,
                nameof(storedName));
        var path = Path.Combine(_baseDirectory, storedName);
        TryDelete(path);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListStoredFilesAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_baseDirectory);
        var pattern = new Regex(@"^[0-9a-f]{32}\.(pdf|jpg|png)(\.part)?$", RegexOptions.Compiled);
        var names = Directory
            .EnumerateFiles(_baseDirectory)
            .Select(Path.GetFileName)
            .Where(name => name is not null && pattern.IsMatch(name))
            .Select(name => name!)
            .OrderBy(name => name)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(names);
    }

    private async Task<(string Sha256, byte[] Header)> HashAndReadHeaderAsync(
        string path, CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        var header = new byte[HeaderBytes];
        var headerCount = 0;
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, useAsync: true);
        var buffer = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (headerCount < header.Length)
            {
                var n = Math.Min(read, header.Length - headerCount);
                Array.Copy(buffer, 0, header, headerCount, n);
                headerCount += n;
            }
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var result = header.AsSpan(0, headerCount).ToArray();
        return (Convert.ToHexString(sha.Hash!).ToLowerInvariant(), result);
    }

    private static void EnsureSafeName(string storedName)
    {
        if (!FinalNameRegex.IsMatch(storedName))
            throw new ArgumentException(
                "Ungültiger Beleg-Dateiname (erwartet: 32 hex + .pdf/.jpg/.png): " + storedName,
                nameof(storedName));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException)
        {
            // Fehlende Datei ist der erwartete Zustand (Delete-Semantik);
            // Zugriffsfehler beim Aufräumen ignorieren — der Hauptfehler
            // bleibt bestehen.
        }
    }
}
