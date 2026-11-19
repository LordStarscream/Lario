using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Lario.Application;
using Lario.Application.Journal;
using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;
using Lario.Infrastructure;
using Lario.Host.Api.Journal;
using Lario.Host.Api.MasterData;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lario.Host.Api.Sync;

/// <summary>
/// Fachlogik der Sync-Endpunkte (Schritt 9, Design D4/D5/D6):
/// <list type="bullet">
/// <item><b>Bootstrap</b> (D4): atomare Konsistenzgarantie —
/// Generationen, Cursor und Stammdaten-Snapshot werden in einer kurzen
/// <b>Read-Transaktion</b> gelesen (kein Restore/Reset kann zwischen den
/// Feldern greifen); danach Rollback (nur Lesezugriffe).</item>
/// <item><b>Push</b> (D5): Fachbuchung, SyncOperation-Zeile und
/// Changelog-Eintrag laufen in <b>einer Transaktion</b>. Idempotenz in
/// zwei Stufen: (1) Operation-Id-Nachweis (Gerät, OperationId) mit
/// gleichem kanonischen Payload-Hash → <c>already_processed</c> (neue
/// Buchung, keine zweite Changelog-Zeile); (2) derselbe Inhalt ohne
/// Operation-Zeile (Crash zwischen Fachbuchung und Nachweis) →
/// Fachbuchung auf Basisrevision <c>0</c> → <c>already_processed</c>
/// (neue Zeile, Nachweis wird nachgetragen). Gleiche OperationId mit
/// anderem Inhalt → 409 <c>payload_mismatch</c>. Der Unique Constraint
/// (Gerät, OperationId) fängt parallele Duplikate auf: der Verlierer liest
/// den Gewinner-Nachweis und antwortet idempotent.</item>
/// <item><b>Changelog-Pull</b> (D6): Seitensuche über
/// <c>SyncChanges</c> (Id &gt; Cursor); <c>complete</c>/<c>truncated</c>
/// sagen dem Gerät, ob es seinen Cursor fortschreiben darf.</item>
/// </list>
/// </summary>
public sealed class SyncService(
    LarioDbContext db,
    IMasterDataRepository masterData,
    IBookingRepository bookings,
    ExpenseService expenseService,
    SyncDeviceService devices)
{
    /// <summary>
    /// SQLITE_CONSTRAINT_UNIQUE als erweiterter Fehlercode
    /// (Microsoft.Data.Sqlite 10.0.12, empirisch verifiziert — siehe
    /// <c>SqliteConstraintErrors</c> in Lario.Infrastructure).
    /// </summary>
    private const int UniqueConstraintExtendedCode = 2067;

    /// <summary>Standard-Seitengröße des Changelog-Pulls (D6).</summary>
    public const int DefaultPullLimit = 500;

    /// <summary>Maximale Seitengröße des Changelog-Pulls (D6).</summary>
    public const int MaxPullLimit = 5000;

    /// <summary>Ergebnis eines Push: Status für die Antwort + Konfliktmeldung.</summary>
    public sealed record PushResult(
        string Status,
        string BookingId,
        long ProtocolGeneration,
        int Revision,
        bool Ack,
        string? ConflictMessage = null);

    // ---------- Bootstrap (Design D4) ----------

    /// <summary>
    /// Vollständiger Fachstammdatenstand mit Generationen und Cursor (D4).
    /// Read-Transaktion: <c>dbGeneration</c>, <c>protocolGeneration</c>,
    /// <c>cursor</c> und der Snapshot stammen aus demselben konsistenten
    /// Moment (kurze Transaktion — kein Restore/Reset kann zwischen den
    /// Feldern greifen).
    /// </summary>
    public async Task<BootstrapResponseDto> BootstrapAsync(string deviceId, CancellationToken ct)
    {
        BootstrapResponseDto response;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var state = await db.SyncProtocolStates.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == "1", ct);
            var catalog = await masterData.LoadCatalogAsync(ct);
            response = new BootstrapResponseDto(
                state?.DbGeneration ?? 1,
                state?.ProtocolGeneration ?? 1,
                await db.SyncChanges.AsNoTracking().MaxAsync(c => (long?)c.Id, ct) ?? 0,
                catalog.Accounts.Select(MasterDataDtoMapper.ToDto).ToList(),
                catalog.Pools.Select(MasterDataDtoMapper.ToDto).ToList(),
                catalog.Categories.Select(c => MasterDataDtoMapper.ToDto(c, catalog)).ToList(),
                catalog.TaxCategories.Select(MasterDataDtoMapper.ToDto).ToList());
        }
        finally
        {
            await transaction.RollbackAsync(ct);
        }

        await devices.TouchLastUsedAsync(deviceId, ct);
        return response;
    }

    // ---------- Push (Design D5) ----------

    /// <summary>
    /// Verarbeitet eine gepushte Geräteoperation (D5). Siehe Klassendoc zu
    /// Transaktion und den beiden Idempotenz-Stufen.
    /// </summary>
    public async Task<PushResult> PushAsync(string deviceId, SyncPushRequest envelope, CancellationToken ct)
    {
        ValidateEnvelope(envelope);
        var canonicalHash = CanonicalPayloadHash(envelope.Payload);

        var protocolState = await db.SyncProtocolStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == "1", ct);
        var protocolGeneration = protocolState?.ProtocolGeneration ?? 1;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Stufe 1: Operation-Nachweis existiert → idempotent oder Konflikt.
            var existing = await db.SyncOperations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.DeviceId == deviceId
                    && o.OperationId == envelope.OperationId, ct);
            if (existing is not null)
            {
                if (existing.PayloadHash == canonicalHash)
                {
                    var booking = await bookings.FindByIdAsync(existing.BookingId, ct);
                    await transaction.CommitAsync(ct);
                    await devices.TouchLastUsedAsync(deviceId, ct);
                    return new PushResult(
                        "already_processed", existing.BookingId, protocolGeneration,
                        booking?.BaseRevision ?? 0, true);
                }

                await transaction.RollbackAsync(ct);
                return new PushResult(
                    "payload_mismatch", existing.BookingId, protocolGeneration, 0, false,
                    $"OperationId '{envelope.OperationId}' wurde bereits mit anderem Inhalt verarbeitet.");
            }

            // Neue Operation: Fachbuchung + Nachweis + Changelog in einer
            // Transaktion. Stufe 2 (identischer Inhalt ohne Nachweis) wird
            // von ExpenseService über ExpectedBaseRevision 0 abgebildet.
            var result = await expenseService.CreateAsync(ToExpenseSpec(envelope.Payload), ct);

            db.SyncOperations.Add(new SyncOperation
            {
                DeviceId = deviceId,
                OperationId = envelope.OperationId!, // durch ValidateEnvelope garantiert
                Type = SyncOperationType.Expense,
                PayloadHash = canonicalHash,
                PayloadJson = CanonicalJson(envelope.Payload),
                BookingId = result.Booking.Id,
                AcceptedAtUtc = DateTime.UtcNow
            });

            // Changelog (D6): die Buchungs-Zeile legt der Kontext-Override
            // (LarioDbContext.CollectSyncChanges) in derselben Transaktion
            // an, in der die Buchung persistiert wird (BookingRepository.
            // AddAsync, oben in expenseService.CreateAsync). Wiederholungen
            // (Stufe 1/2) ändern keine Buchungszeile und erzeugen deshalb
            // keine Zeile. Der Cursor-Counter wird vom Kontext mitgezählt.
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            await devices.TouchLastUsedAsync(deviceId, ct);
            return new PushResult(
                result.Created ? "accepted" : "already_processed",
                result.Booking.Id, protocolGeneration, result.Booking.BaseRevision, true);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is SqliteException sql
                 && sql.SqliteExtendedErrorCode == UniqueConstraintExtendedCode)
        {
            // Paralleles Duplikat: der Unique Constraint (Gerät, OperationId)
            // hat einen gleichzeitigen Push durchlassen. Rollback, dann
            // Gewinner-Nachweis lesen und idempotent antworten.
            // (EF Core kapselt SQLite-Constraint-Verletzungen in
            // DbUpdateException — gleiches Muster wie
            // SqliteConstraintErrors.IsForeignKeyViolation; der Code ist
            // dort empirisch für Microsoft.Data.Sqlite 10.0.12 verifiziert.)
            await transaction.RollbackAsync(ct);
            var winner = await db.SyncOperations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.DeviceId == deviceId
                    && o.OperationId == envelope.OperationId, ct);
            if (winner is null || winner.PayloadHash != canonicalHash)
            {
                throw;
            }

            var booking = await bookings.FindByIdAsync(winner.BookingId, ct);
            await devices.TouchLastUsedAsync(deviceId, ct);
            return new PushResult(
                "already_processed", winner.BookingId, protocolGeneration,
                booking?.BaseRevision ?? 0, true);
        }
    }

    /// <summary>Envelope-Validierung vor der Transaktion (400-Fehlerkanal).</summary>
    private static void ValidateEnvelope(SyncPushRequest envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.OperationId))
        {
            throw new LarioDomainException("Die Push-Operation benötigt eine OperationId.");
        }

        if (envelope.Type != SyncOperationType.Expense)
        {
            throw new LarioDomainException($"Unbekannter Operationstyp '{envelope.Type ?? "<fehlend>"}' (zulässig: expense).");
        }

        if (envelope.Payload is null)
        {
            throw new LarioDomainException("Das Push-Payload fehlt.");
        }

        if (string.IsNullOrWhiteSpace(envelope.PayloadHash))
        {
            throw new LarioDomainException("Das Push-Payload benötigt den Geräte-Nachweis payloadHash.");
        }

        if (string.IsNullOrWhiteSpace(envelope.CreatedAtUtc))
        {
            throw new LarioDomainException("Die Push-Operation benötigt createdAtUtc.");
        }
        else if (!DateTime.TryParse(envelope.CreatedAtUtc, out _))
        {
            throw new LarioDomainException("createdAtUtc ist kein gültiges Datum.");
        }
    }

    /// <summary>
    /// Mappt das sync-eigene Payload (Roh-JSON, <see cref="JsonNode"/>) auf
    /// die identischen <see cref="ExpenseCommand"/>-Konzeptdaten und wendet
    /// die gleiche fachliche Validierung wie <c>POST /api/v1/expenses</c> an
    /// (Schemafehler im Roh-JSON → 400, keine Persistenz).
    /// </summary>
    internal static ExpenseSpec ToExpenseSpec(JsonNode? payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        ExpenseCommand? command;
        try
        {
            // Web-Default (camelCase + case-insensitiv): das Geräte-Payload
            // kommt über den generierten Client im selben JSON-Dialekt wie
            // die direkten Journal-Endpunkte. BCL-Default wäre
            // case-sensitiv und würde 'date'/'totalAmount' & Co. nicht
            // auf die PascalCase-Parameter mappen.
            command = System.Text.Json.JsonSerializer.Deserialize<ExpenseCommand>(
                payload.ToJsonString(), ExpensePayloadOptions);
            if (command is null)
            {
                throw new System.Text.Json.JsonException("Payload ist kein Objekt.");
            }
        }
        catch (System.Text.Json.JsonException)
        {
            throw new LarioDomainException(
                "Das Push-Payload ist kein gültiges Expense-Objekt (Schemafehler).");
        }

        return new ExpenseSpec
        {
            Id = command.Id,
            Date = JournalWriteEndpoints.ParseDate(command.Date, "date"),
            TotalAmount = new Money(command.TotalAmount),
            AccountId = command.AccountId,
            Note = command.Note,
            Splits = JournalWriteEndpoints.RequiredList(command.Splits, "splits")
                .Select(JournalWriteEndpoints.MapSplitSpec).ToList(),
            ExpectedBaseRevision = command.ExpectedBaseRevision,
        };
    }

    // ---------- Changelog-Pull (Design D6) ----------

    /// <summary>
    /// Changelog-Zeilen ab <c>cursor</c> (Id &gt; cursor), max. <c>limit</c>
    /// (Default 500, max. 5000). <c>complete</c> ist nur true, wenn die
    /// Seite alle Änderungen ab dem Cursor enthält — dann (und nur dann)
    /// darf das Gerät seinen Cursor auf <c>cursor</c> fortschreiben (D6).
    /// </summary>
    public async Task<SyncPullResponse> GetChangesAsync(long cursor, int limit, CancellationToken ct)
    {
        if (cursor < 0)
        {
            throw new LarioDomainException("Der Cursor muss mindestens 0 sein.");
        }

        var effectiveLimit = limit <= 0 ? DefaultPullLimit : Math.Min(limit, MaxPullLimit);

        // Konsistenter Lesezeitpunkt (D4/D6): Seitenzeilen und max Id
        // aus derselben Lese-Transaktion. Der Cursor ist das max Id der
        // TABELLE (N'), nicht das letzte Id der Seite — das Gerät
        // darf ihn nur bei complete=true fortschreiben; ein zwischen
        // Cursor und N' geschriebener Eintrag wird auf der nächsten
        // Seite nachgeliefert (Id > übergebener Cursor).
        SyncPullResponse response;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var maxId = await db.SyncChanges.AsNoTracking()
                .MaxAsync(c => (long?)c.Id, ct) ?? 0;
            var rows = await db.SyncChanges.AsNoTracking()
                .Where(c => c.Id > cursor)
                .OrderBy(c => c.Id)
                .Take(effectiveLimit)
                .Select(c => new SyncChangeDto(c.Id, c.EntityType, c.EntityId, c.Revision))
                .ToListAsync(ct);

            var complete = rows.Count < effectiveLimit;
            response = new SyncPullResponse(maxId, complete, !complete, rows);
        }
        finally
        {
            await transaction.RollbackAsync(ct);
        }

        return response;
    }

    /// <summary>JSON-Optionen für das Expense-Payload (Web-Default, siehe ToExpenseSpec).</summary>
    private static readonly System.Text.Json.JsonSerializerOptions ExpensePayloadOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    // ---------- Kanonisches Payload (Idempotenz-Nachweis, B1) ----------

    /// <summary>
    /// Kanonische JSON-Form des Payloads (B1): Objekte mit <b>ordinal
    /// sortierten Schlüsseln</b>, Arrays in Originalreihenfolge (fachlich
    /// signifikant), Primitive unverändert, kompakte Serialisierung (kein
    /// Whitespace), Schlüsselnamen exakt wie empfangen (camelCase), UTF-8.
    /// Beide Enden (Host, TypeScript-Client) müssen aus demselben JSON
    /// dieselben Bytes erzeugen — die Basis des serverseitigen
    /// Idempotenz-Hashes (D5: gleiche logische Inhalte → gleicher Hash,
    /// unabhängig von der Key-Reihenfolge im Client-JSON).
    /// </summary>
    internal static string CanonicalJson(JsonNode? payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return System.Text.Json.JsonSerializer.Serialize(CanonicalizeNode(payload), CanonicalJsonOptions);
    }

    /// <summary>
    /// Rekursive Kanonik: Objektschlüssel ordinal sortiert (byteweise,
    /// locale-unabhängig), Arrays in Originalreihenfolge, Werte rekursiv.
    /// JSON-<c>null</c> bleibt <c>null</c> (der JsonObject-Indexer hält
    /// Schlüssel mit <c>null</c>-Wert als JSON-<c>null</c> vor — der Key
    /// geht also nicht verloren).
    /// </summary>
    private static JsonNode? CanonicalizeNode(JsonNode? node)
    {
        if (node is null)
        {
            // JSON-null.
            return null;
        }

        if (node is JsonObject source)
        {
            // JsonObject implementiert in dieser Runtime direkt
            // IDictionary<string, JsonNode?> (keine Properties/Keys-
            // Eigenschaft) — Keys über die Iteration lesen.
            var sorted = new JsonObject();
            foreach (var key in source.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
            {
                sorted[key] = CanonicalizeNode(source[key]);
            }

            return sorted;
        }

        if (node is JsonArray sourceArray)
        {
            var result = new JsonArray();
            foreach (var item in sourceArray)
            {
                result.Add(CanonicalizeNode(item));
            }

            return result;
        }

        return node.DeepClone();
    }

    /// <summary>
    /// SHA-256-Hash des kanonischen Payloads: <b>64 hex-Zeichen,
    /// klein, kein Präfix</b> (B1 — byte-identisch zum Client-Hash; die
    /// Spalte <c>sync_operations.PayloadHash</c> ist max. 64).
    /// </summary>
    public static string CanonicalPayloadHash(JsonNode? payload)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson(payload!)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static readonly System.Text.Json.JsonSerializerOptions CanonicalJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

}
