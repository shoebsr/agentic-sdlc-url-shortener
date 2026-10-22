using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;
using Orchestrator.Core.Abstractions;

namespace Orchestrator.Agents.Llm;

public sealed class DisabledLlmClient : ILlmClient
{
    public bool IsEnabled => false;

    public string Provider => "deterministic";

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No LLM provider is configured; agents run in deterministic mode.");
}

public sealed class AnthropicLlmClient(IOptions<LlmOptions> options) : ILlmClient, IDisposable
{
    private readonly LlmOptions _options = options.Value;
    private readonly Lazy<AnthropicClient> _client = new(() => new AnthropicClient
    {
        ApiKey = options.Value.ResolveApiKey(),
        Timeout = TimeSpan.FromSeconds(options.Value.TimeoutSeconds),
    });

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.ResolveApiKey());

    public string Provider => $"anthropic:{_options.Model}";

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await _client.Value.Messages.Create(new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = Math.Min(request.MaxTokens, _options.MaxOutputTokens),
            System = request.System,
            OutputConfig = new OutputConfig { Effort = ParseEffort(_options.Effort) },
            Messages = [new UserMessageParam { Content = request.Prompt }],
        }, cancellationToken: cancellationToken);

        if (response.StopReason == "refusal")
        {
            throw new InvalidOperationException("The model declined the request; the deterministic fallback agent takes over.");
        }

        var text = string.Join(Environment.NewLine, response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        return new LlmResponse(text, _options.Model, (int)response.Usage.InputTokens, (int)response.Usage.OutputTokens);
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    private static Effort ParseEffort(string value) => value.ToUpperInvariant() switch
    {
        "LOW" => Effort.Low,
        "HIGH" => Effort.High,
        "MAX" => Effort.Max,
        _ => Effort.Medium,
    };
}

public sealed class OpenAICompatibleLlmClient(HttpClient http, IOptions<LlmOptions> options) : ILlmClient
{
    private readonly LlmOptions _options = options.Value;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.ResolveApiKey()) && !string.IsNullOrWhiteSpace(_options.BaseUrl);

    public string Provider => $"openai-compatible:{_options.Model}";

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.BaseUrl!.TrimEnd('/') + "/"), "chat/completions"))
        {
            Content = JsonContent.Create(new ChatRequest(
                _options.Model,
                [new ChatMessage("system", request.System), new ChatMessage("user", request.Prompt)],
                Math.Min(request.MaxTokens, _options.MaxOutputTokens))),
        };
        message.Headers.Authorization = new("Bearer", _options.ResolveApiKey());

        using var response = await http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Empty completion response.");

        return new LlmResponse(
            (payload.Choices.Count > 0 ? payload.Choices[0].Message.Content : null) ?? string.Empty,
            payload.Model ?? _options.Model,
            payload.Usage?.PromptTokens ?? 0,
            payload.Usage?.CompletionTokens ?? 0);
    }

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("max_tokens")] int MaxTokens);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatResponse(
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice> Choices,
        [property: JsonPropertyName("usage")] ChatUsage? Usage);

    private sealed record ChatChoice([property: JsonPropertyName("message")] ChatMessage Message);

    private sealed record ChatUsage(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens);
}

public static class LlmJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static T? TryParse<T>(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text[start..(end + 1)], Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
