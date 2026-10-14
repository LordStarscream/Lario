using Microsoft.AspNetCore.Http;

namespace Lario.Host.Api;

/// <summary>
/// Bearer-Token-Prüfung für die lokale API (Schritt 7, Design D1).
/// Alle <c>/api/v1</c>-Endpunkte außer <c>/api/v1/auth/login</c> erfordern
/// <c>Authorization: Bearer &lt;token&gt;</c>; statische Weboberflächendateien
/// (Frontend-Build) sind nicht geschützt — die Authentifizierung erfolgt
/// über die API-Ebene.
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
    /// </summary>
    public static IApplicationBuilder UseApiAuth(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            var isApiV1 = path.StartsWith("/api/v1", StringComparison.Ordinal);
            var isLogin = string.Equals(path, "/api/v1/auth/login", StringComparison.Ordinal);

            if (isApiV1 && !isLogin)
            {
                var tokens = context.RequestServices.GetRequiredService<AuthTokenStore>();
                if (!tokens.TryValidate(ExtractBearerToken(context.Request)))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(
                        new ErrorResponse(new ApiError(
                            "unauthorized",
                            "Kein gültiger Zugriffstoken. Melde dich über POST /api/v1/auth/login an.")));
                    return;
                }
            }

            await next(context);
        });
    }
}
