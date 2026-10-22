using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Orchestrator.Agents.Tools;
using Orchestrator.Core;
using Orchestrator.Core.Abstractions;

namespace Orchestrator.Agents.Verification;

internal static class TestProjects
{
    public static IReadOnlyList<string> Find(AgentContext context, string? marker = null) =>
        [.. context.Workspace.Files("*.csproj")
            .Where(p => p.StartsWith("tests/", StringComparison.Ordinal))
            .Where(p => marker is null || p.Contains(marker, StringComparison.OrdinalIgnoreCase))];

    public static string Tail(string text, int lines = 25) =>
        string.Join('\n', text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(lines));
}

public sealed partial class BuildVerifierAgent : IAgent
{
    public string Name => AgentNames.BuildVerifier;

    public async Task<AgentOutcome> ExecuteAsync(AgentContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var projects = TestProjects.Find(context);
        if (projects.Count == 0)
        {
            return AgentOutcome.Failure("No test projects found in the workspace; nothing to verify.");
        }

        var md = new StringBuilder("## Build verification\n\n");
        var succeeded = true;
        var warnings = 0;
        foreach (var project in projects)
        {
            var result = await ToolGateway.DotnetAsync(context, ["build", project, "--nologo"], TimeSpan.FromMinutes(10), cancellationToken);
            warnings += Warnings().Count(result.StandardOutput);
            succeeded &= result.Succeeded;
            md.AppendLine($"### `{project}` — {(result.Succeeded ? "succeeded" : "FAILED")} in {result.Duration.TotalSeconds:F1}s");
            md.AppendLine("```").AppendLine(TestProjects.Tail(result.StandardOutput + result.StandardError, result.Succeeded ? 4 : 30)).AppendLine("```");
        }

        md.AppendLine($"\n**Result:** {(succeeded ? "PASSED" : "FAILED")}");
        return new AgentOutcome
        {
            Succeeded = true,
            Artifacts = [Produce.Markdown(ArtifactKeys.BuildReport, "build", md.ToString())],
            Metrics = new Dictionary<string, string>
            {
                ["build"] = succeeded ? "succeeded" : "failed",
                ["warnings"] = warnings.ToString(CultureInfo.InvariantCulture),
            },
        };
    }

