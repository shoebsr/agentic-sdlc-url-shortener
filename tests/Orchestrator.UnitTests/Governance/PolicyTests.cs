using Microsoft.Extensions.Options;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Model;
using Orchestrator.Governance;
using Orchestrator.Governance.Policies;
using static Orchestrator.UnitTests.Support.EngineHarness;

namespace Orchestrator.UnitTests.Governance;

public class PolicyTests
{
    private static readonly IOptions<GovernanceOptions> Options = Microsoft.Extensions.Options.Options.Create(new GovernanceOptions());

    private static PolicyContext Context(params FileChange[] files) => Context(ScenarioKind.Brownfield, files);

    private static PolicyContext Context(ScenarioKind kind, params FileChange[] files)
    {
        var run = NewRun(Node("impl", "impl"));
        run = new WorkflowRun
        {
            Id = run.Id, Title = run.Title, Kind = kind, Requirement = run.Requirement, CreatedAt = run.CreatedAt, Nodes = run.Nodes,
        };
        return new PolicyContext(run, run.Nodes[0], new AgentOutcome { Succeeded = true, Changes = new ChangeSet(files) });
    }

    private static FileChange Added(string path, string content, int lines = 1) => new(path, FileChangeKind.Added, lines, 0, content);

    [Theory]
    [InlineData("\"AccessKeyId\": \"AKIAIOSFODNN7EXAMPLE\"")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    [InlineData("var key = \"sk-ant-api03-abcdefghijklmnopqrstuvwx\";")]
    [InlineData("token = ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("password = \"SuperSecret123\"")]
    public void Secret_leak_policy_denies_committed_credentials(string content)
    {
        var verdict = new SecretLeakPolicy().Evaluate(Context(Added("src/app.cs", content)));

        Assert.Equal(PolicyEffect.Deny, verdict.Effect);
        Assert.Equal(RiskLevel.Critical, verdict.Risk);
        Assert.DoesNotContain("EXAMPLE", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_leak_policy_allows_configuration_references_to_secrets()
    {
        var verdict = new SecretLeakPolicy().Evaluate(Context(Added("src/app.cs", "var key = configuration[\"Analytics:FingerprintKey\"];")));

        Assert.Equal(PolicyEffect.Allow, verdict.Effect);
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("src/../../etc/passwd")]
    public void Workspace_boundary_policy_denies_escaping_paths(string path)
    {
        Assert.Equal(PolicyEffect.Deny, new WorkspaceBoundaryPolicy().Evaluate(Context(Added(path, "x"))).Effect);
    }

    [Theory]
    [InlineData("db.Database.ExecuteSqlRaw($\"DELETE FROM T WHERE Id = {id}\")")]
    [InlineData("handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;")]
    [InlineData("policy.AllowAnyOrigin();")]
    [InlineData("var formatter = new BinaryFormatter();")]
    public void Insecure_code_policy_requires_security_review(string content)
    {
        var verdict = new InsecureCodePolicy().Evaluate(Context(Added("src/Service.cs", content)));

        Assert.Equal(PolicyEffect.RequireApproval, verdict.Effect);
        Assert.Equal(RiskLevel.High, verdict.Risk);
    }

    [Fact]
    public void Personal_data_policy_flags_logging_client_addresses()
    {
        var verdict = new PersonalDataPolicy().Evaluate(Context(Added("src/Service.cs", "logger.LogInformation(\"from {Ip}\", http.Connection.RemoteIpAddress);")));

        Assert.Equal(PolicyEffect.RequireApproval, verdict.Effect);
    }

    [Theory]
    [InlineData("src/App/Persistence/Migrations/0002_Add.cs")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData("deploy/docker-compose.yml")]
    [InlineData("Directory.Build.props")]
    public void Protected_paths_require_approval(string path)
    {
        var verdict = new ProtectedPathPolicy(Options).Evaluate(Context(Added(path, "x")));

        Assert.Equal(PolicyEffect.RequireApproval, verdict.Effect);
        Assert.Equal(RiskLevel.High, verdict.Risk);
    }

    [Fact]
    public void Ordinary_source_changes_are_not_protected()
    {
        Assert.Equal(PolicyEffect.Allow, new ProtectedPathPolicy(Options).Evaluate(Context(Added("src/App/Domain/Link.cs", "x"))).Effect);
    }

    [Fact]
    public void Change_budget_escalates_large_changes_and_scales_risk_with_size()
    {
        var policy = new ChangeBudgetPolicy(Options);
        var small = policy.Evaluate(Context(Added("a.cs", "x", 50)));
        var medium = policy.Evaluate(Context(Enumerable.Range(0, 13).Select(i => Added($"f{i}.cs", "x", 10)).ToArray()));
        var huge = policy.Evaluate(Context(Added("big.cs", "x", 2000)));

        Assert.Equal(PolicyEffect.Allow, small.Effect);
        Assert.Equal((PolicyEffect.RequireApproval, RiskLevel.Medium), (medium.Effect, medium.Risk));
        Assert.Equal((PolicyEffect.RequireApproval, RiskLevel.High), (huge.Effect, huge.Risk));
    }

    [Fact]
    public void New_dependencies_in_brownfield_need_supply_chain_review()
    {
        var change = Added("src/App/App.csproj", "<PackageReference Include=\"Some.Library\" Version=\"1.0.0\" />");

        Assert.Equal(PolicyEffect.RequireApproval, new DependencyChangePolicy().Evaluate(Context(change)).Effect);
        Assert.Equal(PolicyEffect.Allow, new DependencyChangePolicy().Evaluate(Context(ScenarioKind.Greenfield, change)).Effect);
    }

    [Fact]
    public void Destructive_schema_change_in_forward_migration_requires_critical_approval()
    {
        var verdict = new DestructiveSchemaPolicy().Evaluate(Context(Added(
            "src/Persistence/Migrations/0003_Drop.cs",
            "protected override void Up(MigrationBuilder m) { m.DropColumn(name: \"Legacy\", table: \"Links\"); }")));

        Assert.Equal((PolicyEffect.RequireApproval, RiskLevel.Critical), (verdict.Effect, verdict.Risk));
    }

    [Fact]
    public void Drop_in_the_down_migration_is_not_treated_as_destructive()
    {
        var verdict = new DestructiveSchemaPolicy().Evaluate(Context(Added(
            "src/Persistence/Migrations/0002_Add.cs",
            "protected override void Up(MigrationBuilder m) { m.AddColumn<int>(name: \"MaxClicks\"); }\n" +
            "protected override void Down(MigrationBuilder m) { m.DropColumn(name: \"MaxClicks\"); }")));

        Assert.Equal(PolicyEffect.Allow, verdict.Effect);
    }

    [Fact]
    public void Dropping_a_database_is_denied()
    {
        var verdict = new DestructiveSchemaPolicy().Evaluate(Context(Added(
            "src/Persistence/Migrations/0004_Reset.cs", "protected override void Up(MigrationBuilder m) { m.Sql(\"DROP DATABASE Shortener\"); }")));

        Assert.Equal(PolicyEffect.Deny, verdict.Effect);
    }

    [Fact]
    public void Policy_engine_returns_a_verdict_from_every_policy()
    {
        var engine = new PolicyEngine([new SecretLeakPolicy(), new ProtectedPathPolicy(Options)]);

        var verdicts = engine.Evaluate(Context(Added("src/Persistence/Migrations/1.cs", "AKIAIOSFODNN7EXAMPLE")));

        Assert.Equal(2, verdicts.Count);
        Assert.Contains(verdicts, v => v.Effect == PolicyEffect.Deny);
        Assert.Contains(verdicts, v => v.Effect == PolicyEffect.RequireApproval);
    }
}
