using System.Text.RegularExpressions;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Model;

namespace Orchestrator.Governance.Policies;

public sealed partial class SecretLeakPolicy : IPolicy
{
    public string Id => "security.secret-leak";

    public PolicyVerdict Evaluate(PolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var hits = Changes(context)
            .SelectMany(file => SecretPatterns().Matches(file.AddedContent).Select(m => $"{file.Path} ({Describe(m.Value)})"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return hits.Count == 0
            ? PolicyVerdict.Allow(Id)
            : new PolicyVerdict(Id, PolicyEffect.Deny, $"Potential credential committed: {string.Join(", ", hits)}", RiskLevel.Critical);
    }

    internal static IEnumerable<FileChange> Changes(PolicyContext context) => context.Outcome.Changes?.Files ?? [];

    private static string Describe(string match) => match.Length <= 12 ? "secret-like token" : match[..8] + "…";

    [GeneratedRegex(
        @"(AKIA[0-9A-Z]{16})|(-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----)|(sk-ant-[A-Za-z0-9_\-]{20,})|(sk-[A-Za-z0-9]{32,})|(ghp_[A-Za-z0-9]{36})|((password|pwd|secret|api[_-]?key)\s*[=:]\s*""[^""\s]{8,}"")",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SecretPatterns();
}

public sealed partial class InsecureCodePolicy : IPolicy
{
    public string Id => "security.insecure-code";

    public PolicyVerdict Evaluate(PolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var findings = SecretLeakPolicy.Changes(context)
            .Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => InsecurePatterns().Matches(f.AddedContent).Select(m => $"{f.Path}: {m.Value.Trim()}"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return findings.Count == 0
            ? PolicyVerdict.Allow(Id)
            : new PolicyVerdict(Id, PolicyEffect.RequireApproval, $"Security-sensitive constructs need review: {string.Join("; ", findings)}", RiskLevel.High);
    }

    [GeneratedRegex(
        @"(FromSqlRaw\s*\(\s*\$)|(ExecuteSqlRaw\s*\(\s*\$)|(ServerCertificateCustomValidationCallback\s*=)|(new\s+(MD5|SHA1)CryptoServiceProvider)|(MD5\.Create\(\))|(\bBinaryFormatter\b)|(AllowAnyOrigin\(\))",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InsecurePatterns();
}

public sealed class WorkspaceBoundaryPolicy : IPolicy
{
    public string Id => "security.workspace-boundary";

    public PolicyVerdict Evaluate(PolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var escapes = SecretLeakPolicy.Changes(context)
            .Where(f => Path.IsPathRooted(f.Path) || f.Path.Split('/', '\\').Contains(".."))
            .Select(f => f.Path)
            .ToList();

        return escapes.Count == 0
            ? PolicyVerdict.Allow(Id)
            : new PolicyVerdict(Id, PolicyEffect.Deny, $"Changes escape the sandboxed workspace: {string.Join(", ", escapes)}", RiskLevel.Critical);
    }
}
