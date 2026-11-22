using KatalepsisFT.Application.Auth;

namespace KatalepsisFT.Web.Auth;

/// <summary>Two checks that apply to every request under /api before it reaches a controller.</summary>
public static class ApiGuards
{
    /// <summary>
    /// Browsers cannot add a custom header to a cross-site request without a CORS preflight, and this service allows no
    /// cross-origin callers. Requiring the header on every state-changing request therefore blocks cross-site request forgery.
    /// </summary>
    public const string RequestHeader = "X-KFT-Request";

    private const string AuthPrefix = "/api/auth";

    public static IApplicationBuilder UseApiGuards(this IApplicationBuilder app)
    {
        return app.Use(
            async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    bool changesState = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method);
                    if (changesState && !context.Request.Headers.ContainsKey(RequestHeader))
                    {
                        await RejectAsync(context, StatusCodes.Status400BadRequest, $"The {RequestHeader} header is required.");
                        return;
                    }

                    bool mustChangePassword = context.User.HasClaim(c => c.Type == FtClaims.MustChangePassword);
                    if (mustChangePassword && !context.Request.Path.StartsWithSegments(AuthPrefix))
                    {
                        await RejectAsync(context, StatusCodes.Status403Forbidden, "Change your password before continuing.");
                        return;
                    }
                }

                await next(context);
            }
        );
    }

    private static Task RejectAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(new ErrorResponse(message));
    }
}
