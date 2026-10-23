# Agentic SDLC Orchestrator — Solution Documentation

> A working prototype of an **agentic software-engineering system**: a requirement goes in, and a governed
> pipeline of agents takes it through requirements, design, implementation, testing, documentation and
> release readiness, with humans owning approvals and final quality. The system it builds and evolves is a
> production-grade **URL shortener**.

Prepared by **Shoeb Raza** for **Charles Schwab India**.

| # | Document | What it answers |
|---|----------|-----------------|
| 01 | [Project Plan](01-project-plan.md) | How the assignment was read, scope, phases, deliverables |
| 02 | [Solution Architecture](02-architecture.md) | Components, layers, microservices, deployment, scalability |
| 03 | [Flowcharts](03-flowcharts.md) | Every key runtime flow, step by step |
| 04 | [Orchestration Model](04-orchestration-model.md) | DAG, node lifecycle, gates, policies, approvals, retries, rollback, re-planning |
| 05 | [Architecture Decision Records](05-architecture-decisions.md) | *Why* each technology and pattern was chosen, alternatives, trade-offs |
| 06 | [Scenarios](06-scenarios.md) | Greenfield, brownfield, ambiguous: decomposition, orchestration, validation |
| 07 | [Data Model](07-data-model.md) | Shortener schema and orchestrator run state |
| 08 | [Security](08-security.md) | Controls in the shortener and in the orchestrator |
| 09 | [Testing Approach](09-testing.md) | Test pyramid, what is covered, how orchestration is validated |
| 10 | [Risks, Trade-offs & Limitations](10-risks-and-tradeoffs.md) | Failure scenarios, guardrails, known limitations, assumptions |
| 11 | [Setup Guide](11-setup-guide.md) | Run locally, run with Docker, use the dashboard and CLI, manual test walkthrough |
| 12 | [Engineering Summary](12-engineering-summary.md) | Requirement coverage, plan and rationale, artifacts, validation |

## Technology at a glance

| Layer | Technology |
|-------|------------|
| Language / runtime | C# on .NET 10 (LTS) |
| Shortener services | ASP.NET Core minimal APIs — Links API, Redirect service, Analytics worker |
| API gateway | Ocelot with Polly (timeouts, circuit breakers) |
| Data | SQL Server (system of record), EF Core migrations; SQLite in-memory for tests |
| Cache / queue | Redis — two-tier route cache and Redis Streams for click events |
| Orchestrator | Custom stateful DAG engine, policy engine, approval authority, hash-chained audit log |
| Agents | Deterministic by default; optional Claude (official Anthropic SDK) or OpenAI-compatible provider |
| Dashboard | Static HTML/CSS/JS, server-sent events, no third-party runtime dependencies |
| Packaging | Docker multi-stage images, Docker Compose |
| CI | GitHub Actions (build, test, image build) |
| Architecture | Clean Architecture, Ports & Adapters, CQRS-style read/write split |

## Repository map

```
src/
  Shared/BuildingBlocks/           Result / Error primitives
  UrlShortener/                    Target system (Domain, Application, Infrastructure, Presentation, hosts)
  Gateway/                         Ocelot API gateway
  Orchestrator/                    Core engine, Governance, Observability, Persistence, Agents, Host (API + CLI + dashboard)
tests/                             Unit and integration tests
scenarios/                         Scenario definitions (YAML) and versioned change recipes (patches)
deploy/                            Dockerfile, docker-compose, .env.example
docs/                              This documentation
```
