using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using UrlShortener.Application.Links;

namespace UrlShortener.Presentation;

public static class LinkEndpoints
{
    public const string ManagementTokenHeader = "X-Management-Token";

    public static IEndpointRouteBuilder MapLinkManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var links = endpoints.MapGroup("/api/v1/links")
            .WithTags("Links")
            .RequireRateLimiting(RateLimitPolicies.Management);

        links.MapPost("/", CreateAsync)
            .WithName("CreateShortLink")
            .WithSummary("Create a short link")
            .RequireRateLimiting(RateLimitPolicies.Create)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        links.MapGet("/{code}", GetAsync)
            .WithName("GetShortLink")
            .WithSummary("Get short link details")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        links.MapDelete("/{code}", DisableAsync)
            .WithName("DisableShortLink")
            .WithSummary("Disable a short link; history is retained")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        links.MapGet("/{code}/analytics", AnalyticsAsync)
            .WithName("GetShortLinkAnalytics")
            .WithSummary("Click analytics for a time window")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<Results<Created<CreatedShortLink>, ProblemHttpResult>> CreateAsync(
        CreateLinkRequest request, ShortLinkService service, CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(
            new CreateShortLinkCommand(request.Url, request.CustomAlias, request.ExpiresAt), cancellationToken);

        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/links/{result.Value.Link.Code}", result.Value)
            : (ProblemHttpResult)result.Error.ToProblem();
    }

    private static async Task<IResult> GetAsync(
        string code,
        [FromHeader(Name = ManagementTokenHeader)] string? token,
        ShortLinkService service,
        CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(code, token, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<IResult> DisableAsync(
        string code,
        [FromHeader(Name = ManagementTokenHeader)] string? token,
        ShortLinkService service,
        CancellationToken cancellationToken)
    {
        var result = await service.DisableAsync(code, token, cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<IResult> AnalyticsAsync(
        string code,
        [FromHeader(Name = ManagementTokenHeader)] string? token,
        ShortLinkService service,
        CancellationToken cancellationToken,
        int days = 7)
    {
        var result = await service.GetAnalyticsAsync(code, token, days, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}

public sealed record CreateLinkRequest(string? Url, string? CustomAlias, DateTimeOffset? ExpiresAt);