    [GeneratedRegex(@"warning [A-Z]+\d+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Warnings();
}

public abstract partial class TestRunnerAgentBase : IAgent
{
    public abstract string Name { get; }

    protected abstract IReadOnlyList<string> ExtraArguments { get; }

    public async Task<AgentOutcome> ExecuteAsync(AgentContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var integration = context.Node.Id.Contains("integration", StringComparison.OrdinalIgnoreCase);
        var key = integration ? ArtifactKeys.IntegrationTestReport : ArtifactKeys.UnitTestReport;
        var projects = TestProjects.Find(context, integration ? "IntegrationTests" : "UnitTests");
        if (projects.Count == 0)
        {
            return AgentOutcome.Failure($"No {(integration ? "integration" : "unit")} test project found.");
        }

        int passed = 0, failed = 0, skipped = 0;
        var md = new StringBuilder($"## {(integration ? "Integration" : "Unit")} tests\n\n");
        foreach (var project in projects)
        {
            var result = await ToolGateway.DotnetAsync(context, ["test", project, "--no-build", "--nologo", .. ExtraArguments], TimeSpan.FromMinutes(10), cancellationToken);
            var summary = Summary().Match(result.StandardOutput);
            if (!summary.Success)
            {
                return AgentOutcome.Failure($"Test run for {project} produced no summary (exit {result.ExitCode}): {TestProjects.Tail(result.StandardError, 5)}", transient: true);
            }

            failed += int.Parse(summary.Groups["failed"].Value, CultureInfo.InvariantCulture);
            passed += int.Parse(summary.Groups["passed"].Value, CultureInfo.InvariantCulture);
            skipped += int.Parse(summary.Groups["skipped"].Value, CultureInfo.InvariantCulture);
            md.AppendLine($"- `{project}`: {summary.Value.Trim()}");
            if (!result.Succeeded)
            {
                md.AppendLine("```").AppendLine(TestProjects.Tail(result.StandardOutput, 30)).AppendLine("```");
            }
        }

        md.AppendLine($"\n**Passed:** {passed} · **Failed:** {failed} · **Skipped:** {skipped}  ");
        md.AppendLine($"**Executed by:** {Name}  ");
        md.AppendLine($"**Result:** {(failed == 0 && passed > 0 ? "PASSED" : "FAILED")}");

        return new AgentOutcome
        {
            Succeeded = true,
            Artifacts = [Produce.Markdown(key, "test-report", md.ToString())],
            Metrics = new Dictionary<string, string>
            {
                ["passed"] = passed.ToString(CultureInfo.InvariantCulture),
                ["failed"] = failed.ToString(CultureInfo.InvariantCulture),
                ["skipped"] = skipped.ToString(CultureInfo.InvariantCulture),
            },
        };
    }

    [GeneratedRegex(@"(Passed|Failed)!\s+-\s+Failed:\s+(?<failed>\d+),\s+Passed:\s+(?<passed>\d+),\s+Skipped:\s+(?<skipped>\d+)[^\n]*", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Summary();
}

public sealed class TestRunnerAgent : TestRunnerAgentBase
{
    public override string Name => AgentNames.TestRunner;

    protected override IReadOnlyList<string> ExtraArguments => [];
}

public sealed class DiagnosticTestRunnerAgent : TestRunnerAgentBase
{
    public override string Name => AgentNames.DiagnosticTestRunner;

    protected override IReadOnlyList<string> ExtraArguments => ["--blame-hang", "--blame-hang-timeout", "2m", "--", "RunConfiguration.DisableParallelization=true"];
}

public sealed partial class SecurityReviewerAgent : IAgent
{
    public string Name => AgentNames.SecurityReviewer;

    public async Task<AgentOutcome> ExecuteAsync(AgentContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var findings = new List<(string Severity, string Finding)>();
        var md = new StringBuilder("## Security review\n\n### Dependency audit\n");

        foreach (var project in TestProjects.Find(context))
        {
            var result = await ToolGateway.DotnetAsync(context, ["list", project, "package", "--vulnerable", "--include-transitive"], TimeSpan.FromMinutes(5), cancellationToken);
            foreach (Match match in VulnerablePackage().Matches(result.StandardOutput))
            {
                findings.Add((match.Groups["severity"].Value, $"{match.Groups["package"].Value} {match.Groups["version"].Value} ({project})"));
            }

            md.AppendLine($"- `{project}`: {(VulnerablePackage().IsMatch(result.StandardOutput) ? "vulnerable packages found" : "no known vulnerable packages")}");
        }

        md.AppendLine().AppendLine("### Static analysis of changed code");
        if (context.Workspace is GitWorkspace workspace && !ToolGateway.IsSimulated(context))
        {
            var baseline = await workspace.BaselineCommitAsync(cancellationToken);
            var changes = await workspace.DiffAsync(baseline, cancellationToken);
            foreach (var file in changes.Files)
            {
                foreach (var (pattern, severity, label) in StaticRules)
                {
                    if (pattern.IsMatch(file.AddedContent))
                    {
                        findings.Add((severity, $"{label} in {file.Path}"));
                    }
                }
            }

            md.AppendLine($"- Scanned {changes.Files.Count} changed file(s) against {StaticRules.Length} rules");
        }
        else
        {
            md.AppendLine("- Skipped in simulated execution");
        }

        int Count(string severity) => findings.Count(f => f.Severity.Equals(severity, StringComparison.OrdinalIgnoreCase));
        md.AppendLine().AppendLine("### Findings");
        foreach (var (severity, finding) in findings.DefaultIfEmpty(("None", "No findings")))
        {
            md.AppendLine($"- **{severity}**: {finding}");
        }

        md.AppendLine().AppendLine($"**Critical:** {Count("Critical")} · **High:** {Count("High")} · **Medium:** {Count("Moderate") + Count("Medium")}");
        md.AppendLine($"**Result:** {(Count("Critical") == 0 ? "PASSED" : "FAILED")}");

        return new AgentOutcome
        {
            Succeeded = true,
            Artifacts = [Produce.Markdown(ArtifactKeys.SecurityReport, "security", md.ToString())],
            Metrics = new Dictionary<string, string>
            {
                ["critical"] = Count("Critical").ToString(CultureInfo.InvariantCulture),
                ["high"] = Count("High").ToString(CultureInfo.InvariantCulture),
            },
        };
    }

    private static readonly (Regex Pattern, string Severity, string Label)[] StaticRules =
    [
        (new Regex(@"AKIA[0-9A-Z]{16}|-----BEGIN [A-Z ]*PRIVATE KEY-----|sk-ant-[A-Za-z0-9_\-]{20,}", RegexOptions.None, TimeSpan.FromSeconds(1)), "Critical", "Hard-coded credential"),
        (new Regex(@"FromSqlRaw\s*\(\s*\$|ExecuteSqlRaw\s*\(\s*\$", RegexOptions.None, TimeSpan.FromSeconds(1)), "High", "Interpolated raw SQL"),
        (new Regex(@"AllowAnyOrigin\(\)", RegexOptions.None, TimeSpan.FromSeconds(1)), "High", "Permissive CORS"),
        (new Regex(@"ServerCertificateCustomValidationCallback\s*=", RegexOptions.None, TimeSpan.FromSeconds(1)), "High", "TLS validation override"),
        (new Regex(@"\bMD5\b|\bSHA1\b", RegexOptions.None, TimeSpan.FromSeconds(1)), "Medium", "Weak hash algorithm"),
    ];

    [GeneratedRegex(@">\s+(?<package>[\w\.\-]+)\s+(?<version>\d[\w\.\-]*)\s+(?<severity>Critical|High|Moderate|Low)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VulnerablePackage();
}
