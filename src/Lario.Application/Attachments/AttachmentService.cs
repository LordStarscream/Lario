using Lario.Domain;
using Lario.Domain.Attachments;
using Lario.Domain.Journal;

namespace Lario.Application.Attachments;

/// <summary>
/// Ergebnis des Start-Abgleichs (Design 7a D9b): gelöschte Waisen,
/// bereinigte Abbruch-Reste (.part) und Metadatenzeilen, deren Datei
/// fehlt (zulässig — Download liefert 410).
/// </summary>
public sealed record ReconcileResult(int OrphanedFilesDeleted, int PartFilesDeleted, int MetadataWithoutFile);

/// <summary>
/// Anwendungsfälle für Belege (Schritt 7a): Upload (mit Größen-,
/// Typ- und Referenzprüfung), Download, Zuordnung pro Buchung und
/// Löschen in der Reihenfolge Datei → Metadaten (Design D5).
///
/// Der Service kennt keine HTTP-Begriffe — Statusmappings (400/404/
/// 410/413) liegen in der API-Schicht (Batch B).
/// </summary>
public sealed class AttachmentService(
    IAttachmentRepository repository,
    IAttachmentFileStore fileStore,
    IBookingRepository bookingRepository)
{
    /// <summary>Zugelassene MIME-Typen (Design D3).</summary>
    public static readonly IReadOnlyList<string> AllowedContentTypes =
        ["application/pdf", "image/jpeg", "image/png"];

    /// <summary>
    /// Magic-Bytes je Typ (Design D3: der Inhalt muss zum deklarierten
    /// Typ passen; ein PDF mit .jpg-Endung wird abgelehnt).
    /// </summary>
    private static readonly (string Type, byte[] Magic)[] MagicSignatures =
    [
        ("application/pdf", [0x25, 0x50, 0x44, 0x46, 0x2D]), // %PDF-
        ("image/jpeg", [0xFF, 0xD8, 0xFF]),
        ("image/png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
    ];

    /// <summary>
    /// Upload-Flow (Design D3/D9b), Reihenfolge:
    /// (1) deklarierter Typ muss zulässig sein (sonst ist der Dateiname
    ///     nicht bestimmbar),
    /// (2) Größe: exakt gezählt beim Streamen, fail fast über dem Limit
    ///     (<see cref="AttachmentTooLargeException"/>),
    /// (3) Magic-Bytes müssen zum deklarierten Typ passen,
    /// (4) Buchungsbezug: Buchung vorhanden, Split (falls angegeben)
    ///     gehört zur Buchung — <see cref="InvalidReferenceException"/>.
    /// Die Datei liegt bei (4) bereits; bei Verwerfung wird sie sofort
    /// entfernt, zusätzlich räumt der Start-Abgleich Crash-Reste auf.
    /// Die Metadatenzeile wird zuletzt angelegt — danach ist die Datei
    /// zwingend da (D9b).
    /// </summary>
    public async Task<Attachment> UploadAsync(
        string originalFilename,
        string declaredContentType,
        Stream content,
        string bookingId,
        string? splitId,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        var contentType = NormalizeContentType(declaredContentType);
        var magic = MagicSignatures.FirstOrDefault(m => m.Type == contentType);
        if (magic.Magic is null)
            throw new UnsupportedAttachmentTypeException(
                declaredContentType ?? string.Empty, "Typ nicht in der erlaubten Liste (PDF, JPEG, PNG).");

        var extension = ExtensionFor(contentType);
        var id = Guid.NewGuid().ToString("N");
        var storedName = id + extension;

        var write = await fileStore.WriteAsync(storedName, content, maxBytes, cancellationToken);

        try
        {
            if (!StartsWith(write.HeaderBytes, magic.Magic))
                throw new UnsupportedAttachmentTypeException(
                    contentType, "Magic-Bytes der Datei passen nicht zum deklarierten Typ.");

            var booking = await bookingRepository.FindByIdAsync(bookingId, cancellationToken)
                ?? throw new InvalidReferenceException(
                    $"Unbekannte Buchung '{bookingId}' für Beleg-Zuordnung.");

            ExpenseSplit? split = null;
            if (splitId is not null)
                split = booking.Splits.FirstOrDefault(s => s.Id == splitId)
                    ?? throw new InvalidReferenceException(
                        $"Unbekannter Teilposten '{splitId}' der Buchung '{bookingId}'.");

            var attachment = new Attachment
            {
                Id = id,
                OriginalFilename = SanitizeFilename(originalFilename, storedName),
                ContentType = contentType,
                SizeBytes = write.SizeBytes,
                Sha256 = write.Sha256,
                StoredName = storedName,
                BookingId = bookingId,
                SplitId = split?.Id,
                CreatedAtUtc = DateTime.UtcNow,
            };
            await repository.AddAsync(attachment, cancellationToken);
            return attachment;
        }
        catch
        {
            // Design D3/D9b: nach dem Rename ist die finale Datei da —
            // bei jedem Fehler ohne DB-Zeile (Magic-Prüfung,
            // Referenzprüfung, DB-Commit) würde sie eine Waise bleiben.
            // Sofort entfernen; der Start-Abgleich ist nur die
            // Crash-Versicherung (D9b).
            await fileStore.DeleteAsync(storedName, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Lädt Metadaten und Datei; (null, null) wenn der Beleg unbekannt
    /// ist (404). Die Datei kann fehlen (Metadaten ohne Datei sind
    /// zulässig, Design D9b) — dann liefert
    /// <see cref="AttachmentFileContent.Missing"/> und die API antwortet
    /// 410.
    /// </summary>
    public async Task<(Attachment? Metadata, AttachmentFileContent? File)> DownloadAsync(
        string id, CancellationToken cancellationToken = default)
    {
        var metadata = await repository.FindByIdAsync(id, cancellationToken);
        if (metadata is null)
            return (null, null);
        var file = await fileStore.ReadAsync(metadata.StoredName, cancellationToken);
        return (metadata, file);
    }

    /// <summary>Alle Belege einer Buchung (Anzeige in der Buchungsliste, Design D6).</summary>
    public Task<IReadOnlyList<Attachment>> ListByBookingAsync(
        string bookingId, CancellationToken cancellationToken = default)
        => repository.GetByBookingAsync(bookingId, cancellationToken);

    /// <summary>
    /// Löscht Beleg (Design D5, festgelegte Reihenfolge): Metadaten-
    /// Zeile zuerst committen, dann Datei löschen (fehlende Datei ist
    /// Erfolg). Umgekehrte Reihenfolge würde bei einem Abbruch zwischen
    /// den beiden Schritten einen permanenten 410-Zustand erzeugen
    /// (Metadaten ohne Datei bleiben laut D9b erhalten); eine Waisendatei
    /// dagegen räumt der Start-Abgleich beim nächsten Start auf.
    /// Eine fehlgeschlagene Datei-Löschung hebt die bereits committete
    /// Zeilen-Löschung nicht auf (Design D9(e)): die Datei bleibt als
    /// Waise und wird beim nächsten Start-Abgleich (D2b) entfernt.
    /// Die Buchung bleibt unverändert. null wenn unbekannt (404).
    /// </summary>
    public async Task<Attachment?> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var attachment = await repository.FindByIdAsync(id, cancellationToken);
        if (attachment is null)
            return null;
        await repository.RemoveAsync(id, cancellationToken);
        try
        {
            await fileStore.DeleteAsync(attachment.StoredName, cancellationToken);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            // Design D5 / D9(e): Die Metadaten-Zeile ist dauerhaft weg.
            // Ein Dateilösch-Fehler (z. B. Dateisystem-Fehler) darf den
            // Löscherfolg nicht rückgängig machen — die Datei bleibt als
            // Waise und wird beim nächsten Start-Abgleich (D2b) entfernt,
            // dessen Ergebnis (OrphanedFilesDeleted) die Bereinigung
            // dokumentiert.
        }
        return attachment;
    }

    /// <summary>
    /// Start-Abgleich (Design D9b): finale Dateien ohne Metadatenzeile
    /// sind Waisen (gelöscht); offene .part-Dateien sind Abbruch-Reste
    /// (gelöscht — der Client lädt neu); Metadaten ohne Datei bleiben
    /// (Download → 410).
    /// </summary>
    public async Task<ReconcileResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var storedNames = await fileStore.ListStoredFilesAsync(cancellationToken);
        var metadata = await repository.GetAllAsync(cancellationToken);
        var knownNames = metadata.Select(m => m.StoredName).ToHashSet(StringComparer.Ordinal);

        var orphans = 0;
        var parts = 0;
        foreach (var name in storedNames)
        {
            if (name.EndsWith(".part", StringComparison.Ordinal))
            {
                await fileStore.DeleteAsync(name, cancellationToken);
                parts++;
            }
            else if (!knownNames.Contains(name))
            {
                await fileStore.DeleteAsync(name, cancellationToken);
                orphans++;
            }
        }

        var metadataWithoutFile = metadata.Count(m =>
            !storedNames.Contains(m.StoredName, StringComparer.Ordinal)
            && !storedNames.Contains(m.StoredName + ".part", StringComparer.Ordinal));

        return new ReconcileResult(orphans, parts, metadataWithoutFile);
    }

    private static bool StartsWith(byte[] header, byte[] magic)
    {
        if (header.Length < magic.Length)
            return false;
        for (var i = 0; i < magic.Length; i++)
            if (header[i] != magic[i])
                return false;
        return true;
    }

    private static string NormalizeContentType(string? declared)
    {
        if (string.IsNullOrWhiteSpace(declared))
            return string.Empty;
        var type = declared.Trim();
        var parameterIndex = type.IndexOf(';');
        if (parameterIndex >= 0)
            type = type[..parameterIndex].Trim();
        return type.ToLowerInvariant();
    }

    private static string ExtensionFor(string contentType) => contentType switch
    {
        "application/pdf" => ".pdf",
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        _ => ".bin",
    };

    private static string SanitizeFilename(string original, string storedName)
    {
        // Reine Anzeige (Design D2): aus dem Namen wird nie ein Pfad
        // abgeleitet. Leere Eingaben fallen auf den Dateinamen zurück;
        // die Länge hält sich an die Spaltengrenze.
        var name = original.Trim();
        if (name.Length == 0)
            return storedName;
        return name.Length <= 255 ? name : name[^255..];
    }
}
