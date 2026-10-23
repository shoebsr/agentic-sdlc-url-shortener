using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Model;

namespace Orchestrator.Core.Engine;

public sealed partial class WorkflowEngine(
    IAgentRegistry agents,
    IGateRegistry gates,
    IPolicyEngine policies,
    IReliabilityMetrics metrics,
    IRunStore store,
    IWorkspaceProvider workspaces,
    IToolbox tools,
    ILlmClient llm,
    IKillSwitch killSwitch,
    RunJournal journal,
    TimeProvider clock,
    IOptions<EngineOptions> options,
    ILogger<WorkflowEngine> logger)
{
    private readonly EngineOptions _options = options.Value;

    public async Task ExecuteAsync(WorkflowRun run, RunInbox inbox, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(inbox);

        await inbox.DrainAsync(run);
        if (run.IsTerminal)
        {
            await store.SaveAsync(run, CancellationToken.None);
            return;
        }

        var resumed = run.StartedAt is not null;
        run.Status = RunStatus.Running;
        run.StartedAt ??= clock.GetUtcNow();
        run.HaltReason = null;
        foreach (var node in run.Nodes)
        {
            run.StateOf(node.Id);
        }

        await journal.RecordAsync(run, resumed ? "run.resumed" : "run.started",
            resumed ? $"Run resumed at plan v{run.PlanVersion}" : $"Run started: {run.Title}", cancellationToken: CancellationToken.None);
        await store.SaveAsync(run, CancellationToken.None);

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = new Dictionary<Task<NodeExecution>, string>();
        var halted = false;
        var failed = false;

        while (true)
        {
            if (await inbox.DrainAsync(run))
            {
                failed |= run.NodeStates.Values.Any(s => s.Status == NodeStatus.Failed);
            }

            if (!halted && (killSwitch.IsEngaged || cancellationToken.IsCancellationRequested))
            {
                halted = true;
                await execution.CancelAsync();
                await journal.RecordAsync(run, "run.halting", killSwitch.Reason ?? "Safe-stop requested; draining in-flight work",
                    cancellationToken: CancellationToken.None);
            }

            if (!halted && !failed)
            {
                failed |= await StartReadyNodesAsync(run, running, execution.Token);
            }

            if (running.Count == 0)
            {
                if (!halted && inbox.HasPending)
                {
                    continue;
                }

                break;
            }

            var inboxSignal = inbox.WaitAsync(CancellationToken.None);
            var completed = await Task.WhenAny([.. running.Keys, inboxSignal]);
            if (completed == inboxSignal || completed is not Task<NodeExecution> nodeTask)
            {
                continue;
            }

            running.Remove(nodeTask);
            failed |= !await ApplyAsync(run, await nodeTask);
            await store.SaveAsync(run, CancellationToken.None);
        }

        await FinalizeAsync(run, halted);
    }

    public async Task<CommandResult> DecideApprovalAsync(
        WorkflowRun run, string approvalId, string approver, bool approve, string? comment, IApprovalAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(authority);

        var index = run.Approvals.FindIndex(a => a.Id == approvalId);
        if (index < 0)
        {
            return CommandResult.Rejected($"Approval '{approvalId}' does not exist.");
        }

        var request = run.Approvals[index];
        if (request.Status != ApprovalStatus.Pending)
        {
            return CommandResult.Rejected($"Approval '{approvalId}' is already {request.Status}.");
        }

        var check = authority.CanApprove(run, request, approver);
        if (!check.Allowed)
        {
            await journal.RecordAsync(run, "approval.denied_authority", check.Reason, request.NodeId, approver);
            return CommandResult.Rejected(check.Reason);
        }

        var now = clock.GetUtcNow();
        run.Approvals[index] = request with
        {
            Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected,
            DecidedBy = approver,
            Comment = comment,
            DecidedAt = now,
        };
        metrics.ApprovalWaited(run, now - request.RequestedAt);

        var node = run.Graph()[request.NodeId];
        var state = run.StateOf(node.Id);
        journal.Decide(run, node.Id, approver, new DecisionDraft(
            $"{(approve ? "Approved" : "Rejected")} {node.Title}",
            string.IsNullOrWhiteSpace(comment) ? request.Reason : comment), OutputHashes(run, state));

        if (approve)
        {
            state.Status = NodeStatus.Succeeded;
            metrics.NodeSucceeded(run, node, (state.CompletedAt ?? now) - (state.StartedAt ?? now));
            await journal.RecordAsync(run, "approval.granted", $"{approver} approved {node.Title}", node.Id, approver);
        }
        else
        {
            Fail(run, node, state, $"Rejected by {approver}: {comment ?? "no comment"}");
            await journal.RecordAsync(run, "approval.rejected", $"{approver} rejected {node.Title}", node.Id, approver);
        }

        if (run.Status is RunStatus.AwaitingApproval)
        {
            run.Status = RunStatus.Running;
        }

        return CommandResult.Ok(approve ? "Approved" : "Rejected");
    }

    public async Task<CommandResult> SubmitClarificationsAsync(WorkflowRun run, IReadOnlyDictionary<string, string> answers, string actor)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(answers);

        var awaiting = run.Nodes.Where(n => n.RequiresHumanInput && run.StateOf(n.Id).Status == NodeStatus.AwaitingInput).ToList();
        if (awaiting.Count == 0)
        {
            return CommandResult.Rejected("No stage is waiting for clarification.");
        }

        var unanswered = run.OpenQuestions.Where(q => !answers.ContainsKey(q.Id) && q.SuggestedDefault is null).Select(q => q.Id).ToList();
        if (unanswered.Count > 0)
        {
            return CommandResult.Rejected($"Answers missing for: {string.Join(", ", unanswered)}.");
        }

        var resolved = run.OpenQuestions.ToDictionary(
            q => q.Id,
            q => answers.TryGetValue(q.Id, out var answer) && !string.IsNullOrWhiteSpace(answer) ? answer : $"{q.SuggestedDefault} (default accepted)");

        StoreArtifact(run, new Artifact
        {
            Key = ArtifactKeys.ClarificationAnswers,
            Kind = "clarification",
            ProducedBy = "human:" + actor,
            MediaType = "application/json",
            Content = System.Text.Json.JsonSerializer.Serialize(resolved),
        }, new Dictionary<string, string>());

        foreach (var node in awaiting)
        {
            run.StateOf(node.Id).Status = NodeStatus.Pending;
        }

        journal.Decide(run, awaiting[0].Id, actor, new DecisionDraft(
            "Clarifications provided", string.Join("; ", resolved.Select(kv => $"{kv.Key}: {kv.Value}"))), new Dictionary<string, string>());
        await journal.RecordAsync(run, "clarification.answered", $"{actor} answered {resolved.Count} question(s)", awaiting[0].Id, actor);

        run.Status = RunStatus.Running;
        return CommandResult.Ok("Clarifications recorded; execution resumes.");
    }

    public async Task<CommandResult> AmendRequirementAsync(WorkflowRun run, string requirement, string actor, string reason)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrWhiteSpace(requirement))
        {
            return CommandResult.Rejected("Requirement text is required.");
        }

        if (run.Status is RunStatus.Succeeded)
        {
            return CommandResult.Rejected("Run has already been released; start a new run for further changes.");
        }

        run.Requirement = requirement.Trim();
        var roots = run.Nodes.Where(n => n.Stage == Stage.Requirements).Select(n => n.Id).ToList();
        run.PlanVersion++;
        foreach (var root in roots)
        {
            await InvalidateAsync(run, root, includeSelf: true, $"Requirement amended by {actor}: {reason}");
        }

        journal.Decide(run, roots.FirstOrDefault() ?? "run", actor,
            new DecisionDraft("Requirement amended; dependent stages re-planned", reason), new Dictionary<string, string>
            {
                ["requirement"] = ContentHash.Of(run.Requirement),
            });
        await journal.RecordAsync(run, "plan.replanned", $"Requirement amended; plan v{run.PlanVersion}", actor: actor);

        if (run.Status is RunStatus.Failed or RunStatus.RolledBack)
        {
            run.CompletedAt = null;
        }

        run.Status = RunStatus.Running;
        return CommandResult.Ok($"Re-planned to v{run.PlanVersion}.");
    }

    private async Task<bool> StartReadyNodesAsync(WorkflowRun run, Dictionary<Task<NodeExecution>, string> running, CancellationToken cancellationToken)
    {
        var graph = run.Graph();
        var failed = false;

        foreach (var id in graph.TopologicalOrder())
        {
            if (running.Count >= _options.MaxParallelNodes)
            {
                break;
            }

            var node = graph[id];
            var state = run.StateOf(id);
            if (state.Status != NodeStatus.Pending || running.ContainsValue(id))
            {
                continue;
            }

            if (!node.DependsOn.All(d => run.StateOf(d).Status is NodeStatus.Succeeded or NodeStatus.Skipped))
            {
                continue;
            }

            var entry = EvaluateGates(run, node, GatePhase.Entry, null);
            await RecordGatesAsync(run, node, entry);
            if (entry.Any(g => !g.Passed))
            {
                Fail(run, node, state, "Entry gate failed: " + string.Join("; ", entry.Where(g => !g.Passed).Select(g => g.Detail)));
                await journal.RecordAsync(run, "node.blocked", state.LastError!, node.Id, cancellationToken: CancellationToken.None);
                failed = true;
                continue;
            }

            if (node.RequiresHumanInput && run.OpenQuestions.Count > 0 && run.FindArtifact(ArtifactKeys.ClarificationAnswers) is null)
            {
                state.Status = NodeStatus.AwaitingInput;
                await journal.RecordAsync(run, "node.awaiting_input",
                    $"{node.Title} needs human clarification ({run.OpenQuestions.Count} open question(s))", node.Id, cancellationToken: CancellationToken.None);
                continue;
            }

            state.Status = NodeStatus.Running;
            state.StartedAt = clock.GetUtcNow();
            state.CompletedAt = null;
            state.LastError = null;
            state.Executions++;
            metrics.NodeStarted(run, node);
            await journal.RecordAsync(run, "node.started", $"{node.Title} started ({node.Agent})", node.Id,
                cancellationToken: CancellationToken.None);

            var inputs = new Dictionary<string, Artifact>(run.Artifacts, StringComparer.Ordinal);
            running.Add(ExecuteNodeAsync(run, node, inputs, state.Executions, cancellationToken), id);
        }

        return failed;
    }

    private async Task<NodeExecution> ExecuteNodeAsync(
        WorkflowRun run, WorkflowNode node, IReadOnlyDictionary<string, Artifact> inputs, int execution, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var inputHashes = inputs.Values.ToDictionary(a => a.Key, a => a.Hash, StringComparer.Ordinal);
        var workspace = workspaces.For(run);
        DateTimeOffset? firstFailure = null;
        var attempts = 0;
        AgentOutcome last = AgentOutcome.Failure("not executed");
        var candidates = new List<(string Agent, bool IsFallback)> { (node.Agent, false) };
        if (node.FallbackAgent is not null)
        {
            candidates.Add((node.FallbackAgent, true));
        }

        foreach (var (agentName, isFallback) in candidates)
        {
            if (isFallback)
            {
                metrics.FellBack(run, node);
                await journal.RecordAsync(run, "node.fallback", $"Primary agent exhausted; falling back to {agentName}", node.Id,
                    cancellationToken: CancellationToken.None);
            }

            for (var attempt = 1; attempt <= node.Retry.MaxAttempts; attempt++)
            {
                attempts++;
                var context = new AgentContext
                {
                    Run = run,
                    Node = node,
                    Inputs = inputs,
                    Workspace = workspace,
                    Tools = tools,
                    Llm = llm,
                    Clock = clock,
                    Attempt = attempt,
                };

                last = await InvokeAsync(run, node, agentName, context, execution, isFallback, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return new NodeExecution(node.Id, agentName, last, attempts, firstFailure, timer.Elapsed, inputHashes, Cancelled: true);
                }

                if (last.Succeeded)
                {
                    return new NodeExecution(node.Id, agentName, last, attempts, firstFailure, timer.Elapsed, inputHashes, Cancelled: false);
                }

                firstFailure ??= clock.GetUtcNow();
                if (!last.Transient || attempt == node.Retry.MaxAttempts)
                {
                    break;
                }

                metrics.Retried(run, node, attempt);
                await journal.RecordAsync(run, "node.retry", $"Attempt {attempt} failed ({last.Error}); retrying", node.Id,
                    data: new Dictionary<string, string> { ["attempt"] = attempt.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    cancellationToken: CancellationToken.None);

                try
                {
                    await Task.Delay(node.Retry.BackoffFor(attempt), clock, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return new NodeExecution(node.Id, agentName, last, attempts, firstFailure, timer.Elapsed, inputHashes, Cancelled: true);
                }
            }
        }

        return new NodeExecution(node.Id, candidates[^1].Agent, last, attempts, firstFailure, timer.Elapsed, inputHashes, Cancelled: false);
    }

    private async Task<AgentOutcome> InvokeAsync(
        WorkflowRun run, WorkflowNode node, string agentName, AgentContext context, int execution, bool isFallback, CancellationToken cancellationToken)
    {
        var fault = execution <= 1 ? FaultPlan.For(run, node.Id, context.Attempt, isFallback) : null;
        if (fault is not null)
        {
            await journal.RecordAsync(run, "fault.injected", $"{fault.Kind} fault injected into {agentName}", node.Id,
                cancellationToken: CancellationToken.None);
            return AgentOutcome.Failure($"Injected {fault.Kind.ToString().ToUpperInvariant()} fault", transient: fault.Kind != FaultKind.Fatal);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(node.Timeout);
        try
        {
            return await agents.Resolve(agentName).ExecuteAsync(context, timeout.Token).WaitAsync(node.Timeout, clock, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return AgentOutcome.Failure($"{agentName} exceeded timeout of {node.Timeout.TotalSeconds:F1}s", transient: true);
        }
        catch (OperationCanceledException)
        {
            return AgentOutcome.Failure("Cancelled by safe-stop");
        }
        catch (AgentInputMissingException ex)
        {
            return AgentOutcome.Failure(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException or UnauthorizedAccessException or HttpRequestException)
        {
            LogAgentFault(logger, agentName, node.Id, ex);
            return AgentOutcome.Failure($"{agentName} faulted: {ex.Message}", transient: ex is IOException or HttpRequestException);
        }
    }

    private async Task<bool> ApplyAsync(WorkflowRun run, NodeExecution execution)
    {
        var graph = run.Graph();
        var node = graph[execution.NodeId];
        var state = run.StateOf(node.Id);
        var outcome = execution.Outcome;
        state.Attempts += execution.Attempts;
        state.ExecutedBy = execution.Agent;
        state.CompletedAt = clock.GetUtcNow();

        if (execution.Cancelled)
        {
            state.Status = NodeStatus.Pending;
            await journal.RecordAsync(run, "node.interrupted", $"{node.Title} interrupted by safe-stop; will re-run on resume", node.Id);
            return true;
        }

        if (!outcome.Succeeded)
        {
            Fail(run, node, state, outcome.Error ?? "Agent failed");
            await journal.RecordAsync(run, "node.failed", $"{node.Title} failed: {state.LastError}", node.Id);
            return false;
        }

        var outputChanged = false;
        state.Outputs.Clear();
        foreach (var artifact in outcome.Artifacts)
        {
            outputChanged |= StoreArtifact(run, artifact with { ProducedBy = node.Id }, execution.InputHashes);
            state.Outputs.Add(artifact.Key);
        }

        if (outcome.Artifacts.Count > 0)
        {
            await journal.RecordAsync(run, "artifact.produced", $"{node.Title} produced {string.Join(", ", state.Outputs)}", node.Id,
                data: state.Outputs.ToDictionary(k => k, k => run.Artifacts[k].Hash, StringComparer.Ordinal));
        }

        foreach (var draft in outcome.Decisions)
        {
            var decision = journal.Decide(run, node.Id, execution.Agent, draft, execution.InputHashes);
            await journal.RecordAsync(run, "decision.recorded", decision.Summary, node.Id, execution.Agent);
        }

        if (node.Stage == Stage.Requirements || outcome.Questions.Count > 0)
        {
            run.OpenQuestions = [.. outcome.Questions];
        }

        foreach (var insertion in outcome.NodesToInsert.Where(i => !graph.Contains(i.Node.Id)))
        {
            graph = graph.WithInsertedNode(insertion.Node, insertion.Before);
            run.Nodes = [.. graph.Nodes];
            run.StateOf(insertion.Node.Id);
            run.PlanVersion++;
            journal.Decide(run, node.Id, "planner", new DecisionDraft(
                $"Inserted '{insertion.Node.Title}' before '{graph[insertion.Before].Title}'", insertion.Reason), execution.InputHashes);
            await journal.RecordAsync(run, "plan.replanned",
                $"Plan v{run.PlanVersion}: inserted {insertion.Node.Id} before {insertion.Before}", node.Id, "planner");
        }

        if (outputChanged && state.Executions > 1 && HasSettledDescendants(run, graph, node.Id))
        {
            run.PlanVersion++;
            await InvalidateAsync(run, node.Id, includeSelf: false, $"{node.Title} output changed on re-execution");
        }

        var verdicts = policies.Evaluate(new PolicyContext(run, node, outcome));
        foreach (var verdict in verdicts.Where(v => v.Effect != PolicyEffect.Allow))
        {
            await journal.RecordAsync(run, "policy.verdict", $"{verdict.PolicyId}: {verdict.Effect} - {verdict.Reason}", node.Id, "policy-engine",
                new Dictionary<string, string> { ["effect"] = verdict.Effect.ToString(), ["risk"] = verdict.Risk.ToString() });
        }

        var denial = verdicts.FirstOrDefault(v => v.Effect == PolicyEffect.Deny);
        if (denial is not null)
        {
            Fail(run, node, state, $"Policy '{denial.PolicyId}' denied the change: {denial.Reason}");
            await journal.RecordAsync(run, "node.failed", state.LastError!, node.Id, "policy-engine");
            return false;
        }

        var exit = EvaluateGates(run, node, GatePhase.Exit, outcome);
        await RecordGatesAsync(run, node, exit);
        if (exit.Any(g => !g.Passed))
        {
            Fail(run, node, state, "Exit gate failed: " + string.Join("; ", exit.Where(g => !g.Passed).Select(g => g.Detail)));
            await journal.RecordAsync(run, "node.failed", state.LastError!, node.Id);
            return false;
        }

        var escalations = verdicts.Where(v => v.Effect == PolicyEffect.RequireApproval).ToList();
        if (node.RequiresApproval || escalations.Count > 0)
        {
            var risk = escalations.Select(v => v.Risk).Append(node.Risk).Max();
            if (CanAutoApprove(run, risk))
            {
                journal.Decide(run, node.Id, "policy:auto-approval", new DecisionDraft(
                    $"Auto-approved {node.Title}", $"Risk {risk} is within the auto-approval threshold {_options.AutoApproveUpTo}"), execution.InputHashes);
                await journal.RecordAsync(run, "approval.auto", $"{node.Title} auto-approved at risk {risk}", node.Id, "policy:auto-approval");
            }
            else
            {
                var request = new ApprovalRequest
                {
                    Id = $"A{run.Approvals.Count + 1:00}",
                    NodeId = node.Id,
                    Risk = risk,
                    Reason = node.RequiresApproval
                        ? $"{node.Title} is a governed checkpoint" + (escalations.Count > 0 ? "; " + string.Join("; ", escalations.Select(e => e.Reason)) : string.Empty)
                        : string.Join("; ", escalations.Select(e => e.Reason)),
                    Evidence = [.. state.Outputs, .. escalations.Select(e => $"{e.PolicyId}: {e.Reason}")],
                    RequestedAt = clock.GetUtcNow(),
                };
                run.Approvals.Add(request);
                state.Status = NodeStatus.AwaitingApproval;
                await journal.RecordAsync(run, "approval.requested", $"{request.Id}: {request.Reason}", node.Id,
                    data: new Dictionary<string, string> { ["approvalId"] = request.Id, ["risk"] = risk.ToString() });
                return true;
            }
        }

        state.Status = NodeStatus.Succeeded;
        metrics.NodeSucceeded(run, node, execution.Duration);
        if (execution.FirstFailure is { } failedAt)
        {
            var recovery = clock.GetUtcNow() - failedAt;
            metrics.Recovered(run, node, recovery);
            await journal.RecordAsync(run, "node.recovered", $"{node.Title} recovered after {recovery.TotalMilliseconds:F0} ms", node.Id);
        }

        await journal.RecordAsync(run, "node.succeeded", $"{node.Title} succeeded in {execution.Duration.TotalMilliseconds:F0} ms", node.Id,
            data: new Dictionary<string, string> { ["attempts"] = execution.Attempts.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        return true;
    }

    private async Task FinalizeAsync(WorkflowRun run, bool halted)
    {
        var now = clock.GetUtcNow();
        var statuses = run.Nodes.Select(n => run.StateOf(n.Id).Status).ToList();

        if (halted)
        {
            run.Status = RunStatus.Halted;
            run.HaltReason = killSwitch.Reason ?? "Safe-stop requested";
            await journal.RecordAsync(run, "run.halted", $"Run halted safely: {run.HaltReason}");
        }
        else if (statuses.Contains(NodeStatus.Failed))
        {
            var compensated = await CompensateAsync(run);
            run.Status = compensated ? RunStatus.RolledBack : RunStatus.Failed;
            run.CompletedAt = now;
            metrics.RunCompleted(run, now - (run.StartedAt ?? now));
            await journal.RecordAsync(run, compensated ? "run.rolled_back" : "run.failed",
                compensated ? "Run failed; completed changes were rolled back" : "Run failed; no compensable changes to roll back");
        }
        else if (statuses.Contains(NodeStatus.AwaitingApproval))
        {
            run.Status = RunStatus.AwaitingApproval;
            await journal.RecordAsync(run, "run.paused", $"Waiting on {run.Approvals.Count(a => a.Status == ApprovalStatus.Pending)} approval(s)");
        }
        else if (statuses.Contains(NodeStatus.AwaitingInput))
        {
            run.Status = RunStatus.AwaitingInput;
            await journal.RecordAsync(run, "run.paused", "Waiting on human clarification");
        }
        else if (statuses.All(s => s is NodeStatus.Succeeded or NodeStatus.Skipped))
        {
            run.Status = RunStatus.Succeeded;
            run.CompletedAt = now;
            metrics.RunCompleted(run, now - (run.StartedAt ?? now));
            await journal.RecordAsync(run, "run.succeeded", $"Run completed in {(now - run.StartedAt!.Value).TotalSeconds:F1}s at plan v{run.PlanVersion}");
        }
        else
        {
            run.Status = RunStatus.Failed;
            run.CompletedAt = now;
            await journal.RecordAsync(run, "run.stalled", "No runnable stages remain; workflow is stalled");
        }

        await store.SaveAsync(run, CancellationToken.None);
    }

    private async Task<bool> CompensateAsync(WorkflowRun run)
    {
        var graph = run.Graph();
        var compensated = false;
        foreach (var id in graph.TopologicalOrder().Reverse())
        {
            var node = graph[id];
            var state = run.StateOf(id);
            var leftSideEffects = state.Status == NodeStatus.Failed && state.Outputs.Count > 0;
            if (!node.Compensable || !(leftSideEffects || state.Status is NodeStatus.Succeeded or NodeStatus.AwaitingApproval))
            {
                continue;
            }

            compensated |= await CompensateNodeAsync(run, node, state);
        }

        foreach (var pending in run.Approvals.Where(a => a.Status == ApprovalStatus.Pending).ToList())
        {
            run.Approvals[run.Approvals.IndexOf(pending)] = pending with
            {
                Status = ApprovalStatus.Rejected, DecidedBy = "orchestrator", Comment = "Withdrawn: run rolled back", DecidedAt = clock.GetUtcNow(),
            };
        }

        return compensated;
    }

    private async Task<bool> CompensateNodeAsync(WorkflowRun run, WorkflowNode node, NodeState state)
    {
        var context = new AgentContext
        {
            Run = run,
            Node = node,
            Inputs = new Dictionary<string, Artifact>(run.Artifacts, StringComparer.Ordinal),
            Workspace = workspaces.For(run),
            Tools = tools,
            Llm = llm,
            Clock = clock,
        };

        try
        {
            await agents.Resolve(state.ExecutedBy ?? node.Agent).CompensateAsync(context, CancellationToken.None);
            state.Status = NodeStatus.RolledBack;
            metrics.RolledBack(run, node);
            await journal.RecordAsync(run, "node.rolled_back", $"{node.Title} compensated", node.Id);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            await journal.RecordAsync(run, "compensation.failed", $"{node.Title} compensation failed: {ex.Message}; manual intervention required", node.Id);
            return false;
        }
    }

    private async Task InvalidateAsync(WorkflowRun run, string nodeId, bool includeSelf, string reason)
    {
        var graph = run.Graph();
        var targets = graph.Descendants(nodeId).ToHashSet(StringComparer.Ordinal);
        if (includeSelf)
        {
            targets.Add(nodeId);
        }

        foreach (var id in graph.TopologicalOrder().Reverse().Where(targets.Contains))
        {
            var node = graph[id];
            var state = run.StateOf(id);
            if (state.Status == NodeStatus.Pending)
            {
                continue;
            }

            if (node.Compensable && state.Status is NodeStatus.Succeeded or NodeStatus.AwaitingApproval)
            {
                await CompensateNodeAsync(run, node, state);
            }

            state.Status = NodeStatus.Pending;
            state.LastError = null;
            foreach (var approval in run.Approvals.Where(a => a.NodeId == id && a.Status == ApprovalStatus.Pending).ToList())
            {
                run.Approvals[run.Approvals.IndexOf(approval)] = approval with
                {
                    Status = ApprovalStatus.Rejected, DecidedBy = "orchestrator", Comment = $"Superseded by plan v{run.PlanVersion}", DecidedAt = clock.GetUtcNow(),
                };
            }

            await journal.RecordAsync(run, "node.invalidated", $"{node.Title} invalidated: {reason}", id);
        }
    }

    private static bool HasSettledDescendants(WorkflowRun run, WorkflowGraph graph, string nodeId) =>
        graph.Descendants(nodeId).Any(id => run.StateOf(id).Status != NodeStatus.Pending);

    private bool StoreArtifact(WorkflowRun run, Artifact artifact, IReadOnlyDictionary<string, string> inputHashes)
    {
        var previous = run.FindArtifact(artifact.Key);
        var stored = artifact with
        {
            Version = (previous?.Version ?? 0) + 1,
            CreatedAt = clock.GetUtcNow(),
            DerivedFrom = inputHashes,
        };
        run.Artifacts[artifact.Key] = stored;
        return previous is not null && previous.Hash != stored.Hash;
    }

    private List<GateOutcome> EvaluateGates(WorkflowRun run, WorkflowNode node, GatePhase phase, AgentOutcome? outcome)
    {
        var ids = phase == GatePhase.Entry ? node.EntryGates : node.ExitGates;
        var context = new GateContext(run, node, phase, outcome);
        var results = ids.Select(id =>
        {
            var result = gates.Resolve(id).Evaluate(context);
            return new GateOutcome(id, phase.ToString(), result.Passed, result.Detail, clock.GetUtcNow());
        }).ToList();
        run.StateOf(node.Id).GateOutcomes.AddRange(results);
        return results;
    }

    private async Task RecordGatesAsync(WorkflowRun run, WorkflowNode node, List<GateOutcome> outcomes)
    {
        foreach (var gate in outcomes)
        {
            await journal.RecordAsync(run, gate.Passed ? "gate.passed" : "gate.failed", $"{gate.Phase} gate {gate.Gate}: {gate.Detail}", node.Id, "gatekeeper");
        }
    }

    private bool CanAutoApprove(WorkflowRun run, RiskLevel risk) =>
        _options.AllowAutoApproval
        && run.Settings.TryGetValue(RunSettings.AutoApprove, out var flag)
        && bool.TryParse(flag, out var enabled)
        && enabled
        && risk <= _options.AutoApproveUpTo;

    private void Fail(WorkflowRun run, WorkflowNode node, NodeState state, string error)
    {
        state.Status = NodeStatus.Failed;
        state.LastError = error;
        state.FirstFailureAt ??= clock.GetUtcNow();
        metrics.NodeFailed(run, node, error);
    }

    private static Dictionary<string, string> OutputHashes(WorkflowRun run, NodeState state) =>
        state.Outputs.Where(run.Artifacts.ContainsKey).ToDictionary(k => k, k => run.Artifacts[k].Hash, StringComparer.Ordinal);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {Agent} faulted on node {NodeId}")]
    private static partial void LogAgentFault(ILogger logger, string agent, string nodeId, Exception exception);

    private sealed record NodeExecution(
        string NodeId,
        string Agent,
        AgentOutcome Outcome,
        int Attempts,
        DateTimeOffset? FirstFailure,
        TimeSpan Duration,
        IReadOnlyDictionary<string, string> InputHashes,
        bool Cancelled);
}
