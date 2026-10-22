using BuildingBlocks;
using Microsoft.AspNetCore.Http;

namespace UrlShortener.Presentation;

internal static class ResultHttpExtensions
{
    public static IResult ToProblem(this Error error) => TypedResults.Problem(
        statusCode: StatusFor(error.Kind),
        title: error.Message,
        type: $"https://errors.shortener.local/{error.Code}",
        extensions: new Dictionary<string, object?> { ["code"] = error.Code });

    private static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.Gone => StatusCodes.Status410Gone,
        ErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };
}
