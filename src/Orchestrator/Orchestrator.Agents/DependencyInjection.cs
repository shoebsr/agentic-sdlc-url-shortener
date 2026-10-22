using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orchestrator.Agents.Delivery;
using Orchestrator.Agents.Design;
using Orchestrator.Agents.Implementation;
using Orchestrator.Agents.Llm;
using Orchestrator.Agents.Requirements;
using Orchestrator.Agents.Tools;
using Orchestrator.Agents.Verification;
using Orchestrator.Agents.Workflows;
using Orchestrator.Core.Abstractions;

namespace Orchestrator.Agents;

public static class DependencyInjection
{
    public static IServiceCollection AddOrchestratorAgents(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<WorkspaceOptions>().BindConfiguration(WorkspaceOptions.SectionName);
        services.AddOptions<LlmOptions>().BindConfiguration(LlmOptions.SectionName).ValidateDataAnnotations();

        services.AddSingleton<IToolbox, ProcessToolbox>();
        services.AddSingleton<IWorkspaceProvider, GitWorkspaceProvider>();
        services.AddSingleton<ScenarioCatalog>();
        services.AddSingleton<RunFactory>();

        var provider = configuration.GetSection(LlmOptions.SectionName).GetValue<LlmProvider>(nameof(LlmOptions.Provider));
        switch (provider)
        {
            case LlmProvider.Anthropic:
                services.AddSingleton<ILlmClient, AnthropicLlmClient>();
                break;
            case LlmProvider.OpenAICompatible:
                services.AddHttpClient<ILlmClient, OpenAICompatibleLlmClient>((sp, http) =>
                    http.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<LlmOptions>>().Value.TimeoutSeconds));
                break;
            default:
                services.AddSingleton<ILlmClient, DisabledLlmClient>();
                break;
        }

        services.AddSingleton<IAgent, RequirementsAnalystAgent>();
        services.AddSingleton<IAgent, RuleBasedRequirementsAgent>();
        services.AddSingleton<IAgent, ClarificationBrokerAgent>();
        services.AddSingleton<IAgent, CodebaseAnalystAgent>();
        services.AddSingleton<IAgent, SolutionArchitectAgent>();
        services.AddSingleton<IAgent, DeliveryPlannerAgent>();
        services.AddSingleton<IAgent, ImplementationEngineerAgent>();
        services.AddSingleton<IAgent, BuildVerifierAgent>();
        services.AddSingleton<IAgent, TestRunnerAgent>();
        services.AddSingleton<IAgent, DiagnosticTestRunnerAgent>();
        services.AddSingleton<IAgent, SecurityReviewerAgent>();
        services.AddSingleton<IAgent, TechnicalWriterAgent>();
        services.AddSingleton<IAgent, ReleaseManagerAgent>();
        services.AddSingleton<IAgent, ReleasePublisherAgent>();
        services.AddSingleton<IAgentRegistry, AgentRegistry>();
        return services;
    }
}

public sealed class AgentRegistry(IEnumerable<IAgent> agents) : IAgentRegistry
{
    private readonly Dictionary<string, IAgent> _agents = agents.ToDictionary(a => a.Name, StringComparer.Ordinal);

    public IAgent Resolve(string name) => _agents.TryGetValue(name, out var agent)
        ? agent
        : throw new InvalidOperationException($"No agent registered as '{name}'.");

    public IReadOnlyCollection<string> Names => _agents.Keys;
}
