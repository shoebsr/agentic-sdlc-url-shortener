using System.Text.Json;
using Microsoft.Extensions.Options;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Model;
using Orchestrator.Observability;
using static Orchestrator.UnitTests.Support.EngineHarness;

namespace Orchestrator.UnitTests.Observability;

public sealed class AuditAndMetricsTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "sdlc-tests", Guid.NewGuid().ToString("N"), "audit.jsonl");

    private HashChainedAuditLog NewLog() => new(Options.Create(new ObservabilityOptions { AuditFile = _file }));

    private static AuditEvent Event(string runId, string type) => new() { RunId = runId, Type = type, Message = type, At = DateTimeOffset.UtcNow };

    [Fact]
    public async Task Appended_events_form_a_verifiable_chain()
    {
        using var log = NewLog();

        var first = await log.AppendAsync(Event("r1", "run.started"), default);
        var second = await log.AppendAsync(Event("r1", "node.started"), default);

        Assert.Equal(1, first.Sequence);
        Assert.Equal(first.Hash, second.PreviousHash);
        Assert.True((await log.VerifyAsync(default)).Intact);
    }

    [Fact]
    public async Task Editing_a_past_record_is_detected_at_that_sequence()
    {
        using (var log = NewLog())
        {
            for (var i = 0; i < 5; i++)
            {
                await log.AppendAsync(Event("r1", $"event.{i}"), default);
            }
        }

        var lines = await File.ReadAllLinesAsync(_file);
        lines[2] = lines[2].Replace("event.2", "event.tampered", StringComparison.Ordinal);
        await File.WriteAllLinesAsync(_file, lines);

        using var reopened = NewLog();
        var verification = await reopened.VerifyAsync(default);

        Assert.False(verification.Intact);
        Assert.Equal(3, verification.FirstBrokenSequence);
    }

    [Fact]
    public async Task Concurrent_writers_in_separate_instances_keep_one_intact_chain()
    {
        using var a = NewLog();
        using var b = NewLog();

        await Task.WhenAll(
            Task.WhenAll(Enumerable.Range(0, 40).Select(i => a.AppendAsync(Event("ra", $"a.{i}"), default))),
            Task.WhenAll(Enumerable.Range(0, 40).Select(i => b.AppendAsync(Event("rb", $"b.{i}"), default))));

        var verification = await a.VerifyAsync(default);
        var events = await a.ReadAsync(null, default);

        Assert.True(verification.Intact);
        Assert.Equal(80, events.Count);
        Assert.Equal(Enumerable.Range(1, 80).Select(i => (long)i), events.Select(e => e.Sequence));
    }

    [Fact]
    public async Task Events_can_be_read_per_run()
    {
        using var log = NewLog();
        await log.AppendAsync(Event("r1", "x"), default);
        await log.AppendAsync(Event("r2", "y"), default);

        Assert.Single(await log.ReadAsync("r2", default));
    }

    [Fact]
    public async Task Publisher_delivers_only_the_subscribed_runs_events()
    {
        var publisher = new RunEventPublisher();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<string>();
        var reader = Task.Run(async () =>
        {
            await foreach (var e in publisher.SubscribeAsync("r1", cancel.Token))
            {
                received.Add(e.Type);
                if (received.Count == 2)
                {
                    break;
                }
            }
        });

        await Task.Delay(100);
        publisher.Publish(Event("r1", "one"));
        publisher.Publish(Event("r2", "other"));
        publisher.Publish(Event("r1", "two"));
        await reader;

        Assert.Equal(["one", "two"], received);
    }

    [Fact]
    public void Metrics_snapshot_computes_rates_mttr_and_percentiles()
    {
        using var metrics = new ReliabilityMetrics();
        var node = Node("n", "a");
        var succeeded = NewRun(node);
        succeeded.Status = RunStatus.Succeeded;
        var rolledBack = NewRun(node);
        rolledBack.Status = RunStatus.RolledBack;

        for (var i = 0; i < 4; i++)
        {
            metrics.NodeStarted(succeeded, node);
        }

        metrics.NodeFailed(succeeded, node, "boom");
        metrics.Retried(succeeded, node, 1);
        metrics.Recovered(succeeded, node, TimeSpan.FromSeconds(2));
        metrics.Recovered(succeeded, node, TimeSpan.FromSeconds(4));
        metrics.RolledBack(rolledBack, node);
        metrics.RunCompleted(succeeded, TimeSpan.FromSeconds(10));
        metrics.RunCompleted(rolledBack, TimeSpan.FromSeconds(30));

        var snapshot = metrics.Snapshot();

        Assert.Equal(50, snapshot.RunSuccessRate);
        Assert.Equal(75, snapshot.NodeSuccessRate);
        Assert.Equal(25, snapshot.RetryRate);
        Assert.Equal(50, snapshot.RollbackRate);
        Assert.Equal(3, snapshot.MeanTimeToRecoverSeconds);
        Assert.Equal(20, snapshot.MeanEndToEndSeconds);
        Assert.Equal(30, snapshot.P95EndToEndSeconds);
        Assert.Equal(1, snapshot.ByStage["Implementation"].Failures);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_file)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Audit_records_serialize_with_their_hash_fields()
    {
        using var log = NewLog();
        await log.AppendAsync(Event("r1", "x"), default);

        using var json = JsonDocument.Parse((await File.ReadAllLinesAsync(_file))[0]);

        Assert.Equal("GENESIS", json.RootElement.GetProperty("previousHash").GetString());
        Assert.Equal(64, json.RootElement.GetProperty("hash").GetString()!.Length);
    }
}
