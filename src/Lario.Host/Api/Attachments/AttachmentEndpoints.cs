using Lario.Application;
using Lario.Application.Attachments;
using Lario.Domain;
using Lario.Domain.Attachments;
using Lario.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Lario.Host.Api.Attachments;

/// <summary>
/// Beleg-Endpunkte (Schritt 7a, Design D3):
/// <list type="bullet">
/// <item><c>POST /api/v1/attachments</c> — multipart-Upload
///       (<c>file</c>, <c>bookingId</c>, optionales <c>splitId</c>) → 201</item>
/// <item><c>GET /api/v1/attachments/{id}</c> — Metadaten → 200 / 404</item>
/// <item><c>GET /api/v1/bookings/{id}/attachments</c> — Belege einer
///       Buchung → 200 / 400 (unbekannte Buchung)</item>
/// <item><c>GET /api/v1/attachments/{id}/download</c> — Datei (inline,
///       Originalname in <c>Content-Disposition</c>) → 200 / 404 / 410</item>
/// <item><c>DELETE /api/v1/attachments/{id}</c> → 204 / 404</item>
/// </list>
/// Die fachliche Validierung (Typ, Größe, Magic-Bytes, Referenzen)
/// passiert im <see cref="AttachmentService"/>; die Endpunkte bilden
/// nur HTTP-Mapping ab.
/// </summary>
public static class AttachmentEndpoints
{
    public static void MapAttachments(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/attachments", Upload)
            .WithName("uploadAttachment")
            // [FromForm] aktiviert die Anti-Forgery-Prüfung; der Host ist
            // eine Bearer-Token-API ohne Cookies → hier explizit deaktiviert.
            .DisableAntiforgery()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.ConsumesAttribute("multipart/form-data"))
            .Produces<AttachmentDto>(StatusCodes.Status201Created)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<Api.ErrorResponse>(StatusCodes.Status413PayloadTooLarge);

