namespace Lario.Infrastructure;

/// <summary>
/// Persistente Typen des Sync-Protokolls (Schritt 9, Batch A1).
///
/// Diese Entitäten sind reine Infrastruktur des Hosts (Gerätekopplung,
/// Protokollzustand, Änderungs- und Idempotenznachweise) — sie gehören
/// bewusst nicht in die Domain (Lario.Domain bleibt frei von
/// Sync-Begriffen) und werden direkt in <see cref="LarioDbContext"/>
/// gemappt.
///
/// Zeitstempel sind UTC-<see cref="DateTime"/> (technische
/// Protokollzeitpunkte, kein Fachdatum — das Fachdatum der Buchung liegt
/// im Expense-Payload).
/// </summary>

/// <summary>
/// Gekoppeltes Gerät (Schritt 9): ein Android-Gerät mit einem
/// widerrufbaren Gerätetoken. Nur der SHA-256-Hash des Tokens wird
/// gespeichert; das Klartext-Token erscheint genau einmal in der
/// Pair-Antwort.
/// </summary>
public class SyncDevice
{
    /// <summary>Stabile Geräte-Id (GUID, hostseitig erzeugt).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Anzeigename des Geräts (Client-Vorgabe beim Pairing).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256-Hash (Hex, klein) des Klartext-Tokens; eindeutig.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>null = aktiv; gesetzter Wert = widerrufener Zeitpunkt (UTC).</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>Letzter erfolgreicher Sync-Zugriff (UTC).</summary>
    public DateTime? LastUsedAtUtc { get; set; }
}

/// <summary>
/// Protokollzustand: genau eine Zeile (Id '1').
/// <see cref="DbGeneration"/> markiert die Datenbank-Generation (Restore
/// erzeugt eine neue Generation — Batch B); <see cref="ProtocolGeneration"/>
/// zählt Protokoll-Generationen; <see cref="CursorCounter"/> spiegelt den
/// höchsten bekannten <see cref="SyncChange"/>-Id (Cursor).
/// </summary>
public class SyncProtocolState
{
    public string Id { get; set; } = "1";

    public long DbGeneration { get; set; } = 1;

    public long ProtocolGeneration { get; set; } = 1;

    public long CursorCounter { get; set; } = 0;
}

/// <summary>
/// Änderungszeile des Host-Changelogs (Schritt 9): jede Änderung an
/// Stammdaten oder Buchungen erzeugt genau eine Zeile mit monoton
/// wachsender <see cref="Id"/> (INTEGER PRIMARY KEY = rowid,
/// AUTOINCREMENT — die Id ist zugleich der Cursorwert). Zeilen werden
/// nie gelöscht; die Zeilenreihenfolge ist die Änderungsreihenfolge.
/// </summary>
public class SyncChange
{
    /// <summary>Monoton wachsende Id (rowid/AUTOINCREMENT) — Cursorwert.</summary>
    public long Id { get; set; }

    /// <summary>Datenbank-Generation, in der die Änderung entstanden ist.</summary>
    public long DbGeneration { get; set; }

    /// <summary>
    /// Geänderte Entitätenart: <c>account|pool|category|taxCategory|booking</c>.
    /// </summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>Id der geänderten Entität.</summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>Neue Revisionszahl der Entität (Buchung: <c>baseRevision</c>).</summary>
    public int Revision { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}

/// <summary>
/// Idempotenznachweis einer empfangenen Geräteoperation (Schritt 9):
/// Primärschlüssel (DeviceId, OperationId) — dieselbe Operation desselben
/// Geräts wird höchstens einmal verarbeitet. <see cref="PayloadHash"/>
/// (SHA-256 über das Payload) macht Inhaltstausch unter gleicher
/// Operation-Id erkennbar (409 payload_mismatch).
/// </summary>
public class SyncOperation
{
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Gerätegenerierte, eindeutige Operation-Id (Client-Idempotenz).</summary>
    public string OperationId { get; set; } = string.Empty;

    /// <summary>Operationstyp (Version 1: <c>expense</c>).</summary>
    public string Type { get; set; } = string.Empty;

    public string PayloadHash { get; set; } = string.Empty;

    /// <summary>Kanonisches Original-Payload (JSON-Text, Nachvollziehbarkeit).</summary>
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>Id der entstandenen Buchung.</summary>
    public string BookingId { get; set; } = string.Empty;

    public DateTime AcceptedAtUtc { get; set; }
}
