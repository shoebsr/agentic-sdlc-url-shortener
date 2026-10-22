using System.Globalization;
using Microsoft.Extensions.Options;
using Orchestrator.Core;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Model;

namespace Orchestrator.Governance;

public static class GateIds
{
    public const string ArtifactPrefix = "artifact:";
    public const string RequirementsUnambiguous = "requirements.unambiguous";
    public const string ChangesProduced = "changes.produced";
    public const string BuildSucceeded = "build.succeeded";
    public const string TestsPassed = "tests.passed";
    public const string NoCriticalFindings = "security.no-critical";
    public const string ApprovalsCleared = "approvals.cleared";
    public const string ReadinessComplete = "readiness.complete";

    public static string Artifact(string key) => ArtifactPrefix + key;
}

public sealed class GateRegistry(IOptions<GovernanceOptions> options) : IGateRegistry
{
    public IGate Resolve(string id) => id switch
    {
        _ when id.StartsWith(GateIds.ArtifactPrefix, StringComparison.Ordinal) => new ArtifactPresentGate(id),
        GateIds.RequirementsUnambiguous => new DelegateGate(id, RequirementsUnambiguous),
        GateIds.ChangesProduced => new DelegateGate(id, ChangesProduced),
        GateIds.BuildSucceeded => new DelegateGate(id, c => MetricEquals(c, "build", "succeeded")),
        GateIds.TestsPassed => new DelegateGate(id, c => TestsPassed(c, options.Value.MinimumPassRate)),
        GateIds.NoCriticalFindings => new DelegateGate(id, c => MetricEquals(c, "critical", "0")),
        GateIds.ApprovalsCleared => new DelegateGate(id, ApprovalsCleared),
        GateIds.ReadinessComplete => new DelegateGate(id, c => MetricEquals(c, "blocking", "0")),
        _ => throw new InvalidOperationException($"Unknown gate '{id}'."),
    };

    private static GateResult RequirementsUnambiguous(GateContext context)
    {
        var open = context.Run.OpenQuestions.Count;
        if (open == 0)
        {
            return GateResult.Pass("No open ambiguities");
        }

        return context.Run.FindArtifact(ArtifactKeys.ClarificationAnswers) is not null
            ? GateResult.Pass($"{open} ambiguity(ies) resolved by clarification")
            : GateResult.Fail($"{open} unresolved ambiguity(ies)");
    }

    private static GateResult ChangesProduced(GateContext context)
    {
        var files = context.Outcome?.Changes?.Files.Count ?? 0;
        return files > 0 ? GateResult.Pass($"{files} file(s) changed") : GateResult.Fail("Implementation produced no changes");
    }

    private static GateResult TestsPassed(GateContext context, double minimumPassRate)
    {
        var passed = Metric(context, "passed");
        var failed = Metric(context, "failed");
        var total = passed + failed;
        if (total == 0)
        {
            return GateResult.Fail("No tests were executed");
        }

        var rate = passed * 100.0 / total;
        return rate >= minimumPassRate && failed == 0
            ? GateResult.Pass($"{passed}/{total} tests passed")
            : GateResult.Fail($"{failed} of {total} tests failed ({rate:F1}% pass rate, {minimumPassRate}% required)");
    }

    private static GateResult ApprovalsCleared(GateContext context)
    {
        var pending = context.Run.Approvals.Count(a => a.Status == ApprovalStatus.Pending);
        var governed = context.Run.Approvals.Count(a => a.Status == ApprovalStatus.Approved);
        return pending == 0
            ? GateResult.Pass($"{governed} approval(s) granted, none pending")
            : GateResult.Fail($"{pending} approval(s) still pending");
    }

    private static GateResult MetricEquals(GateContext context, string key, string expected)
    {
        var actual = context.Outcome?.Metrics.GetValueOrDefault(key);
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
            ? GateResult.Pass($"{key} = {actual}")
            : GateResult.Fail($"{key} = {actual ?? "missing"} (expected {expected})");
    }

    private static int Metric(GateContext context, string key) =>
        int.TryParse(context.Outcome?.Metrics.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private sealed class ArtifactPresentGate(string id) : IGate
    {
        public string Id => id;

        public GateResult Evaluate(GateContext context)
        {
            var key = id[GateIds.ArtifactPrefix.Length..];
            var produced = context.Outcome?.Artifacts.FirstOrDefault(a => a.Key == key) ?? context.Run.FindArtifact(key);
            return produced is { Content.Length: > 0 }
                ? GateResult.Pass($"{key} v{produced.Version} present")
                : GateResult.Fail($"{key} missing");
        }
    }

    private sealed class DelegateGate(string id, Func<GateContext, GateResult> evaluate) : IGate
    {
        public string Id => id;

        public GateResult Evaluate(GateContext context) => evaluate(context);
    }
}
