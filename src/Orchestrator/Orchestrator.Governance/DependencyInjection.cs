using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Core.Abstractions;
using Orchestrator.Governance.Policies;

namespace Orchestrator.Governance;

public static class DependencyInjection
{
    public static IServiceCollection AddOrchestratorGovernance(this IServiceCollection services)
    {
        services.AddOptions<GovernanceOptions>().BindConfiguration(GovernanceOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<IPolicy, SecretLeakPolicy>();
        services.AddSingleton<IPolicy, InsecureCodePolicy>();
        services.AddSingleton<IPolicy, WorkspaceBoundaryPolicy>();
        services.AddSingleton<IPolicy, PersonalDataPolicy>();
        services.AddSingleton<IPolicy, ProtectedPathPolicy>();
        services.AddSingleton<IPolicy, ChangeBudgetPolicy>();
        services.AddSingleton<IPolicy, DependencyChangePolicy>();
        services.AddSingleton<IPolicy, DestructiveSchemaPolicy>();
        services.AddSingleton<IPolicyEngine, PolicyEngine>();
        services.AddSingleton<IGateRegistry, GateRegistry>();
        services.AddSingleton<IApprovalAuthority, ApprovalAuthority>();
        services.AddSingleton<IKillSwitch, KillSwitch>();
        return services;
    }
}
