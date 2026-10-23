using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Model;

namespace Orchestrator.UnitTests.Support;

internal sealed class ScriptedAgent(string name, Func<AgentContext, int, Task<AgentOutcome>> behaviour) : IAgent
{
    private int _calls;
    private int _compensations;

    public string Name { get; } = name;

    public int Calls => _calls;

    public int Compensations => _compensations;

    public static ScriptedAgent Succeeds(string name, string? artifactKey = null) =>
        new(name, (_, _) => Task.FromResult(Success(artifactKey ?? name + ".out")));

    public static ScriptedAgent FailsTimes(string name, int failures, bool transient = true) =>
        new(name, (_, call) => Task.FromResult(call <= failures
            ? AgentOutcome.Failure($"failure {call}", transient)
            : Success(name + ".out")));

    public static ScriptedAgent AlwaysFails(string name, bool transient = false) =>
        new(name, (_, call) => Task.FromResult(AgentOutcome.Failure($"failure {call}", transient)));

    public static AgentOutcome Success(string key, string content = "content") =>
        AgentOutcome.Success(new Artifact { Key = key, Kind = "test", ProducedBy = string.Empty, Content = content });

    public Task<AgentOutcome> ExecuteAsync(AgentContext context, CancellationToken cancellationToken) =>
        behaviour(context, Interlocked.Increment(ref _calls));

    public Task CompensateAsync(AgentContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _compensations);
        return Task.CompletedTask;
    }
}
