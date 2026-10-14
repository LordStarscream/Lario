namespace Lario.Host.Api;

/// <summary>Login-Anfrage (Benutzername + Passwort, Design D2).</summary>
public sealed record LoginRequest(string? Username, string? Password);

/// <summary>
/// Login-Antwort: Bearer-Token für alle weiteren <c>/api/v1</c>-Aufrufe
/// und dessen Gültigkeitsdauer in Minuten.
/// </summary>
public sealed record LoginResponse(string Token, int ExpiresInMinutes);

/// <summary>
/// Authentifizierungs-Endpunkte (Schritt 7, Design D1):
/// <c>POST /api/v1/auth/login</c> (öffentlich) und
/// <c>POST /api/v1/auth/logout</c> (entwertet den Token).
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapPost("/api/v1/auth/login", (LoginRequest? request, AuthService auth, AuthTokenStore tokens) =>
            {
                if (request is null || !auth.Verify(request.Username, request.Password))
                {
                    return Results.Json(
                        new ErrorResponse(new ApiError(
                            "unauthorized",
                            "Benutzername oder Passwort ist falsch.")),
                        statusCode: StatusCodes.Status401Unauthorized);
                }

                var (token, _) = tokens.Issue();
                return Results.Ok(new LoginResponse(token, tokens.DefaultValidityMinutes));
            })
            .WithName("authLogin")
            .Produces<LoginResponse>(StatusCodes.Status200OK)
            .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized);

        endpoints
            .MapPost("/api/v1/auth/logout", (HttpRequest request, AuthTokenStore tokens) =>
            {
                // Token-Prüfung läuft in UseApiAuth; Logout ist idempotent.
                tokens.Revoke(ApiAuth.ExtractBearerToken(request));
                return Results.NoContent();
            })
            .WithName("authLogout")
            .Produces(StatusCodes.Status204NoContent);
    }
}
