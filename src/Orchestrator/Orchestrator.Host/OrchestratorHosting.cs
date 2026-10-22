using System.Text.Json.Serialization;
using Orchestrator.Agents;
using Orchestrator.Agents.Tools;
using Orchestrator.Core;
using Orchestrator.Governance;
using Orchestrator.Observability;
using Orchestrator.Persistence;

namespace Orchestrator.Host;

public static class OrchestratorHosting
{
    public const string DataRootKey = "Orchestrator:DataRoot";

    public static IServiceCollection AddOrchestrator(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOrchestratorEngine();
        services.AddOrchestratorGovernance();
        services.AddOrchestratorObservability();
        services.AddOrchestratorPersistence();
        services.AddOrchestratorAgents(configuration);

        services.PostConfigure<WorkspaceOptions>(o => o.WorkspacesRoot = UnderDataRoot(configuration, o.WorkspacesRoot, o));
        services.PostConfigure<PersistenceOptions>(o => o.RunsDirectory = UnderDataRoot(configuration, o.RunsDirectory, null));
        services.PostConfigure<ObservabilityOptions>(o =>
        {
            if (!string.IsNullOrWhiteSpace(o.AuditFile))
            {
                o.AuditFile = UnderDataRoot(configuration, o.AuditFile, null);
            }
        });

        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        return services;
    }

    private static string UnderDataRoot(IConfiguration configuration, string path, WorkspaceOptions? workspace)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        var configured = configuration[DataRootKey];
        var root = !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine((workspace ?? BindWorkspace(configuration)).ResolveSourceRoot(), ".data");
        return Path.Combine(root, path);
    }

    private static WorkspaceOptions BindWorkspace(IConfiguration configuration)
    {
        var options = new WorkspaceOptions();
        configuration.GetSection(WorkspaceOptions.SectionName).Bind(options);
        return options;
    }
}
