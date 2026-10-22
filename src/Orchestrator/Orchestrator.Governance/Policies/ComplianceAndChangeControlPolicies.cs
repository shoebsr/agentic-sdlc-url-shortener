using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Model;

namespace Orchestrator.Governance.Policies;

public sealed partial class PersonalDataPolicy : IPolicy
{
    public string Id => "compliance.personal-data";

    public PolicyVerdict Evaluate(PolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var findings = SecretLeakPolicy.Changes(context)
            .Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => PersonalDataInLogs().IsMatch(f.AddedContent) || RawAddressPersisted().IsMatch(f.AddedContent))
            .Select(f => f.Path)
            .ToList();

        return findings.Count == 0
            ? PolicyVerdict.Allow(Id)
            : new PolicyVerdict(Id, PolicyEffect.RequireApproval,
                $"Possible personal data (IP/e-mail) logged or stored without hashing in {string.Join(", ", findings)}", RiskLevel.High);
    }

    [GeneratedRegex(@"Log\w*\([^;]*(RemoteIpAddress|Email|SSN|AccountNumber)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PersonalDataInLogs();

    [GeneratedRegex(@"(IpAddress|ClientIp)\s*=\s*[^;]*RemoteIpAddress(?![^;]*Hash)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RawAddressPersisted();
}

public sealed class ProtectedPathPolicy(IOptions<GovernanceOptions> options) : IPolicy
{
    public string Id => "change-control.protected-paths";

    public PolicyVerdict Evaluate(PolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var touched = SecretLeakPolicy.Changes(context)
            .Where(f => options.Value.ProtectedPaths.Any(p => f.Path.Replace('\\', '/').Contains(p, StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.Path)
            .ToList();

        return touched.Count == 0
            ? PolicyVerdict.Allow(Id)
            : new PolicyVerdict(Id, PolicyEffect.RequireApproval, $"Protected areas modified (schema, pipeline, security or deployment): {string.Join(", ", touched.Take(6))}{(touched.Count > 6 ? $" +{touched.Count - 6} more" : string.Empty)}", RiskLevel.High);
    }
}

public sealed class ChangeBudgetPolicy(IOptions<GovernanceOptions> options) : IPolicy
{
    public string Id => "change-control.change-budget";

    public PolicyVerdict Evaluate(PolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var changes = context.Outcome.Changes;
        if (changes is null || changes.Files.Count == 0)
        {
            return PolicyVerdict.Allow(Id);
        }

        var budget = options.Value;
        var churn = changes.AddedLines + changes.RemovedLines;
        if (changes.Files.Count <= budget.MaxFilesWithoutReview && churn <= budget.MaxLinesWithoutReview)
        {
            return PolicyVerdict.Allow(Id);
        }

        var risk = churn > budget.MaxLinesWithoutReview * 4 ? RiskLevel.High : RiskLevel.Medium;
        return new PolicyVerdict(Id, PolicyEffect.RequireApproval,
            $"Change of {changes.Files.Count} files / {churn} lines exceeds the unreviewed budget ({budget.MaxFilesWithoutReview} files / {budget.MaxLinesWithoutReview} lines)", risk);
    }
}

public sealed partial class DependencyChangePolicy : IPolicy
{
    public string Id => "change-control.dependencies";

    public PolicyVerdict Evaluate(PolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var packages = SecretLeakPolicy.Changes(context)
            .Where(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".props", StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => PackageReference().Matches(f.AddedContent).Select(m => m.Groups["name"].Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return packages.Count == 0 || context.Run.Kind == ScenarioKind.Greenfield
            ? PolicyVerdict.Allow(Id)
            : new PolicyVerdict(Id, PolicyEffect.RequireApproval, $"New third-party dependencies need supply-chain review: {string.Join(", ", packages)}", RiskLevel.Medium);
    }

    [GeneratedRegex(@"PackageReference\s+Include=""(?<name>[^""]+)""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PackageReference();
}

public sealed partial class DestructiveSchemaPolicy : IPolicy
{
    public string Id => "change-control.destructive-schema";

    public PolicyVerdict Evaluate(PolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var migrations = SecretLeakPolicy.Changes(context)
            .Where(f => f.Path.Contains("Migrations", StringComparison.OrdinalIgnoreCase) && !f.Path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (migrations.Any(f => DropDatabase().IsMatch(ForwardPath(f.AddedContent))))
        {
            return new PolicyVerdict(Id, PolicyEffect.Deny, "Migration drops a database or truncates data", RiskLevel.Critical);
        }

        var destructive = migrations.Where(f => DropObject().IsMatch(ForwardPath(f.AddedContent))).Select(f => f.Path).ToList();
        return destructive.Count == 0
            ? PolicyVerdict.Allow(Id)
            : new PolicyVerdict(Id, PolicyEffect.RequireApproval, $"Migration drops schema objects (data loss risk): {string.Join(", ", destructive)}", RiskLevel.Critical);
    }

    private static string ForwardPath(string migration)
    {
        var down = migration.IndexOf("void Down(", StringComparison.Ordinal);
        return down < 0 ? migration : migration[..down];
    }

    [GeneratedRegex(@"(DROP\s+DATABASE)|(TRUNCATE\s+TABLE)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DropDatabase();

    [GeneratedRegex(@"\.(DropTable|DropColumn|DropIndex)\(", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DropObject();
}
