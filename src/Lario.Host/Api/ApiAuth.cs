using Microsoft.AspNetCore.Http;

namespace Lario.Host.Api;

/// <summary>
/// Bearer-Token-Prüfung für die lokale API (Schritt 7, Design D1).
/// Alle <c>/api/v1</c>-Endpunkte außer <c>/api/v1/auth/login</c> erfordern
/// <c>Authorization: Bearer &lt;token&gt;</c>; statische Weboberflächendateien
/// (Frontend-Build) sind nicht geschützt — die Authentifizierung erfolgt
/// über die API-Ebene.
///
/// Schritt 9 erweitert die Zugangsdaten um widerrufbare Gerätetokens
/// (Gerätekopplung) mit <b>bidirektionaler Endpunkt-Trennung</b>:
/// <list type="bullet">
/// <item>Alle <c>/api/v1</c>-Endpunkte (außer Login) akzeptieren ein
/// lokales Sitzungstoken.</item>
/// <item>Ein aktives Gerätetoken ist <b>nur</b> an den drei
/// Sync-Geräte-Endpunkten (<c>POST /api/v1/sync/bootstrap</c>,
/// <c>POST /api/v1/sync/push</c>, <c>GET /api/v1/sync/changes</c>) gültig
/// — an allen anderen Endpunkten wird es mit 401 verworfen (ein Gerät
/// verwaltet keine Stammdaten über die API).</item>
/// <item>Die Sync-Endpunkte selbst verlangen das Gerätetoken (ein
/// Sitzungstoken wird dort verworfen); die Trennung wird zusätzlich von
/// <see cref="SyncAuthMiddleware"/> für <c>/api/v1/sync/*</c> geprüft
/// (doppelte Absicherung).</item>
/// </list>
/// </summary>
public static class ApiAuth
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>Extrahiert den Bearer-Token-Wert aus dem Authorization-Header.</summary>
    public static string? ExtractBearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var token = header[BearerPrefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>
    /// Schützt die <c>/api/v1</c>-Oberfläche vor allen API-Endpunkten.
    /// Antwort bei fehlendem/ungültigem Token: 401 mit dem verbindlichen
    /// Fehlerformat (Code <c>unauthorized</c>).
    ///
    /// Die Pfadprüfung deckt exakt dieselben Varianten ab wie das Routing:
    /// ASP.NET-Core-Routen matchen Literaltext case-insensitiv, die
    /// Prüfung tut das deshalb auch (Review R1 P1) — sonst umgeht
    /// <c>/API/V1/...</c> die Authentifizierung. Segmentbasiert:
    /// <c>/api/v1</c> exakt oder als vollständiges Pfadsegment
    /// (<c>/api/v1foo</c> gehört nicht zur API-Oberfläche).
    /// </summary>
    public static IApplicationBuilder UseApiAuth(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            var isApiV1 = string.Equals(path, "/api/v1", StringComparison.OrdinalIgnoreCase)
                           || path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase);
            var isLogin = string.Equals(path, "/api/v1/auth/login", StringComparison.OrdinalIgnoreCase);

            if (isApiV1 && !isLogin)
            {
                var token = ExtractBearerToken(context.Request);
                var tokens = context.RequestServices.GetRequiredService<AuthTokenStore>();
                if (tokens.TryValidate(token))
                {
                    // Sitzungstoken: an allen Endpunkten gültig (die
                    // Sync-Geräte-Endpunkte werfen es in der
                    // SyncAuthMiddleware ab).
                    await next(context);
                    return;
                }

                if (IsSyncDeviceEndpoint(context.Request)
                    && await TryValidateDeviceTokenAsync(context, token))
                {
                    // Sync-Geräte-Endpunkt: aktives Gerätetoken ist
                    // ausreichend.
                    await next(context);
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(
                    new ErrorResponse(new ApiErrorBody("unauthorized", UnauthorizedMessage(
                        await IsDeviceTokenAsync(context, token),
                        IsSyncDeviceEndpoint(context.Request)))));
                return;
            }

            await next(context);
        });
    }

    /// <summary>
    /// Die drei Sync-Geräte-Endpunkte, an denen ein aktives Gerätetoken
    /// (und kein Sitzungstoken) gültig ist (bidirektionale Trennung).
    /// </summary>
    private static bool IsSyncDeviceEndpoint(HttpRequest request)
    {
        var path = request.Path.Value ?? string.Empty;
        var method = request.Method;
        return (method.Equals(HttpMethods.Post, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(path, "/api/v1/sync/bootstrap", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(path, "/api/v1/sync/push", StringComparison.OrdinalIgnoreCase)))
            || (method.Equals(HttpMethods.Get, StringComparison.OrdinalIgnoreCase)
                && string.Equals(path, "/api/v1/sync/changes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>401-Meldung je nach Token-Art und Endpunkt (siehe Klassendoc).</summary>
    private static string UnauthorizedMessage(bool isDeviceToken, bool isSyncDeviceEndpoint)
    {
        if (isDeviceToken)
        {
            return isSyncDeviceEndpoint
                ? "Das Gerätetoken ist nicht mehr gültig (widerrufen?)."
                : "Gerätetokens sind nur an den Sync-Endpunkten gültig.";
        }

        return "Kein gültiger Zugriffstoken. Melde dich über POST /api/v1/auth/login an.";
    }

    private static async Task<bool> TryValidateDeviceTokenAsync(HttpContext context, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        // SyncDeviceService ist scoped — der Request-Scope wird explizit
        // angelegt (die Middleware läuft außerhalb eines Endpoint-Scope).
        using var scope = context.RequestServices.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<Sync.SyncDeviceService>();
        return await devices.TryValidateDeviceTokenAsync(token) is not null;
    }

    private static async Task<bool> IsDeviceTokenAsync(HttpContext context, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        using var scope = context.RequestServices.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<Sync.SyncDeviceService>();
        return await devices.IsDeviceTokenAsync(token, context.RequestAborted);
    }
}

/// <summary>
/// Auth-Trennung der Sync-Endpunkte (Schritt 9, Batch A1); läuft nach
/// <c>UseApiAuth</c> (das Bearer-Format und die Gültigkeit sind dort
/// bereits geprüft) und nur für <c>/api/v1/sync/*</c>:
/// <list type="bullet">
/// <item>Kopplungs-Endpunkte (<c>POST /pair</c>, <c>GET/DELETE
/// /devices…</c>) verlangen ein lokales Sitzungstoken — ein
/// Gerätetoken erhält 401 (ein Gerät verwaltet sich nicht selbst).</item>
/// <item>Sync-Endpunkte (<c>POST /bootstrap</c>, <c>POST /push</c>,
/// <c>GET /changes</c>) verlangen ein aktives Gerätetoken — ein
/// Sitzungstoken erhält 401 „Gerät nicht mehr verbunden.“</item>
/// </list>
/// Bei Erfolg legt die Middleware <c>HttpContext.Items["Lario.SyncDeviceId"]</c>
/// an (die gerätebezogene Idempotenz des Push braucht sie).
/// Die Pfadprüfung ist wie <c>UseApiAuth</c> segmentbasiert und
/// case-insensitiv.
/// </summary>
public sealed class SyncAuthMiddleware(RequestDelegate next)
{
    public const string SyncDeviceIdItemKey = "Lario.SyncDeviceId";

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var isSync = string.Equals(path, "/api/v1/sync", StringComparison.OrdinalIgnoreCase)
                     || path.StartsWith("/api/v1/sync/", StringComparison.OrdinalIgnoreCase);
        if (!isSync)
        {
            await next(context);
            return;
        }

        // Die Tokenart richtet sich nach dem PFAD (methodenunabhängig):
        // Falsche Methode an bekanntem Pfad → 405 vom Routing; unbekannter
        // Pfad unter /api/v1/sync → 404 hier (siehe unten) — beides vor
        // dem SPA-Fallback (MapFallbackToFile) beantwortet.
        var isPairingEndpoint = string.Equals(path, "/api/v1/sync/pair", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(path, "/api/v1/sync/devices", StringComparison.OrdinalIgnoreCase)
                                || IsSyncDeviceSegment(path, "/api/v1/sync/devices");
        var isSyncEndpoint = string.Equals(path, "/api/v1/sync/bootstrap", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(path, "/api/v1/sync/push", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(path, "/api/v1/sync/changes", StringComparison.OrdinalIgnoreCase);

        var token = ApiAuth.ExtractBearerToken(context.Request);
        var tokens = context.RequestServices.GetRequiredService<AuthTokenStore>();
        var isSessionToken = tokens.TryValidate(token);

        if (isPairingEndpoint)
        {
            if (!isSessionToken)
            {
                await WriteUnauthorizedAsync(context, "Kopplungs-Endpunkte erfordern ein Sitzungstoken.");
                return;
            }

            await next(context);
            return;
        }

        if (isSyncEndpoint)
        {
            // Sync-Endpunkte: aktives Gerätetoken erforderlich — ein
            // Sitzungstoken wird verworfen (bidirektionale Trennung).
            if (isSessionToken)
            {
                await WriteUnauthorizedAsync(context, "Sync-Endpunkte erfordern ein Gerätetoken.");
                return;
            }

            using var scope = context.RequestServices.CreateScope();
            var devices = scope.ServiceProvider.GetRequiredService<Sync.SyncDeviceService>();
            var device = await devices.TryValidateDeviceTokenAsync(token);
            if (device is null)
            {
                await WriteUnauthorizedAsync(context, "Gerät nicht mehr verbunden.");
                return;
            }

            context.Items[SyncDeviceIdItemKey] = device.Id;
            await next(context);
            return;
        }

        // Unbekannter Pfad unter /api/v1/sync: 404 direkt aus der
        // Middleware mit dem verbindlichen Fehlerformat — andernfalls
        // würde MapFallbackToFile ein HTML-Dokument/404-Seite liefern.
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(
            new ErrorResponse(new ApiErrorBody("not_found", "Unbekannter Sync-Endpunkt.")));
    }

    private static bool IsSyncDeviceSegment(string path, string prefix)
        => path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
           && path[(prefix.Length + 1)..].Contains('/') == false
           && path.Length > prefix.Length + 1;

    private static Task WriteUnauthorizedAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(
            new ErrorResponse(new ApiErrorBody("unauthorized", message)));
    }
}
