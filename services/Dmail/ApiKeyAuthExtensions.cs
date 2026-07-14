namespace Dmail;

public static class ApiKeyAuthExtensions
{
    // Exact, closed allow-list of the only two /api/* paths exempt from the
    // X-Api-Key check: the GET OAuth entry points. Their security boundary is the
    // OAuth state + PKCE handshake (see EndpointExtensions callback), not the app
    // API key. Every other /api/* path (including any other /api/auth/* route)
    // stays default-deny. Matched exactly and GET-only — no prefix wildcard.
    private static readonly string[] OAuthExemptPaths =
    {
        "/api/auth/google/login",
        "/api/auth/google/callback",
    };

    public static IApplicationBuilder UseApiKeyAuth(this IApplicationBuilder app)
    {
        return app.Use(InvokeAsync);
    }

    internal static async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next();
            return;
        }

        if (IsOAuthEntryPoint(context.Request))
        {
            await next();
            return;
        }

        var apiKeyService = context.RequestServices.GetRequiredService<Services.ApiKeyService>();
        var key = context.Request.Headers["X-Api-Key"].FirstOrDefault();

        if (!apiKeyService.Validate(key))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
            return;
        }

        await next();
    }

    private static bool IsOAuthEntryPoint(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        foreach (var exempt in OAuthExemptPaths)
        {
            if (request.Path.StartsWithSegments(exempt, StringComparison.OrdinalIgnoreCase, out var remaining)
                && !remaining.HasValue)
            {
                return true;
            }
        }

        return false;
    }
}
