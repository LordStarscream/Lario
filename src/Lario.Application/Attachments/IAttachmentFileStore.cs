namespace Lario.Application.Attachments;

/// <summary>
/// Ergebnis eines erfolgreichen Datei-Uploads (Design 7a D9b):
/// exakte Größe, SHA-256 (hex) und die ersten Bytes der Datei
/// (für die Magic-Byte-Prüfung) — alles aus dem geprüften Inhalt,
/// nicht aus Client-Metadaten.
/// </summary>
public sealed record AttachmentWriteResult(long SizeBytes, string Sha256, byte[] HeaderBytes);

/// <summary>
/// Ergebnis eines Datei-Lesezugriffs: vorhanden (Stream + Größe) oder
/// fehlt auf dem Datenträger (410-Semantik, Design 7a D4). Der Stream
/// muss vom Aufrufer geschlossen werden.
/// </summary>
public sealed class AttachmentFileContent
{
    internal AttachmentFileContent(bool exists, Stream? content, long sizeBytes)
    {
        Exists = exists;
        Content = content;
        SizeBytes = sizeBytes;
    }

    public bool Exists { get; }
    public Stream? Content { get; }
    public long SizeBytes { get; }

    public static AttachmentFileContent Missing() => new(false, null, 0);
    public static AttachmentFileContent Of(Stream content, long sizeBytes) => new(true, content, sizeBytes);
}

/// <summary>
/// Speichert, liest und löscht die Dateien hinter den Beleg-Metadaten
/// (Design 7a D2: serverseitige Namen, Ablage außerhalb der öffentlichen
/// Webressourcen). Implementierung: <c>SqliteAttachmentFileStore</c>.
///
/// Alle Methoden adressieren Dateien über den serverseitig erzeugten
/// <c>storedName</c> (Metadatenzeile), niemals über Client-Eingaben.
/// </summary>
public interface IAttachmentFileStore
{
    /// <summary>
    /// Streamt den Inhalt unter dem Teilnamen <c>{storedName}.part</c>
    /// ab, zählt die Byte exakt und wirft
    /// <see cref="AttachmentTooLargeException"/>, sobald
    /// <paramref name="maxBytes"/> überschritten ist (fail fast — die
    /// Teil-Datei wird entfernt). Danach Fsync (Inhalt liegt auf
    /// Platte), SHA-256-Berechnung über den kompletten Inhalt und
    /// atomares Umbenennen auf <paramref name="storedName"/>. Bei jedem
    /// Fehler wird die Teil-Datei entfernt — keine Waisen
    /// (Design D9b: erst Metadaten, danach ist die Datei da).
    /// </summary>
    Task<AttachmentWriteResult> WriteAsync(
        string storedName,
        Stream content,
        long maxBytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lädt die gespeicherte Datei; fehlt sie auf dem Datenträger,
    /// <see cref="AttachmentFileContent.Missing"/> (keine Ausnahme —
    /// das Service entscheidet über die 410-Antwort).
    /// </summary>
    Task<AttachmentFileContent> ReadAsync(
        string storedName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Löscht die gespeicherte Datei; eine bereits fehlende Datei ist
    /// Erfolg (Design 7a D5: Metadaten zuerst, dann Datei — eine
    /// fehlende Datei darf den Abbruch nicht erzwingen).
    /// </summary>
    Task DeleteAsync(string storedName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Alle Dateinamen im Ablageverzeichnis nach dem Lario-
    /// Namensschema: finale Namen (ohne Punkt in der Endung) und offene
    /// <c>.part</c>-Dateien (Design D9b — der Start-Abgleich muss beide
    /// kennen). Nicht-Lario-Dateien werden nicht gelistet.
    /// </summary>
    Task<IReadOnlyList<string>> ListStoredFilesAsync(CancellationToken cancellationToken = default);
}
