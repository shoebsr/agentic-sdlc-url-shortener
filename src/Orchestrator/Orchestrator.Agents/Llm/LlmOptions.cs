using System.ComponentModel.DataAnnotations;

namespace Orchestrator.Agents.Llm;

public enum LlmProvider
{
    None,
    Anthropic,
    OpenAICompatible,
}

public sealed class LlmOptions
{
    public const string SectionName = "Orchestrator:Llm";

    public LlmProvider Provider { get; set; } = LlmProvider.None;

    public string Model { get; set; } = "claude-opus-5-5";

    public string Effort { get; set; } = "medium";

    public string ApiKeyEnvironmentVariable { get; set; } = "ANTHROPIC_API_KEY";

    public string? BaseUrl { get; set; }

    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 120;

    [Range(256, 128_000)]
    public int MaxOutputTokens { get; set; } = 16_000;

    public string? ResolveApiKey() =>
        string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable) ? null : Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
}
