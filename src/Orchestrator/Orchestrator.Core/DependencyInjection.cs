using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Core.Engine;

namespace Orchestrator.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddOrchestratorEngine(this IServiceCollection services)
    {
        services.AddOptions<EngineOptions>().BindConfiguration(EngineOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RunJournal>();
        services.AddSingleton<RunInboxes>();
        services.AddSingleton<WorkflowEngine>();
        services.AddSingleton<RunCoordinator>();
        return services;
    }
}
