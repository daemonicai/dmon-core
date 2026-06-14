namespace Daemonic.Dmail;

public static class ApiKeyAuthExtensions
{
    public static RouteHandlerBuilder RequireApiKey(this RouteHandlerBuilder builder)
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var apiKeyService = context.HttpContext.RequestServices
                .GetRequiredService<Services.ApiKeyService>();

            var key = context.HttpContext.Request.Headers["X-Api-Key"].FirstOrDefault();

            if (!apiKeyService.Validate(key))
            {
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            }

            return await next(context);
        });

        return builder;
    }
}
