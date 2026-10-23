# 04 — Orchestration Model

This document maps each orchestration requirement in the brief to the mechanism that implements it.

| Requirement from the brief | Mechanism | Code |
|----------------------------|-----------|------|
| Explicit dependency graph | `WorkflowGraph`: validated DAG, Kahn topological order (stage-prioritised), cycle detection, descendants | `Orchestrator.Core/Model/WorkflowGraph.cs` |
| Entry / exit gates | `IGate` per node, evaluated before start and after the agent, results stored on the node | `Orchestrator.Governance/Gates.cs` |
| Sequential and parallel paths with synchronisation | Scheduler starts every ready node up to `MaxParallelNodes`; a node with several dependencies is a join | `WorkflowEngine.StartReadyNodesAsync` |
| Cross-stage context | Artifacts are versioned, hashed and keyed; every agent receives an immutable snapshot of upstream artifacts | `Artifact`, `AgentContext.Inputs` |
| Decision lineage | Every decision records actor, node, plan version, rationale, rejected alternatives and the hashes of the inputs it was based on | `Decision`, `RunJournal.Decide` |
| Human approval for high-impact actions | Governed checkpoints plus policy escalations create `ApprovalRequest`s; approval authority enforces roles, risk ceilings and segregation of duties | `ApprovalAuthority.cs` |
| Bounded retries | Per-node `RetryPolicy` (attempts, exponential backoff) for transient failures only | `WorkflowNode.Retry` |
| Fallback | Per-node `FallbackAgent` invoked when the primary is exhausted | `ExecuteNodeAsync` |
| Rollback | Saga-style compensation in reverse topological order; implementation restores its git checkpoint, release deletes its tag | `CompensateAsync`, `IAgent.CompensateAsync` |
| Safe-stop | Per-run halt and global kill switch cancel in-flight agents; interrupted nodes return to Pending; state is persisted | `RunCoordinator.HaltAsync`, `KillSwitch` |
| Policy guardrails (security, compliance, change control) | Policy engine evaluates every agent outcome: Allow, RequireApproval or Deny | `Orchestrator.Governance/Policies` |
| Audit-grade observability | Append-only, hash-chained audit log with verification endpoint; live event stream | `HashChainedAuditLog.cs` |
| Reliability metrics | Success rate, retry rate, fallback and rollback counts, MTTR, mean and p95 end-to-end latency, per-stage stats | `ReliabilityMetrics.cs` |
| Dynamic re-planning | Agents can insert nodes at run time; requirement amendments and changed upstream outputs invalidate descendants | `NodeInsertion`, `InvalidateAsync`, `AmendRequirementAsync` |
| Controlled autonomy | Sandboxed workspace, allow-listed tools, scrubbed environment, humans own approvals | `ProcessToolbox`, `GitWorkspace` |

## 1. Graph and scheduling

- Nodes declare `DependsOn`, `EntryGates`, `ExitGates`, `Risk`, `RequiresApproval`, `RequiresHumanInput`,
  `Compensable`, `Retry`, `Timeout`, `Agent` and an optional `FallbackAgent`.
- The graph is validated on construction and again on every mutation, so a cycle or a dangling dependency can
  never enter a running workflow.
- Scheduling is **not** a fixed list. Each loop iteration computes the ready set from the current state, starts up
  to `MaxParallelNodes` nodes, and waits for whichever finishes first *or* for a human command.

## 2. State and ownership

- A run's state (nodes, node states, artifacts, decisions, approvals, open questions, settings, plan version) is
  persisted after every applied transition with an atomic write (temp file then rename).
- **Single owner:** only the engine loop mutates a run. Approvals, clarifications and amendments arrive as
  commands in a per-run inbox; the engine drains the inbox between scheduling steps. This removes races between
  API requests and running branches without locking the whole run for the duration of a test suite.
- Agents never see live state: they receive a snapshot of artifacts at start.

## 3. Gates

| Gate | Phase | Passes when |
|------|-------|-------------|
| `artifact:<key>` | exit | the node produced a non-empty artifact |
| `requirements.unambiguous` | entry | no open questions, or a clarification answer exists |
| `changes.produced` | exit | implementation produced a non-empty change set |
| `build.succeeded` | exit | build verifier reported success |
| `tests.passed` | exit | pass rate ≥ `MinimumPassRate` (100 %) and zero failures |
| `security.no-critical` | exit | security review reported zero critical findings |
| `approvals.cleared` | entry (release) | no approval is pending |
| `readiness.complete` | exit | readiness checklist has zero blocking items |

## 4. Policies