        endpoints.MapGet("/api/v1/attachments/{id}", GetById)
            .WithName("getAttachment")
            .Produces<AttachmentDto>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status404NotFound);

        endpoints.MapGet("/api/v1/attachments/{id}/download", Download)
            .WithName("downloadAttachment")
            .Produces(StatusCodes.Status200OK, typeof(byte[]), "application/octet-stream")
            .Produces<Api.ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<Api.ErrorResponse>(StatusCodes.Status410Gone);

        endpoints.MapGet("/api/v1/bookings/{bookingId}/attachments", GetByBooking)
            .WithName("getBookingAttachments")
            .Produces<List<AttachmentDto>>(StatusCodes.Status200OK)
            .Produces<Api.ErrorResponse>(StatusCodes.Status400BadRequest);

        endpoints.MapDelete("/api/v1/attachments/{id}", Delete)
            .WithName("deleteAttachment")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<Api.ErrorResponse>(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Multipart-Upload (Design D3: Felder file, bookingId, splitId). Die
    /// maximale Dateigröße kommt aus der Host-Konfiguration
    /// (<c>Lario:Attachments:MaxBytes</c>, Default 10 MB).
    ///
    /// Die Formularfelder werden explizit als <c>[FromForm]</c> gebunden
    /// (keine <c>HttpRequest</c>-Parameter): so erzeugt der OpenAPI-Generator
    /// einen korrekten multipart/form-data-Antragskörper statt einer
    /// fehlerhaften JSON-Body-Annahme. Fehlende Felder kommen als
    /// <c>null</c> an und werden hier mit dem Schritt-7-Fehlerformat
    /// (400 validation_error) abgewiesen.
    /// </summary>
    private static async Task<IResult> Upload(
        [FromForm] IFormFile? file,
        [FromForm] string? bookingId,
        [FromForm] string? splitId,
        AttachmentService service,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            throw new LarioDomainException(
                "Fehlendes oder leeres Feld 'file' (die Belegdatei). " +
                "Anfrage erwartet multipart/form-data mit den Feldern " +
                "'file', 'bookingId' und optional 'splitId'.");
        }

        if (string.IsNullOrWhiteSpace(bookingId))
        {
            throw new LarioDomainException("Fehlendes Pflichtfeld 'bookingId'.");
        }

        var attachment = await service.UploadAsync(
            file.FileName,
            file.ContentType,
            file.OpenReadStream(),
            bookingId,
            string.IsNullOrWhiteSpace(splitId) ? null : splitId,
            configuration.GetValue("Lario:Attachments:MaxBytes", 10_000_000L),
            cancellationToken);

        return Results.Created($"/api/v1/attachments/{attachment.Id}", ToDto(attachment));
    }

    /// <summary>Metadaten eines Belegs; unbekannt → 404.</summary>
    private static async Task<IResult> GetById(
        string id,
        IAttachmentRepository repository,
        CancellationToken cancellationToken)
    {
        var attachment = await repository.FindByIdAsync(id, cancellationToken);
        return attachment is null
            ? NotFound()
            : Results.Ok(ToDto(attachment));
    }

    /// <summary>
    /// Datei-Download (inline): Originalname im
    /// <c>Content-Disposition</c>-Header (ASCII-Name zusätzlich als
    /// <c>filename</c>, nicht-ASCII nur als RFC-5987-<c>filename*</c>);
    /// Metadaten vorhanden, Datei fehlt → 410.
    /// </summary>
    private static async Task<IResult> Download(
        HttpContext httpContext,
        string id,
        AttachmentService service,
        CancellationToken cancellationToken)
    {
        var (metadata, content) = await service.DownloadAsync(id, cancellationToken);
        if (metadata is null)
        {
            return NotFound();
        }
        if (content is null || content.Content is null)
        {
            return Results.Json(new Api.ErrorResponse(new Api.ApiErrorBody(
                "gone",
                "Die Bilddatei ist nicht mehr vorhanden.",
                DetailCode: "attachment_missing")),
                statusCode: StatusCodes.Status410Gone);
        }

        httpContext.Response.Headers.ContentDisposition =
            ContentDispositionFor(metadata.OriginalFilename, metadata.StoredName);
        httpContext.Response.ContentLength = content.SizeBytes;
        return Results.File(content.Content, metadata.ContentType);
    }

    /// <summary>Alle Belege einer Buchung; unbekannte Buchung → 400 invalid_reference.</summary>
    private static async Task<IResult> GetByBooking(
        string bookingId,
        IBookingRepository bookingRepository,
        IAttachmentRepository attachmentRepository,
        CancellationToken cancellationToken)
    {
        var booking = await bookingRepository.FindByIdAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            throw new InvalidReferenceException(
                $"Unbekannte Buchung: {bookingId}.");
        }

        var attachments = await attachmentRepository.GetByBookingAsync(bookingId, cancellationToken);
        return Results.Ok(attachments.Select(ToDto).ToList());
    }

    /// <summary>Beleg löschen (Datei → Metadaten, D5); unbekannt → 404.</summary>
    private static async Task<IResult> Delete(
        string id,
        AttachmentService service,
        CancellationToken cancellationToken)
    {
        var deleted = await service.DeleteAsync(id, cancellationToken);
        return deleted is null ? NotFound() : Results.NoContent();
    }

    private static IResult NotFound() => Results.Json(new Api.ErrorResponse(new Api.ApiErrorBody(
        "not_found",
        "Beleg nicht gefunden.",
        DetailCode: "attachment_not_found")),
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// RFC-5987-konforme Content-Disposition: nicht-ASCII-Zeichensätze
    /// (z. B. deutsche Umlaute) sind im klassischen <c>filename</c>
    /// unzulässig — dann steht dort ein ASCII-Fallback (Stored-Name) und
    /// der Originalname im <c>filename*</c>-Parameter.
    /// </summary>
    private static string ContentDispositionFor(string originalFilename, string storedName)
    {
        var isAscii = originalFilename.All(c => c >= 0x20 && c < 0x7F && c != '"');
        var fallback = isAscii ? originalFilename : storedName;
        var encoded = Uri.EscapeDataString(originalFilename);
        return $"inline; filename=\"{fallback}\"; filename*=UTF-8''{encoded}";
    }

    private static AttachmentDto ToDto(Attachment attachment) => new(
        attachment.Id,
        attachment.OriginalFilename,
        attachment.ContentType,
        attachment.SizeBytes,
        attachment.Sha256,
        attachment.SplitId,
        attachment.OriginalSplitId,
        attachment.CreatedAtUtc);
}

/// <summary>Metadaten-View für die API (keine internen Felder wie StoredName).</summary>
public sealed record AttachmentDto(
    string Id,
    string OriginalFilename,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string? SplitId,
    string? OriginalSplitId,
    DateTime CreatedAtUtc);
