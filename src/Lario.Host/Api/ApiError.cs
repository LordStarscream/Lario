using Lario.Application.Attachments;
using Lario.Domain;

namespace Lario.Host.Api;

/// <summary>
/// Verbindliches API-Fehlerformat (Schritt 7, Design §2.3):
/// <c>{ "error": { "code": "...", "message": "...", ... } }</c>.
/// <see cref="DetailCode"/> verfeinert einen generischen Code für die
/// Beleg-Validierung (Schritt 7a, Design D3: z. B.
/// <c>validation_error</c> + <c>detailCode=attachment_too_large</c>);
/// bei allen anderen Fehlern null.
/// </summary>
public sealed record ApiErrorBody(
    string Code,
    string Message,
    int? ExpectedBaseRevision = null,
    int? ActualBaseRevision = null,
    string? DetailCode = null);

/// <summary>Antworthülle für alle API-Fehler.</summary>
public sealed record ErrorResponse(ApiErrorBody Error);

/// <summary>
/// Mapping der Domänen-Exceptionen auf HTTP-Status und Fehlercode
/// (Schritt 7, Design §2.3). Domain-Exceptionen sind fachliche
/// Ablehnungen (400) bzw. Revisionskonflikte (409); unerwartete
/// Exceptionen werden zu 500 mit generischer Meldung zusammengefasst
/// (Details nur im Host-Log).
/// </summary>
public static class ApiExceptionMapping
{
    public static (int StatusCode, ErrorResponse Body) Map(Exception exception)
    {
        switch (exception)
        {
            case DomainConflictException conflict:
                return (
                    StatusCodes.Status409Conflict,
                    new ErrorResponse(new ApiErrorBody(
                        "domain_conflict",
                        conflict.Message,
                        conflict.ExpectedBaseRevision,
                        conflict.ActualBaseRevision)));

            case InvalidReferenceException reference:
                return (
                    StatusCodes.Status400BadRequest,
                    new ErrorResponse(new ApiErrorBody("invalid_reference", reference.Message)));

            // Beleg-Validierung (Schritt 7a, Design D3): der generische
            // Code bleibt <c>validation_error</c>, der Detailcode sagt
            // aus, welche Prüfung fehlgeschlagen ist.
            case UnsupportedAttachmentTypeException type:
                return (
                    StatusCodes.Status400BadRequest,
                    new ErrorResponse(new ApiErrorBody(
                        "validation_error", type.Message, DetailCode: "unsupported_attachment_type")));

            case AttachmentTooLargeException tooLarge:
                return (
                    StatusCodes.Status413PayloadTooLarge,
                    new ErrorResponse(new ApiErrorBody(
                        "validation_error", tooLarge.Message, DetailCode: "attachment_too_large")));

            case CycleDetectedException cycle:
                return (
                    StatusCodes.Status400BadRequest,
                    new ErrorResponse(new ApiErrorBody("cycle_detected", cycle.Message)));

            case LarioDomainException domain:
                return (
                    StatusCodes.Status400BadRequest,
                    new ErrorResponse(new ApiErrorBody("validation_error", domain.Message)));

            default:
                return (
                    StatusCodes.Status500InternalServerError,
                    new ErrorResponse(new ApiErrorBody(
                        "internal_error",
                        "Ein interner Fehler ist auf dem Host aufgetreten.")));
        }
    }
}

/// <summary>
/// Fangt Exceptionen aus den API-Endpunkten und schreibt das verbindliche
/// Fehlerformat. Läuft nach der statischen Datei-Auslieferung und vor den
/// Endpunkten; statische Dateien und das Fallback (index.html) bleiben
/// unberührt.
/// </summary>
public sealed class ApiExceptionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            if (context.Response.HasStarted)
            {
                throw;
            }

            var (statusCode, body) = ApiExceptionMapping.Map(exception);
            if (statusCode == StatusCodes.Status500InternalServerError)
            {
                context.RequestServices
                    .GetRequiredService<ILogger<ApiExceptionMiddleware>>()
                    .LogError(
                        exception,
                        "Unerwarteter Fehler bei {Method} {Path}",
                        context.Request.Method,
                        context.Request.Path);
            }

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(body);
        }
    }
}
