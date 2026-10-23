using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Orchestrator.Agents.Tools;
using Orchestrator.Core;
using Orchestrator.Core.Model;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Orchestrator.Agents.Workflows;

public sealed class ScenarioDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public ScenarioKind Kind { get; set; }

    public string Requirement { get; set; } = string.Empty;

    public string InitiatedBy { get; set; } = "requester";

    public string Description { get; set; } = string.Empty;

    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.Ordinal);
}

public sealed record RunRequest(
    string? ScenarioId,
    string? Requirement,
    ScenarioKind? Kind,
    string? Title,
    string? InitiatedBy,
    string? Execution,
    string? Faults,
    IReadOnlyDictionary<string, string>? Settings);

public sealed class ScenarioCatalog(IOptions<WorkspaceOptions> options)
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public IReadOnlyList<ScenarioDefinition> List()
    {
        var directory = Path.Combine(options.Value.ResolveSourceRoot(), "scenarios");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(directory, "*.yaml")
            .Order(StringComparer.Ordinal)
            .Select(file => Yaml.Deserialize<ScenarioDefinition>(File.ReadAllText(file)))
            .Where(s => !string.IsNullOrWhiteSpace(s.Id))];
    }

    public ScenarioDefinition? Find(string id) => List().FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

public sealed class RunFactory(ScenarioCatalog scenarios, TimeProvider clock)
{
    private static readonly HashSet<string> OverridableSettings = new(StringComparer.Ordinal)
    {
        RunSettings.ExecutionMode, RunSettings.Faults, RunSettings.AutoApprove, "injectSecret", "preview",
    };

    public WorkflowRun Create(RunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scenario = request.ScenarioId is null ? null : scenarios.Find(request.ScenarioId)
            ?? throw new ArgumentException($"Unknown scenario '{request.ScenarioId}'.", nameof(request));

        var requirement = request.Requirement ?? scenario?.Requirement;
        if (string.IsNullOrWhiteSpace(requirement) || requirement.Length > 4000)
        {
            throw new ArgumentException("A requirement of 1-4000 characters is required.", nameof(request));
        }

        var kind = request.Kind ?? scenario?.Kind ?? ScenarioKind.Brownfield;
        var settings = new Dictionary<string, string>(scenario?.Settings ?? [], StringComparer.Ordinal);
        if (!settings.ContainsKey(RunSettings.Baseline))
        {
            settings[RunSettings.Baseline] = kind == ScenarioKind.Greenfield ? "empty" : "repository";
        }

        foreach (var (key, value) in request.Settings ?? new Dictionary<string, string>())
        {
            if (OverridableSettings.Contains(key))
            {
                settings[key] = value;
            }
        }

        if (request.Execution is not null)
        {
            settings[RunSettings.ExecutionMode] = request.Execution;
        }

        if (request.Faults is not null)
        {
            settings[RunSettings.Faults] = request.Faults;
        }

        var now = clock.GetUtcNow();
        return new WorkflowRun
        {
            Id = $"run-{now:yyyyMMdd-HHmmss}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}",
            Title = request.Title ?? scenario?.Title ?? Truncate(requirement),
            Kind = kind,
            Requirement = requirement.Trim(),
            InitiatedBy = request.InitiatedBy ?? scenario?.InitiatedBy ?? "requester",
            ScenarioId = scenario?.Id,
            CreatedAt = now,
            Nodes = [.. WorkflowTemplates.For(kind)],
            Settings = settings,
        };
    }

    private static string Truncate(string text) => text.Length <= 60 ? text : text[..60] + "…";
}
