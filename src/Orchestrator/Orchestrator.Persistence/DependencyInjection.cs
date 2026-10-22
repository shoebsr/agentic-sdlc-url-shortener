using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Core.Abstractions;

namespace Orchestrator.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddOrchestratorPersistence(this IServiceCollection services)
    {
        services.AddOptions<PersistenceOptions>().BindConfiguration(PersistenceOptions.SectionName);
        services.AddSingleton<IRunStore, FileRunStore>();
        return services;
    }
}
