namespace Dmail;

public static class ApiKeyAuthExtensions
{
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
}
