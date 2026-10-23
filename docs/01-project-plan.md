# 01 — Project Plan

## 1. How the assignment was read

The brief asks for two things that are easy to conflate:

| Ask | Interpretation |
|-----|----------------|
| "Build a URL shortener with core APIs, analytics and reliability features" | The **target system**. It must be real and production-quality, because the agents build and change it. |
| "Design and implement an agentic orchestration layer… the critical differentiator" | The **main deliverable**. A control plane that runs the SDLC under explicit governance. |

The evaluation criteria put orchestration first, so effort was weighted towards the orchestrator, while the
shortener was built to a standard that makes the agents' work verifiable (real builds, real tests, real migrations).

## 2. Requirements normalised into an engineering problem

| # | Requirement from the brief | Engineering interpretation |
|---|----------------------------|----------------------------|
| R1 | Requirement understanding | Requirements agent normalises text into capabilities and acceptance criteria and flags ambiguity |
| R2 | Task decomposition | Planner produces a task DAG with dependencies and verification per task |
| R3 | Codebase reasoning (brownfield) | Impact analysis reads the real codebase: project graph, impacted types per layer, API surface, data flows |
| R4 | Workflow orchestration | Explicit DAG, entry/exit gates, parallel branches with join, persistent context, decision lineage, approvals, retries, fallback, rollback, safe-stop, policy guardrails, audit, metrics, dynamic re-planning |
| R5 | Engineering output | Production code, OpenAPI, migrations, unit and integration tests, documentation |
| R6 | Validation and risk control | Gates, policy engine, security review, risk register in design, rollback plan in readiness |
| R7 | Controlled autonomy | Agents act only inside a sandbox; humans approve design, governed changes and release |
| R8 | Engineering summary | [12-engineering-summary.md](12-engineering-summary.md) |

## 3. Ambiguities in the brief and decisions taken

| Ambiguity | Decision | Reason |
|-----------|----------|--------|
| Real LLM or not? | Deterministic agents by default; pluggable LLM provider | Reviewers can run it without keys; behaviour is reproducible; LLM mode reuses every gate |
| How much UI? | Dashboard for the orchestrator only; Swagger for the shortener | The human checkpoints are what need a UI |
| What does "release" mean in a prototype? | A tagged commit plus a reviewable patch in a sandbox | Promotion to the main repository stays a human-controlled merge |
| Scale target | Horizontal scale for every service; stateless hosts | "Scalable code" is an evaluation criterion |

## 4. Phases

| Phase | Outcome | Status |
|-------|---------|--------|
| 1 | Solution scaffold, build policy (warnings as errors, analyzers), CI | Done |
| 2 | URL shortener: domain, application, infrastructure, API, tests | Done |
| 3 | Microservices split, Ocelot gateway, Docker Compose topology | Done |
| 4 | Orchestrator core: DAG engine, state, gates, retries, rollback, inbox | Done |
| 5 | Governance, observability, persistence | Done |
| 6 | Agents and workflow templates | Done |
| 7 | Scenarios with real change recipes, host API and CLI | Done |
| 8 | Dashboard | Done |
| 9 | Documentation | Done |

Each phase was committed and pushed separately so the history reads as a sequence of reviewable steps.

## 5. Deliverables map

| Deliverable from the brief | Where |
|----------------------------|-------|
| Working prototype (runnable end-to-end) | [11-setup-guide.md](11-setup-guide.md) |
| Architecture overview | [02-architecture.md](02-architecture.md), [04-orchestration-model.md](04-orchestration-model.md) |
| Three scenarios | [06-scenarios.md](06-scenarios.md), `scenarios/` |
| Setup instructions | [11-setup-guide.md](11-setup-guide.md) |
| Testing approach, limitations, trade-offs | [09-testing.md](09-testing.md), [10-risks-and-tradeoffs.md](10-risks-and-tradeoffs.md) |
