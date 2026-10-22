namespace Orchestrator.Observability;

public sealed class ObservabilityOptions
{
    public const string SectionName = "Orchestrator:Observability";

    public string? AuditFile { get; set; } = "audit.jsonl";
}
