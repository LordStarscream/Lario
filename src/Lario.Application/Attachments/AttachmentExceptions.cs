namespace Lario.Application.Attachments;

/// <summary>
/// Deklarierter oder erkannter Dateityp ist nicht zulässig
/// (Design 7a D3: nur PDF, JPEG, PNG). Die API antwortet mit
/// 400 <c>validation_error</c> und Detailcode
/// <c>unsupported_attachment_type</c>.
/// </summary>
public sealed class UnsupportedAttachmentTypeException : Exception
{
    public UnsupportedAttachmentTypeException(string declaredContentType, string reason)
        : base($"Unzulässiger Dateityp '{declaredContentType}': {reason}")
    {
        DeclaredContentType = declaredContentType;
        Reason = reason;
    }

    /// <summary>Deklarierter MIME-Typ (oder der erkannte Typ bei Magic-Mismatch).</summary>
    public string DeclaredContentType { get; }

    /// <summary>Präziser Grund (z. B. Magic-Bytes passen nicht zum deklarierten Typ).</summary>
    public string Reason { get; }
}

/// <summary>
/// Die Datei überschreitet die maximale Größe
/// (Design 7a D3: 413 <c>validation_error</c> mit Detailcode
/// <c>attachment_too_large</c>).
/// </summary>
public sealed class AttachmentTooLargeException : Exception
{
    public AttachmentTooLargeException(long maxBytes, long sizeBytes)
        : base($"Datei überschreitet die maximale Größe von {maxBytes} Byte (erkannt: mindestens {sizeBytes} Byte).")
    {
        MaxBytes = maxBytes;
        SizeBytes = sizeBytes;
    }

    /// <summary>Konfiguriertes Maximum in Byte.</summary>
    public long MaxBytes { get; }

    /// <summary>Größe zum Zeitpunkt der Erkennung (fail fast: Maximum + 1).</summary>
    public long SizeBytes { get; }
}
