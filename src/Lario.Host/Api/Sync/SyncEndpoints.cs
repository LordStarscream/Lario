using Lario.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Lario.Host.Api.Sync;

/// <summary>
/// Sync-Endpunkte (Schritt 9, Batch A1), gemappt unter <c>/api/v1/sync</c>:
/// <list type="bullet">
/// <item><c>POST /pair</c> — Geräte-Kopplung (Sitzungstoken; liefert das
/// Klartext-Gerätetoken genau einmal).</item>
/// <item><c>GET /devices</c> / <c>DELETE /devices/{id}</c> — Geräte-Verwaltung
/// (Sitzungstoken; Widerruf ist idempotent).</item>
/// <item><c>POST /bootstrap</c> — vollständiger Fachstammdaten-Snapshot mit
/// Generationen und Cursor (Gerätetoken; Read-Transaktion, Design D4).</item>
/// <item><c>POST /push</c> — Geräteoperation (Gerätetoken; Idempotenz nach
/// (Gerät, OperationId), Design D5).</item>
/// <item><c>GET /changes?cursor=&amp;limit=</c> — Changelog-Pull (Gerätetoken;
/// <c>cursor</c> = Id der letzten verarbeiteten Zeile, Default 0, Design D6).</item>
/// </list>
/// Die Auth-Trennung (welche Endpunkte welche Token-Art verlangen) regelt
/// <see cref="SyncAuthMiddleware"/>; die Sync-Endpunkte lesen das Gerät aus
/// <c>HttpContext.Items[SyncAuthMiddleware.SyncDeviceIdItemKey]</c>.
/// Fehler laufen über das verbindliche API-Fehlerformat
/// (<see cref="ApiExceptionMiddleware"/>): <c>LarioDomainException</c> → 400
/// validation_error, <c>InvalidReferenceException</c> → 400
/// invalid_reference, <c>DomainConflictException</c> → 409 — nur
/// <c>payload_mismatch</c> (gleiche OperationId, anderer Inhalt) antwortet
/// der Push-Endpunkt selbst mit 409.
/// </summary>
public static class SyncEndpoints
{
    public static void MapSyncEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // ---------- Geräte-Kopplung (Sitzungstoken) ----------

        endpoints.MapPost("/api/v1/sync/pair", PairDevice)
            .WithName("syncPair")
            .Produces<DeviceDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapGet("/api/v1/sync/devices", ListDevices)
            .WithName("syncListDevices")
            .Produces<IReadOnlyList<DeviceDto>>(StatusCodes.Status200OK);

        endpoints.MapDelete("/api/v1/sync/devices/{id}", RevokeDevice)
            .WithName("syncRevokeDevice")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        // ---------- Sync-Endpunkte (aktives Gerätetoken) ----------

        endpoints.MapPost("/api/v1/sync/bootstrap", Bootstrap)
            .WithName("syncBootstrap")
            .Produces<BootstrapResponseDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapPost("/api/v1/sync/push", Push)
            .WithName("syncPush")
            .Produces<SyncPushResponse>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<Api.ErrorResponse>(StatusCodes.Status409Conflict);

        endpoints.MapGet("/api/v1/sync/changes", GetChanges)
            .WithName("syncGetChanges")
            .Produces<SyncPullResponse>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> PairDevice(
        [FromBody] PairDeviceRequest? request,
        SyncDeviceService devices,
        CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(request?.Name) ? "Android-Gerät" : request!.Name!.Trim();
        if (name.Length > 256)
        {
            throw new LarioDomainException("Gerätenamen zu lang (maximal 256 Zeichen).");
        }

        var (id, deviceName, token, createdAtUtc) = await devices.PairAsync(name, cancellationToken);
        return Results.Ok(new DeviceDto(id, deviceName, token, createdAtUtc, null, null));
    }

    private static async Task<IResult> ListDevices(
        SyncDeviceService devices, CancellationToken cancellationToken)
    {
        var list = await devices.ListAsync(cancellationToken);
        // Das Klartext-Token ist nicht wiederherstellbar (nur der Hash ist
        // persistiert) — die Liste liefert leer.
        return Results.Ok(list.Select(d => new DeviceDto(
            d.Id, d.Name, string.Empty, d.CreatedAtUtc, d.RevokedAtUtc, d.LastUsedAtUtc)).ToList());
    }

    private static async Task<IResult> RevokeDevice(
        string id, SyncDeviceService devices, CancellationToken cancellationToken)
    {
        try
        {
            await devices.RevokeAsync(id, cancellationToken);
        }
        catch (LarioDomainException)
        {
            // Unbekanntes Gerät: konsequent wie die anderen Stammdaten-
            // Endpunkte (400 invalid_reference).
            throw new InvalidReferenceException($"Unbekanntes Gerät '{id}'.");
        }

        return Results.NoContent();
    }

    private static async Task<IResult> Bootstrap(
        HttpContext context, SyncService service, CancellationToken cancellationToken)
    {
        var deviceId = (string)context.Items[SyncAuthMiddleware.SyncDeviceIdItemKey]!;
        var response = await service.BootstrapAsync(deviceId, cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> Push(
        HttpContext context,
        [FromBody] SyncPushRequest? envelope,
        SyncService service,
        CancellationToken cancellationToken)
    {
        var deviceId = (string)context.Items[SyncAuthMiddleware.SyncDeviceIdItemKey]!;
        if (envelope is null)
        {
            throw new LarioDomainException("Die Push-Operation fehlt (Body erforderlich).");
        }

        var result = await service.PushAsync(deviceId, envelope, cancellationToken);

        if (result.Status == "payload_mismatch")
        {
            return Results.Conflict(new Api.ErrorResponse(new Api.ApiErrorBody(
                "payload_mismatch", result.ConflictMessage ?? string.Empty)));
        }

        return Results.Ok(new SyncPushResponse(
            result.Status, result.BookingId, result.ProtocolGeneration, result.Revision, result.Ack));
    }

    private static async Task<IResult> GetChanges(
        [FromQuery(Name = "cursor")] long cursor,
        [FromQuery(Name = "limit")] int limit,
        SyncService service,
        CancellationToken cancellationToken)
    {
        var result = await service.GetChangesAsync(cursor, limit, cancellationToken);
        return Results.Ok(result);
    }
}
