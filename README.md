# Agentic SDLC Orchestrator — URL Shortener

A working prototype of an **agentic software-engineering system** that takes a requirement and drives it
through the full SDLC — requirements → design → implementation → testing → documentation → release
readiness — under explicit governance, with a **URL shortener service** as the target system it builds
and evolves.

> Work in progress — this README is expanded as each phase lands.

## Repository layout

```
src/
  Shared/BuildingBlocks/           Cross-cutting primitives (Result, errors, clock)
  UrlShortener/                    Target system (Clean Architecture)
    UrlShortener.Domain/           Entities and invariants — no dependencies
    UrlShortener.Application/      Use cases, ports, validation
    UrlShortener.Infrastructure/   EF Core persistence, code generation
    UrlShortener.Api/              HTTP surface, OpenAPI, rate limiting, health
  Orchestrator/                    Agentic control plane
    Orchestrator.Core/             DAG engine, run state machine, gates, retries, rollback
    Orchestrator.Agents/           Stage agents (requirements, design, code, test, docs, release)
    Orchestrator.Governance/       Policy guardrails, approvals, change control
    Orchestrator.Observability/    Audit trail, reliability metrics
    Orchestrator.Persistence/      Durable run state and decision lineage
    Orchestrator.Host/             REST API, CLI and dashboard
tests/                             Unit, integration and scenario tests
scenarios/                         Greenfield, brownfield and ambiguous requirement inputs
docs/                              Architecture, ADRs, risks and testing approach
deploy/                            Dockerfiles and docker-compose
```

## Quick start

```bash
dotnet build AgenticSdlc.sln
dotnet test AgenticSdlc.sln
```
