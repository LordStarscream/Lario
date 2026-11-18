using System.Text.Json.Nodes;
using Lario.Domain.Journal;
using Lario.Host.Api.MasterData;

namespace Lario.Host.Api.Sync;

/// <summary>
/// DTOs der Sync-Endpunkte (Schritt 9, Design D4/D5/D6).
///
/// Wire-Vertrag:
/// - Push-Envelope (D5): <c>{operationId, type, payload, payloadHash,
///   createdAtUtc}</c>; <c>payload</c> ist das eingebettete
///   <see cref="ExpenseCommand"/> (dieselbe Shape wie das lokale
///   Expense-Command der mobilen Outbox, Design B3). <see cref="SyncPushRequest.Payload"/>
///   wird bewusst als <see cref="JsonNode"/> gehalten, damit der Host den
///   kanonischen Payload-Hash <b>serialisierungsunabhängig</b> berechnen
///   kann: Raw-JSON → Schlüssel sortiert, camelCase, kein Whitespace →
///   SHA-256 (64 hex, kein Präfix) — byte-identisch zum Client-Hash (B1).
///   Gleiche logische Inhalte → gleicher Hash → idempotent; gleiche
///   OperationId mit anderem Inhalt → 409 <c>payload_mismatch</c>.
/// - Push-Antwort (D5/D7): <c>{status, bookingId, protocolGeneration,
///   revision, ack}</c>; <c>ack: true</c> ist die einzige Quelle, mit der
///   die mobile Outbox-Zeile <c>confirmed</c> wird (D7: kein
///   Auto-Bestätigen nach Timeout).
/// - Bootstrap (D4): flache Antwort <c>{dbGeneration, protocolGeneration,
///   cursor, accounts, pools, categories, taxCategories}</c>; die
///   Stammdaten-DTOs kommen aus <see cref="MasterData"/> — exakt dieselben
///   Shapes wie die GET-Endpunkte /api/v1/{accounts,...} (Vergleichstest
///   <c>Bootstrap_und_GetAccounts_liefern_dieselben_Daten</c>).
/// - Pull (D6): <c>?cursor=N&amp;limit=</c> → <c>{cursor, complete,
///   truncated, changes:[{id, entityType, entityId, revision}]}</c>;
///   <c>cursor</c> (Request) = Id der letzten übernommenen Zeile (0 =
///   alles); der <c>cursor</c> der Antwort = größtes Changelog-Id desselben
///   konsistenten Lesezeitpunkts (D6: N' = max Id in der Read-Transaktion)
///   — der Client paginiert über das Id der letzten erhaltenen Änderung
///   und prüft <c>complete</c>, bevor er den Cursor fortschreibt.
/// </summary>

// ---------- Gerät-Kopplung (Design D1) ----------

/// <summary>Anfrage von <c>POST /devices/pair</c> (Sitzungstoken).</summary>
public sealed record PairDeviceRequest(string? Name);

/// <summary>
/// Gerät (<c>GET /devices</c>, <c>POST /devices/pair</c>). Beim Pairing
/// enthält <see cref="Token"/> das Klartext-Gerätetoken — es wird genau
/// einmal zurückgegeben (persistiert wird nur der SHA-256-Hash); die
/// Geräte-Liste liefert <c>Token: ""</c>.
/// </summary>
public sealed record DeviceDto(
    string Id,
    string Name,
    string Token,
    DateTime CreatedAtUtc,
    DateTime? RevokedAtUtc,
    DateTime? LastUsedAtUtc);

// ---------- Push (Design D5) ----------

/// <summary>
/// Push-Envelope einer Outbox-Operation (D5). <see cref="Type"/> ist die
/// Operationstyp-Whitelist; derzeit nur <c>"expense"</c>. <see cref="Payload"/>
/// ist das Raw-JSON des <see cref="ExpenseCommand"/> (für den kanonischen
/// Hash, siehe Klassendoc). <see cref="PayloadHash"/> ist der
/// Geräte-Nachweis (B1: SHA-256 über den kanonischen Payload, 64 hex).
/// </summary>
public sealed record SyncPushRequest(
    string? OperationId,
    string? Type,
    JsonNode? Payload,
    string? PayloadHash,
    string? CreatedAtUtc);

/// <summary>
/// Push-Antwort (D5/D7): <c>status</c> = <c>"accepted"</c> (erste
/// Verarbeitung), <c>"already_processed"</c> (wiederholte Operation mit
/// gleichem Inhalt) — <see cref="Ack"/> ist in beiden Fällen true.
/// <c>protocolGeneration</c> ist die im Pushzeitpunkt gültige
/// Protokollgeneration (D6: 1, solange kein Restore/Reset stattgefunden
/// hat).
/// </summary>
public sealed record SyncPushResponse(
    string Status,
    string BookingId,
    long ProtocolGeneration,
    int Revision,
    bool Ack);

/// <summary>Operationstyp-Whitelist des Push-Endpunkts (D5).</summary>
public static class SyncOperationType
{
    public const string Expense = "expense";
}

// ---------- Bootstrap (Design D4) ----------

/// <summary>
/// Bootstrap-Antwort (D4): Generationen, aktueller Changelog-Cursor und
/// der vollständige Fachstammdatenstand (flaches Format — keine
/// verschachtelte <c>snapshot</c>-Gruppe). <see cref="Cursor"/> deckt alle
/// Changelog-Zeilen ab, die der Snapshot bereits enthält (D4: kein
/// Doppel-Lieferen).
/// </summary>
public sealed record BootstrapResponseDto(
    long DbGeneration,
    long ProtocolGeneration,
    long Cursor,
    IReadOnlyList<AccountDto> Accounts,
    IReadOnlyList<PoolDto> Pools,
    IReadOnlyList<CategoryDto> Categories,
    IReadOnlyList<TaxCategoryDto> TaxCategories);

// ---------- Pull (Design D6) ----------

/// <summary>Eine Changelog-Zeile: was sich geändert hat (wer wird nicht übertragen).</summary>
public sealed record SyncChangeDto(
    long Id,
    string EntityType,
    string EntityId,
    int Revision);

/// <summary>
/// Pull-Antwort (D6). <see cref="Cursor"/> = größtes Changelog-Id
/// desselben konsistenten Lesezeitpunkts (N' — nicht das letzte Id der
/// Seite). <see cref="Complete"/> = alle Änderungen ab dem angefragten
/// Cursor sind in dieser Antwort enthalten (nur dann darf der mobile
/// Cursor fortschreiben); <see cref="Truncated"/> = die Seite ist auf
/// <c>limit</c> gekürzt, nächste Seite mit <c>cursor</c> = Id der letzten
/// hier erhaltenen Änderung.
/// </summary>
public sealed record SyncPullResponse(
    long Cursor,
    bool Complete,
    bool Truncated,
    IReadOnlyList<SyncChangeDto> Changes);
