using BuildingBlocks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UrlShortener.Application.Abstractions;
using UrlShortener.Domain;

namespace UrlShortener.Application.Links;

public sealed partial class ShortLinkService(
    IShortLinkRepository repository,
    ILinkRouteCache cache,
    IAnalyticsReader analytics,
    IClickTracker clickTracker,
    IShortCodeGenerator codeGenerator,
    IManagementTokenService tokens,
    IVisitorFingerprint fingerprint,
    TimeProvider clock,
    IOptions<ShortenerOptions> options,
    ILogger<ShortLinkService> logger)
{
    private readonly ShortenerOptions _options = options.Value;
    private readonly Uri _publicBase = new(options.Value.PublicBaseUrl.TrimEnd('/') + "/");

    public async Task<Result<CreatedShortLink>> CreateAsync(CreateShortLinkCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var destination = DestinationUrl.Create(command.Url);
        if (!destination.IsSuccess)
        {
            return destination.Error;
        }

        if (string.Equals(destination.Value.Value.Host, _publicBase.Host, StringComparison.OrdinalIgnoreCase))
        {
            return DomainErrors.Url.SelfReference;
        }

        var now = clock.GetUtcNow();
        if (command.ExpiresAt > now.AddDays(_options.MaxExpiryDays))
        {
            return DomainErrors.Link.ExpiryTooFar;
        }

        var token = tokens.Issue();
        var isCustom = !string.IsNullOrWhiteSpace(command.CustomAlias);

        return isCustom
            ? await CreateWithAliasAsync(command.CustomAlias!, destination.Value, token, now, command.ExpiresAt, cancellationToken)
            : await CreateWithGeneratedCodeAsync(destination.Value, token, now, command.ExpiresAt, cancellationToken);
    }

    public async Task<Result<Uri>> ResolveAsync(string? code, ClickContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var shortCode = ShortCode.Create(code);
        if (!shortCode.IsSuccess)
        {
            return DomainErrors.Link.NotFound;
        }

        var route = await cache.GetOrLoadAsync(
            shortCode.Value, ct => repository.FindRouteAsync(shortCode.Value, ct), cancellationToken);
        if (route is null)
        {
            return DomainErrors.Link.NotFound;
        }

        var now = clock.GetUtcNow();
        var resolvable = route.EnsureResolvable(now);
        if (!resolvable.IsSuccess)
        {
            return resolvable.Error;
        }

        var click = new ClickEvent(
            route.LinkId,
            now,
            ExtractHost(context.Referrer),
            context.UserAgent,
            fingerprint.Compute(context.ClientAddress, context.UserAgent));

        if (!await clickTracker.TryTrackAsync(click, cancellationToken))
        {
            LogClickDropped(logger, route.Code);
        }

        return route.Destination;
    }

    public async Task<Result<ShortLinkView>> GetAsync(string? code, string? managementToken, CancellationToken cancellationToken)
    {
        var link = await LoadAuthorizedAsync(code, managementToken, cancellationToken);
        return link.IsSuccess ? ToView(link.Value) : link.Error;
    }

    public async Task<Result> DisableAsync(string? code, string? managementToken, CancellationToken cancellationToken)
    {
        var link = await LoadAuthorizedAsync(code, managementToken, cancellationToken);
        if (!link.IsSuccess)
        {
            return link.Error;
        }

        link.Value.Disable(clock.GetUtcNow());
        await repository.SaveChangesAsync(cancellationToken);
        await cache.InvalidateAsync(link.Value.Code, cancellationToken);
        LogLinkDisabled(logger, link.Value.Code.Value);
        return Result.Success();
    }

    public async Task<Result<LinkAnalyticsView>> GetAnalyticsAsync(
        string? code, string? managementToken, int windowDays, CancellationToken cancellationToken)
    {
        if (windowDays is < 1 or > 90)
        {
            return DomainErrors.Analytics.Window;
        }

        var link = await LoadAuthorizedAsync(code, managementToken, cancellationToken);
        if (!link.IsSuccess)
        {
            return link.Error;
        }

        var to = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var from = to.AddDays(-(windowDays - 1));
        var snapshot = await analytics.ReadAsync(link.Value.Id, from, to, _options.TopReferrers, cancellationToken);

        return new LinkAnalyticsView(
            link.Value.Code.Value,
            from,
            to,
            link.Value.ClickCount,
            snapshot.WindowClicks,
            snapshot.UniqueVisitors,
            snapshot.Daily,
            snapshot.TopReferrers);
    }

    private async Task<Result<CreatedShortLink>> CreateWithAliasAsync(
        string alias, DestinationUrl destination, IssuedToken token, DateTimeOffset now, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        var code = ShortCode.Create(alias);
        if (!code.IsSuccess)
        {
            return code.Error;
        }

        var link = ShortLink.Create(code.Value, destination, token.Hash, now, expiresAt, isCustomAlias: true);
        if (!link.IsSuccess)
        {
            return link.Error;
        }

        if (!await repository.TryAddAsync(link.Value, cancellationToken))
        {
            return DomainErrors.ShortCode.Taken;
        }

        LogLinkCreated(logger, link.Value.Code.Value, true);
        return new CreatedShortLink(ToView(link.Value), token.Token);
    }

    private async Task<Result<CreatedShortLink>> CreateWithGeneratedCodeAsync(
        DestinationUrl destination, IssuedToken token, DateTimeOffset now, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= _options.MaxCodeAllocationAttempts; attempt++)
        {
            var link = ShortLink.Create(codeGenerator.Next(), destination, token.Hash, now, expiresAt, isCustomAlias: false);
            if (!link.IsSuccess)
            {
                return link.Error;
            }

            if (await repository.TryAddAsync(link.Value, cancellationToken))
            {
                LogLinkCreated(logger, link.Value.Code.Value, false);
                return new CreatedShortLink(ToView(link.Value), token.Token);
            }

            LogCodeCollision(logger, attempt);
        }

        return DomainErrors.ShortCode.Exhausted;
    }

    private async Task<Result<ShortLink>> LoadAuthorizedAsync(string? code, string? managementToken, CancellationToken cancellationToken)
    {
        var shortCode = ShortCode.Create(code);
        if (!shortCode.IsSuccess)
        {
            return DomainErrors.Link.NotFound;
        }

        var link = await repository.FindByCodeAsync(shortCode.Value, cancellationToken);
        if (link is null)
        {
            return DomainErrors.Link.NotFound;
        }

        return tokens.Verify(managementToken, link.ManagementTokenHash) ? link : DomainErrors.Link.Unauthorized;
    }

    private ShortLinkView ToView(ShortLink link) => new(
        link.Code.Value,
        new Uri(_publicBase, link.Code.Value).AbsoluteUri,
        link.Destination.ToString(),
        link.CreatedAt,
        link.ExpiresAt,
        link.IsActive(clock.GetUtcNow()),
        link.ClickCount);

    private static string? ExtractHost(string? referrer) =>
        Uri.TryCreate(referrer, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.IdnHost : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "Short link {Code} created (custom alias: {IsCustom})")]
    private static partial void LogLinkCreated(ILogger logger, string code, bool isCustom);

    [LoggerMessage(Level = LogLevel.Information, Message = "Short link {Code} disabled")]
    private static partial void LogLinkDisabled(ILogger logger, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Generated short code collided on attempt {Attempt}")]
    private static partial void LogCodeCollision(ILogger logger, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Click for {Code} dropped: analytics pipeline is saturated")]
    private static partial void LogClickDropped(ILogger logger, string code);
}
