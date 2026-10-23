using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Agents.Preview;
using Orchestrator.Agents.Workflows;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Engine;
using Orchestrator.Core.Model;

namespace Orchestrator.Host.Api;

public static class RunEndpoints
{
    public const string ActorHeader = "X-Actor";

    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapOrchestratorApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api").WithTags("Orchestrator");

        api.MapGet("/scenarios", (ScenarioCatalog catalog) => TypedResults.Ok(catalog.List()));

        api.MapGet("/runs", async (IRunStore store, CancellationToken ct) =>
            TypedResults.Ok((await store.ListAsync(ct)).Select(RunSummary.From)));

        api.MapPost("/runs", StartAsync);

        api.MapGet("/runs/{id}", async (string id, IRunStore store, RunCoordinator coordinator, CancellationToken ct) =>
            await store.LoadAsync(id, ct) is { } run
                ? Results.Ok(RunView.From(run, coordinator.IsExecuting(id)))
                : Results.NotFound());

        api.MapGet("/runs/{id}/artifacts/{key}", async (string id, string key, IRunStore store, CancellationToken ct) =>
            await store.LoadAsync(id, ct) is { } run && run.FindArtifact(key) is { } artifact
                ? Results.Ok(artifact)
                : Results.NotFound());

        api.MapGet("/runs/{id}/audit", async (string id, IAuditLog audit, CancellationToken ct) =>
            TypedResults.Ok(await audit.ReadAsync(id, ct)));

        api.MapGet("/runs/{id}/events", StreamAsync);

        api.MapPost("/runs/{id}/approvals/{approvalId}", async (
            string id, string approvalId, ApprovalDecision decision, [FromHeader(Name = ActorHeader)] string? actor, RunCoordinator coordinator) =>
            ToResult(await coordinator.ApproveAsync(id, approvalId, actor ?? string.Empty, decision.Approve, decision.Comment)));

        api.MapPost("/runs/{id}/clarifications", async (
            string id, Dictionary<string, string> answers, [FromHeader(Name = ActorHeader)] string? actor, RunCoordinator coordinator) =>
            ToResult(await coordinator.ClarifyAsync(id, answers, actor ?? "requester")));

        api.MapPost("/runs/{id}/requirement", async (
            string id, RequirementAmendment amendment, [FromHeader(Name = ActorHeader)] string? actor, RunCoordinator coordinator) =>
            ToResult(await coordinator.AmendRequirementAsync(id, amendment.Requirement, actor ?? "requester", amendment.Reason ?? "Requirement changed")));

        api.MapPost("/runs/{id}/halt", async (string id, [FromHeader(Name = ActorHeader)] string? actor, RunCoordinator coordinator) =>
            ToResult(await coordinator.HaltAsync(id, actor ?? "operator", "Manual safe-stop")));

        api.MapPost("/runs/{id}/resume", async (string id, RunCoordinator coordinator, CancellationToken ct) =>
            ToResult(await coordinator.ResumeAsync(id, ct)));

        api.MapGet("/runs/{id}/preview", (string id, PreviewEnvironments previews) =>
            previews.Find(id) is { } preview ? Results.Ok(PreviewView.From(preview)) : Results.NotFound());

        api.MapDelete("/runs/{id}/preview", async (string id, PreviewEnvironments previews) =>
        {
            await previews.StopAsync(id);
            return Results.NoContent();
        });

        api.MapGet("/previews", (PreviewEnvironments previews) => TypedResults.Ok(previews.Active.Select(PreviewView.From)));

        api.MapGet("/control/kill-switch", (IKillSwitch killSwitch) => TypedResults.Ok(new { engaged = killSwitch.IsEngaged, reason = killSwitch.Reason }));

        api.MapPost("/control/kill-switch", async (KillSwitchRequest request, [FromHeader(Name = ActorHeader)] string? actor, RunCoordinator coordinator) =>
        {
            if (request.Engage)
            {
                await coordinator.EngageKillSwitchAsync($"Engaged by {actor ?? "operator"}: {request.Reason ?? "no reason given"}");
            }
            else
            {
                coordinator.ReleaseKillSwitch();
            }

            return TypedResults.Ok(new { engaged = request.Engage });
        });

