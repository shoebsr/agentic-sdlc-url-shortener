using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Core.Abstractions;

namespace Orchestrator.Observability;

public static class DependencyInjection
{
    public static IServiceCollection AddOrchestratorObservability(this IServiceCollection services)
    {
        services.AddOptions<ObservabilityOptions>().BindConfiguration(ObservabilityOptions.SectionName);
        services.AddSingleton<IAuditLog, HashChainedAuditLog>();
        services.AddSingleton<IRunEventPublisher, RunEventPublisher>();
        services.AddSingleton<IReliabilityMetrics, ReliabilityMetrics>();
        return services;
    }
}
