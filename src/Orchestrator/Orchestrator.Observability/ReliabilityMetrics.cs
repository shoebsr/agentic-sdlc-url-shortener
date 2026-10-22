using System.Diagnostics.Metrics;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Model;

namespace Orchestrator.Observability;

public sealed class ReliabilityMetrics : IReliabilityMetrics, IDisposable
{
    public const string MeterName = "AgenticSdlc.Orchestrator";

    private readonly Lock _gate = new();
    private readonly Meter _meter = new(MeterName, "1.0");
    private readonly Counter<long> _nodeExecutions;
    private readonly Counter<long> _nodeFailures;
    private readonly Counter<long> _retries;
    private readonly Counter<long> _fallbacks;
    private readonly Counter<long> _rollbacks;
    private readonly Histogram<double> _nodeDuration;
    private readonly Histogram<double> _runDuration;
    private readonly Histogram<double> _recovery;

    private readonly List<double> _endToEnd = [];
    private readonly List<double> _recoveries = [];
    private readonly List<double> _approvalWaits = [];
    private readonly Dictionary<string, (int Executions, int Failures, double TotalSeconds, int Timed)> _stages = new(StringComparer.Ordinal);
    private int _runsCompleted;
    private int _runsSucceeded;
    private int _executions;
    private int _failures;
    private int _retryCount;
    private int _fallbackCount;
    private int _rollbackCount;

    public ReliabilityMetrics()
    {
        _nodeExecutions = _meter.CreateCounter<long>("sdlc.node.executions");
        _nodeFailures = _meter.CreateCounter<long>("sdlc.node.failures");
        _retries = _meter.CreateCounter<long>("sdlc.node.retries");
        _fallbacks = _meter.CreateCounter<long>("sdlc.node.fallbacks");
        _rollbacks = _meter.CreateCounter<long>("sdlc.node.rollbacks");
        _nodeDuration = _meter.CreateHistogram<double>("sdlc.node.duration", "s");
        _runDuration = _meter.CreateHistogram<double>("sdlc.run.duration", "s");
        _recovery = _meter.CreateHistogram<double>("sdlc.recovery.time", "s");
    }

    public void NodeStarted(WorkflowRun run, WorkflowNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _nodeExecutions.Add(1, Tag(node));
        lock (_gate)
        {
            _executions++;
            var stage = StageOf(node);
            _stages[stage] = _stages.GetValueOrDefault(stage) with { Executions = _stages.GetValueOrDefault(stage).Executions + 1 };
        }
    }

    public void NodeSucceeded(WorkflowRun run, WorkflowNode node, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(node);
        _nodeDuration.Record(duration.TotalSeconds, Tag(node));
        lock (_gate)
        {
            var stage = _stages.GetValueOrDefault(StageOf(node));
            _stages[StageOf(node)] = stage with { TotalSeconds = stage.TotalSeconds + duration.TotalSeconds, Timed = stage.Timed + 1 };
        }
    }

    public void NodeFailed(WorkflowRun run, WorkflowNode node, string reason)
    {
        ArgumentNullException.ThrowIfNull(node);
        _nodeFailures.Add(1, Tag(node));
        lock (_gate)
        {
            _failures++;
            var stage = _stages.GetValueOrDefault(StageOf(node));
            _stages[StageOf(node)] = stage with { Failures = stage.Failures + 1 };
        }
    }

    public void Retried(WorkflowRun run, WorkflowNode node, int attempt)
    {
        _retries.Add(1, Tag(node));
        lock (_gate)
        {
            _retryCount++;
        }
    }

    public void FellBack(WorkflowRun run, WorkflowNode node)
    {
        _fallbacks.Add(1, Tag(node));
        lock (_gate)
        {
            _fallbackCount++;
        }
    }

    public void RolledBack(WorkflowRun run, WorkflowNode node)
    {
        _rollbacks.Add(1, Tag(node));
        lock (_gate)
        {
            _rollbackCount++;
        }
    }

    public void Recovered(WorkflowRun run, WorkflowNode node, TimeSpan timeToRecover)
    {
        _recovery.Record(timeToRecover.TotalSeconds, Tag(node));
        lock (_gate)
        {
            _recoveries.Add(timeToRecover.TotalSeconds);
        }
    }

    public void ApprovalWaited(WorkflowRun run, TimeSpan waited)
    {
        lock (_gate)
        {
            _approvalWaits.Add(waited.TotalSeconds);
        }
    }

    public void RunCompleted(WorkflowRun run, TimeSpan endToEnd)
    {
        ArgumentNullException.ThrowIfNull(run);
        _runDuration.Record(endToEnd.TotalSeconds, new KeyValuePair<string, object?>("status", run.Status.ToString()));
        lock (_gate)
        {
            _runsCompleted++;
            if (run.Status == RunStatus.Succeeded)
            {
                _runsSucceeded++;
            }

            _endToEnd.Add(endToEnd.TotalSeconds);
        }
    }

    public MetricsSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new MetricsSnapshot(
                _runsCompleted,
                _runsSucceeded,
                Ratio(_runsSucceeded, _runsCompleted),
                _executions,
                _failures,
                Ratio(_executions - _failures, _executions),
                _retryCount,
                _fallbackCount,
                _rollbackCount,
                Ratio(_retryCount, _executions),
                Ratio(_rollbackCount, _runsCompleted),
                Mean(_recoveries),
                Mean(_endToEnd),
                Percentile(_endToEnd, 0.95),
                Mean(_approvalWaits),
                _stages.ToDictionary(
                    kv => kv.Key,
                    kv => new StageMetrics(kv.Value.Executions, kv.Value.Failures, kv.Value.Timed == 0 ? 0 : kv.Value.TotalSeconds / kv.Value.Timed),
                    StringComparer.Ordinal));
        }
    }

    public void Dispose() => _meter.Dispose();

    private static string StageOf(WorkflowNode node) => node.Stage.ToString();

    private static KeyValuePair<string, object?> Tag(WorkflowNode node) => new("stage", node.Stage.ToString());

    private static double Ratio(int numerator, int denominator) => denominator == 0 ? 0 : Math.Round(numerator * 100.0 / denominator, 2);

    private static double Mean(List<double> values) => values.Count == 0 ? 0 : Math.Round(values.Average(), 3);

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return Math.Round(sorted[Math.Clamp(index, 0, sorted.Count - 1)], 3);
    }
}