        api.MapGet("/metrics", (IReliabilityMetrics metrics) => TypedResults.Ok(metrics.Snapshot()));

        api.MapGet("/audit/verify", async (IAuditLog audit, CancellationToken ct) => TypedResults.Ok(await audit.VerifyAsync(ct)));

        api.MapGet("/approvers", (Microsoft.Extensions.Options.IOptions<Governance.GovernanceOptions> options) =>
            TypedResults.Ok(options.Value.Approvers.Select(a => new { a.Name, a.Role, MaxRisk = a.MaxRisk.ToString() })));

        return endpoints;
    }

    private static async Task<IResult> StartAsync(RunRequest request, RunFactory factory, RunCoordinator coordinator, CancellationToken ct)
    {
        try
        {
            var run = await coordinator.StartAsync(factory.Create(request), ct);
            return Results.Created($"/api/runs/{run.Id}", RunSummary.From(run));
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: ex.Message);
        }
        catch (WorkflowDefinitionException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: ex.Message);
        }
    }

    private static async Task StreamAsync(string id, HttpContext http, IRunEventPublisher events, CancellationToken ct)
    {
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        await http.Response.WriteAsync(": connected\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
        try
        {
            await foreach (var auditEvent in events.SubscribeAsync(id, ct))
            {
                await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(auditEvent, EventJson)}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static IResult ToResult(CommandResult result) => result.Accepted
        ? Results.Ok(result)
        : Results.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Message);
}

public sealed record ApprovalDecision(bool Approve, string? Comment);

public sealed record PreviewView(string RunId, string BaseUrl, string AppUrl, string SwaggerUrl, int Port, DateTimeOffset StartedAt)
{
    public static PreviewView From(PreviewInfo preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return new PreviewView(preview.RunId, preview.BaseUrl.AbsoluteUri, preview.AppUrl.AbsoluteUri, preview.SwaggerUrl.AbsoluteUri, preview.Port, preview.StartedAt);
    }
}

public sealed record RequirementAmendment(string Requirement, string? Reason);

public sealed record KillSwitchRequest(bool Engage, string? Reason);

public sealed record RunSummary(
    string Id, string Title, ScenarioKind Kind, RunStatus Status, int PlanVersion, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt, int PendingApprovals, int Nodes, int NodesSucceeded)
{
    public static RunSummary From(WorkflowRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new RunSummary(
            run.Id, run.Title, run.Kind, run.Status, run.PlanVersion, run.CreatedAt, run.CompletedAt,
            run.Approvals.Count(a => a.Status == ApprovalStatus.Pending),
            run.Nodes.Count,
            run.Nodes.Count(n => run.StateOf(n.Id).Status == NodeStatus.Succeeded));
    }
}

public sealed record RunView(WorkflowRun Run, IReadOnlyList<string> Order, bool Executing, IReadOnlyList<ArtifactSummary> Artifacts)
{
    public static RunView From(WorkflowRun run, bool executing)
    {
        ArgumentNullException.ThrowIfNull(run);
        var artifacts = run.Artifacts.Values
            .Select(a => new ArtifactSummary(a.Key, a.Kind, a.ProducedBy, a.Version, a.Hash, a.MediaType, a.CreatedAt, a.Content.Length))
            .OrderBy(a => a.CreatedAt)
            .ToList();
        var summary = new WorkflowRun
        {
            Id = run.Id,
            Title = run.Title,
            Kind = run.Kind,
            Requirement = run.Requirement,
            InitiatedBy = run.InitiatedBy,
            ScenarioId = run.ScenarioId,
            Status = run.Status,
            PlanVersion = run.PlanVersion,
            CreatedAt = run.CreatedAt,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            HaltReason = run.HaltReason,
            Nodes = run.Nodes,
            NodeStates = run.NodeStates,
            Decisions = run.Decisions,
            Approvals = run.Approvals,
            OpenQuestions = run.OpenQuestions,
            Settings = run.Settings,
        };
        return new RunView(summary, run.Graph().TopologicalOrder(), executing, artifacts);
    }
}

public sealed record ArtifactSummary(
    string Key, string Kind, string ProducedBy, int Version, string Hash, string MediaType, DateTimeOffset CreatedAt, int Length);