| Policy | Effect | Example trigger |
|--------|--------|-----------------|
| `security.secret-leak` | **Deny** (Critical) | AWS key, private key block, `sk-ant-…`, `ghp_…`, quoted password literal |
| `security.workspace-boundary` | **Deny** (Critical) | Change path is absolute or contains `..` |
| `security.insecure-code` | RequireApproval (High) | Interpolated raw SQL, TLS validation override, `BinaryFormatter`, permissive CORS, MD5 |
| `compliance.personal-data` | RequireApproval (High) | IP address / e-mail logged or stored without hashing |
| `change-control.protected-paths` | RequireApproval (High) | Migrations, CI workflows, deployment, security code, build props |
| `change-control.change-budget` | RequireApproval (Medium / High) | More than 12 files or 400 changed lines |
| `change-control.dependencies` | RequireApproval (Medium) | New `PackageReference` in a brownfield change |
| `change-control.destructive-schema` | Deny / RequireApproval (Critical) | `DROP DATABASE` / `TRUNCATE` (deny), `DropTable` / `DropColumn` in the forward migration (approve) |

Policies see the agent's proposed **change set** (files, kind, added/removed lines, added content) — computed from
the sandbox diff, not from what the agent claims it did.

## 5. Approvals and roles

| Approver | Role | May approve up to |
|----------|------|-------------------|
| `product-owner` | Product Owner | Medium |
| `eng-lead` | Engineering Lead | High |
| `security-officer` | Security Officer | Critical |

- The initiator of a run can never approve their own run (segregation of duties).
- Auto-approval exists only for the CLI/CI path, is disabled by default, and is capped at `Low` risk.
- Approval wait time is recorded as a metric; every decision is in the audit log and in decision lineage.

## 6. Retries, fallback, rollback

- **Retry** only on transient outcomes (timeouts, I/O, network, missing test summary), with exponential backoff.
- **Fallback** examples: requirements analyst → rule-based analyst; test runner → diagnostic runner
  (`--blame-hang`, no parallelisation).
- **MTTR** is measured from a node's first failure to its eventual success.
- **Rollback** runs on any terminal failure. A node that failed *after* producing side effects (e.g. implementation
  denied by policy) is compensated too.

## 7. Re-planning

| Trigger | Effect |
|---------|--------|
| Agent requests a node insertion (ambiguity found) | Node inserted before the first dependent; plan version +1; decision recorded |
| Requirement amended by a human | Requirements node and all descendants invalidated; compensable work undone; pending approvals withdrawn; plan version +1 |
| A node re-executes and its output hash changes | Its succeeded descendants are invalidated; plan version +1 |

## 8. Metrics

`GET /api/metrics` and the dashboard header expose:

| Metric | Definition |
|--------|------------|
| Run success rate | Succeeded runs / completed runs |
| Node success rate | (Executions − failures) / executions |
| Retry rate | Retries / node executions |
| Rollback rate | Rollbacks / completed runs |
| MTTR | Mean time from a node's first failure to its recovery |
| End-to-end latency | Mean and p95 from run start to completion (includes human wait) |
| Approval wait | Mean time an approval was pending |
| Per stage | Executions, failures, mean duration |

The same values are emitted through `System.Diagnostics.Metrics` (meter `AgenticSdlc.Orchestrator`) for
OpenTelemetry collection.

## 9. Agents

| Agent | Stage | Produces |
|-------|-------|----------|
| `requirements-analyst` (fallback `requirements-rules`) | Requirements | `requirements.spec`, questions, node insertions |
| `clarification-broker` | Clarification | `requirements.clarified` |
| `codebase-analyst` | Design (brownfield) | `impact.analysis` |
| `solution-architect` | Design | `design.doc`, `design.api`, ADR decisions |
| `delivery-planner` | Planning | `plan.tasks` (task DAG) |
| `implementation-engineer` | Implementation | change set, `implementation.changes`, checkpoint |
| `build-verifier` | Verification | `verify.build` |
| `test-runner` (fallback `test-runner-diagnostic`) | Testing | `tests.unit`, `tests.integration` |
| `security-reviewer` | Verification | `security.report` |
| `technical-writer` | Documentation | `docs.feature`, `CHANGELOG.md` in the sandbox |
| `release-manager` | Release readiness | `release.checklist`, `release.notes` |
| `release-publisher` | Release | `release.package` (tag + patch) |
| `preview-deployer` | Release | `release.preview` — runs the released build from the sandbox on a free port (5100–5109) with its own SQLite database, waits for `/health/ready`, returns the URL; compensation stops it |

### Deterministic and LLM modes

| | Deterministic (default) | LLM (`Orchestrator:Llm:Provider`) |
|---|---|---|
| Requirements | Capability catalogue + vague-term lexicon | Same, enriched with model-proposed ambiguities and criteria |
| Implementation | Versioned change recipe (git patch) or template | Model writes a unified diff, applied with `git apply --check` first |
| Gates, policies, approvals, tests | Identical | Identical |
| Key handling | None | Read from an environment variable at call time; never logged or stored |

LLM output is never trusted: it lands in the sandbox and must pass the same build, tests, policies and human approval.
