using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UrlShortener.Application.Abstractions;
using UrlShortener.Domain;

namespace UrlShortener.Infrastructure.Clicks;

internal sealed partial class ClickBatchPersister(
    IServiceScopeFactory scopeFactory, IOptions<AnalyticsOptions> options, ILogger<ClickBatchPersister> logger)
{
    private readonly int _maxAttempts = options.Value.MaxPersistAttempts;

    public async Task<bool> PersistAsync(IReadOnlyCollection<ClickEvent> batch, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IClickSink>().PersistAsync(batch, cancellationToken);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < _maxAttempts)
            {
                LogRetry(logger, batch.Count, attempt, ex);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt)), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogGaveUp(logger, batch.Count, ex);
            }
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Persisting {Count} clicks failed on attempt {Attempt}; retrying")]
    private static partial void LogRetry(ILogger logger, int count, int attempt, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Persisting {Count} clicks failed after all attempts")]
    private static partial void LogGaveUp(ILogger logger, int count, Exception exception);
}
