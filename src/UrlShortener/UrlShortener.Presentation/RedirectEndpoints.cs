using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using UrlShortener.Application.Links;

namespace UrlShortener.Presentation;

public static class RedirectEndpoints
{
    public static IEndpointRouteBuilder MapRedirectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/{code:regex(^[A-Za-z0-9_-]{{4,32}}$)}", RedirectAsync)
            .WithName("ResolveShortLink")
            .WithTags("Redirect")
            .WithSummary("Redirect to the destination URL")
            .RequireRateLimiting(RateLimitPolicies.Redirect)
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone);

        return endpoints;
    }

    private static async Task<IResult> RedirectAsync(
        string code, HttpContext http, ShortLinkService service, CancellationToken cancellationToken)
    {
        var context = new ClickContext(
            http.Connection.RemoteIpAddress?.ToString(),
            http.Request.Headers.Referer.ToString(),
            http.Request.Headers.UserAgent.ToString());

        var result = await service.ResolveAsync(code, context, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        http.Response.Headers[HeaderNames.CacheControl] = "private, no-store";
        return TypedResults.Redirect(result.Value.AbsoluteUri);
    }
}
