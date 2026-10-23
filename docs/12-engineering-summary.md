# 12 — Engineering Summary

Prepared by **Shoeb Raza**.

## 1. What was built

1. **An agentic SDLC orchestrator** that takes a requirement through requirements, (impact analysis,) design,
   planning, implementation, build, tests, security review, documentation, release readiness and release — as an
   explicit, stateful DAG with gates, policies, human approvals, retries, fallback, rollback, safe-stop, audit,
   metrics and dynamic re-planning, operated from a live dashboard, a REST API or a CLI.
2. **A production-grade URL shortener** — the system the orchestrator builds and evolves — with Clean Architecture,
   SQL Server, Redis caching, an asynchronous click pipeline, rate limiting, health checks, an Ocelot gateway and
   horizontally scalable microservices in Docker Compose.

## 2. Requirement coverage

| # | Brief requirement | Delivered | Evidence |
|---|-------------------|-----------|----------|
| 1 | Requirement understanding | Capability normalisation, acceptance criteria, ambiguity detection with questions and defaults | `requirements.spec`, ambiguous scenario |
| 2 | Task decomposition | Task DAG with layer, dependencies, verification; mermaid graph | `plan.tasks` |
| 3 | Codebase reasoning | Project graph, impacted types per layer, API surface, data flows, change risk | `impact.analysis` |
| 4 | Workflow orchestration | DAG, entry/exit gates, parallel + join, context and lineage, approvals, bounded retries, fallback, compensation, safe-stop, kill switch, policies, audit chain, metrics (success, retry, rollback, MTTR, latency), re-planning | [04-orchestration-model.md](04-orchestration-model.md) |
| 5 | Engineering output | Code, OpenAPI, migrations, unit + integration tests, docs, changelog, release notes, patch | `src/`, `tests/`, `release.package` |
| 6 | Validation and risk control | Build/test/security gates, eight policies, readiness checklist, rollback plan, risk register | [10-risks-and-tradeoffs.md](10-risks-and-tradeoffs.md) |
| 7 | Controlled autonomy | Sandboxed agents, allow-listed tools, scrubbed environment, humans approve design / changes / release | [02-architecture.md §3.3](02-architecture.md#33-autonomy-boundary) |
| 8 | Final engineering summary | This document | — |

| Deliverable | Where |
|-------------|-------|
| Working prototype | `dotnet run --project src/Orchestrator/Orchestrator.Host`, `docker compose up` — [11-setup-guide.md](11-setup-guide.md) |
| Architecture overview | [02-architecture.md](02-architecture.md), [03-flowcharts.md](03-flowcharts.md) |
| Greenfield, brownfield, ambiguous scenarios | [06-scenarios.md](06-scenarios.md) |
| Setup instructions | [11-setup-guide.md](11-setup-guide.md) |
| Testing approach, limitations, trade-offs | [09-testing.md](09-testing.md), [10-risks-and-tradeoffs.md](10-risks-and-tradeoffs.md) |

## 3. Plan and rationale

- **Orchestration first.** The brief calls orchestration the critical differentiator, so governance mechanisms are
  first-class code (not configuration of a third-party engine) and each one is observable on the dashboard.
- **Real verification over simulated success.** Agents work in a git sandbox where the build and tests actually run;
  policies inspect the observed diff. The first live run caught a real flaky test, which validated the approach.
- **Reproducible by default.** Deterministic agents need no keys or network beyond NuGet; the LLM path is a
  configuration switch and is held to the same gates.
- **Scale where it matters.** The shortener separates write, read and analytics paths so the redirect hot path
  scales independently and never waits on writes.
- **Step-wise delivery.** Each phase was built, verified and committed separately.

## 4. Validation performed

| Check | Result |
|-------|--------|
| `dotnet build` (warnings as errors, analyzers) | Clean |
| Full test suite (`dotnet test AgenticSdlc.sln`) | 178 passing: orchestrator 99 unit + 6 scenario, shortener 61 unit + 12 integration |
| Shortener tests with each change recipe applied | 67 / 16 with click-cap; 68 / 13 with blocklist |
| Defects caught by the orchestrator test suite | Timeout not enforced on non-cooperative agents; double plan-version increment on amendment; lexicon missed "safer" — all fixed |
| Live brownfield run | Real build and tests in the sandbox, three approvals, released |
| Simulated runs of all four scenarios | Succeeded / Succeeded / Succeeded / RolledBack as designed |
| Docker Compose end-to-end | Create via gateway → 20 redirects over 2 replicas → 2 workers consumed the stream (lag 0) → analytics correct; spoofed `X-Forwarded-For` ignored |
| Local SQL Server smoke test | Create, 409, SSRF block, 302, 403 without token, analytics, 410 after disable, health, security headers |
| Governance | Self-approval refused; unregistered approver refused |
| Audit | Chain verified intact |

## 5. Key risks and trade-offs (short form)

- Deterministic implementation uses versioned change recipes; novel code needs LLM mode.
- Control plane is single-node (file store); production needs a database store and distributed run locks.
- Approver identity is header-based; production needs OIDC.
- Analytics is eventually consistent by design; caps may overshoot by in-flight clicks.

Details: [10-risks-and-tradeoffs.md](10-risks-and-tradeoffs.md).

## 6. Assumptions

SQL Server is the system of record; traffic enters through the gateway; brownfield changes are backward compatible
and schema changes additive; evaluators may lack API keys and infrastructure.

## 7. What I would do next

1. Orchestrator container in Docker Compose; database-backed run store with per-run distributed locks.
2. OIDC for dashboard and approvals; approver identity from the token.
3. Container-isolated agent sandboxes with a package-mirror-only network.
4. Pull-request based release (open a PR with the patch, checks and approvals attached).
5. Load test of the redirect path (NBomber/k6) to set the p95 target from data.
