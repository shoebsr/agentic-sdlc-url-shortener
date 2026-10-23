# 02 — Solution Architecture

## 1. Two bounded contexts

```mermaid
flowchart TB
    subgraph CP["Orchestrator — control plane"]
        direction LR
        H[Host<br/>REST · SSE · CLI · Dashboard] --> CO[Run Coordinator]
        CO --> EN[Workflow Engine<br/>DAG scheduler]
        EN --> AG[Agents]
        EN --> GV[Governance<br/>gates · policies · approvals · kill switch]
        EN --> OB[Observability<br/>audit chain · metrics · events]
        EN --> PS[(Run store)]
        AG --> WS[[Git sandbox workspace]]
    end
    subgraph DP["URL shortener — target system"]
        direction LR
        GW[Ocelot gateway] --> LA[Links API]
        GW --> RD[Redirect service ×N]
        LA --> SQL[(SQL Server)]
        RD --> RC[(Redis cache)]
        RD --> RS[(Redis Stream)]
        RS --> AW[Analytics worker ×N]
        AW --> SQL
    end
    WS -. "builds, tests and changes a copy of" .-> DP
```

The **orchestrator** plans and governs work. The **shortener** is the product. The orchestrator never edits the
live repository: every run gets its own git-backed sandbox seeded from the repository, so changes are
reviewable as a diff and can be rolled back atomically.

## 2. URL shortener

### 2.1 Clean Architecture per service

```mermaid
flowchart LR
    P[Presentation<br/>endpoints · problem details · rate limits · security headers] --> A[Application<br/>ShortLinkService · ports · options]
    I[Infrastructure<br/>EF Core · Redis · click pipeline · crypto] --> A
    A --> D[Domain<br/>ShortCode · DestinationUrl · ShortLink · ClickEvent]
    D --> B[BuildingBlocks<br/>Result · Error]
    Hosts[Hosts: Links API · Redirect · Analytics worker] --> P
    Hosts --> I
```

- **Domain** has no dependencies. Invariants (code format, SSRF-safe URLs, expiry, disable) live in value objects and the aggregate.
- **Application** owns use cases and defines ports (`IShortLinkRepository`, `ILinkRouteCache`, `IClickTracker`, `IClickSink`, `IManagementTokenService`, …).
- **Infrastructure** implements the ports; the same code runs in two modes, chosen by configuration:

| Concern | Single process (local, tests) | Distributed (Docker) |
|---------|-------------------------------|----------------------|
| Route cache | In-memory | Two-tier: in-memory L1 + Redis L2 with pub/sub invalidation |
| Click capture | Bounded in-process channel + background writer | Redis Stream + consumer-group workers |
| Database | SQL Server or SQLite | SQL Server |

- **Presentation** is a shared library, so the Links API and the Redirect service reuse the same endpoints, error mapping, rate-limit policies, security headers and health checks.

### 2.2 Microservices and scaling

| Service | Responsibility | Scales by |
|---------|----------------|-----------|
| Gateway (Ocelot) | Single entry point, routing, timeouts, circuit breakers, trusted client address | Replicas behind a load balancer |
| Links API | Create, read, disable, analytics; applies migrations at start-up | Replicas (stateless) |
| Redirect | `GET /{code}` hot path, cache-first | Replicas (most traffic lands here) |
| Analytics worker | Consumes click stream, idempotent batch persistence | Consumer-group members |
| Redis | Shared cache and click stream | Managed Redis / cluster |
| SQL Server | System of record | Vertical, read replicas |

Write traffic (create) and read traffic (redirect) are separated, and click recording never blocks a redirect.
If Redis or the worker is slow or down, redirects still succeed and the dropped-click counter is logged.

### 2.3 Deployment (Docker Compose)

```mermaid
flowchart LR
    U((Client)) -->|:8080| GW[gateway]
    GW --> LA[links-api]
    GW --> R1[redirect-1]
    GW --> R2[redirect-2]
    LA --> SQL[(sqlserver)]
    R1 & R2 --> RED[(redis)]
    R1 & R2 --> SQL
    RED --> W1[analytics-worker-1]
    RED --> W2[analytics-worker-2]
    W1 & W2 --> SQL
```

Start-up is health-gated: SQL Server and Redis must be healthy before the Links API starts; the Links API
must be healthy (migrations applied) before Redirect and workers start; the gateway starts last.

## 3. Orchestrator

### 3.1 Components

| Project | Responsibility |
|---------|----------------|
| `Orchestrator.Core` | Model (graph, run, artifacts, decisions, approvals), `WorkflowEngine`, `RunCoordinator`, per-run command inbox, journal |
| `Orchestrator.Governance` | Policy engine and policies, gate registry, approval authority (risk ceilings, segregation of duties), kill switch |
| `Orchestrator.Observability` | Hash-chained audit log, live event publisher, reliability metrics (`System.Diagnostics.Metrics`) |
| `Orchestrator.Persistence` | Atomic file-backed run store |
| `Orchestrator.Agents` | Agents, toolbox, git sandbox, capability catalogue, workflow templates, scenario loader, LLM adapters |
| `Orchestrator.Host` | REST API, server-sent events, headless CLI, dashboard, restart recovery |

Core depends on nothing but abstractions. Governance, observability, persistence and agents implement Core's
interfaces and are composed in the Host, so each can be replaced (e.g. a database run store, an OPA policy engine)
without touching the engine.

### 3.2 Control flow in one picture

```mermaid
sequenceDiagram
    autonumber
    participant Human
    participant Host
    participant Coord as RunCoordinator
    participant Engine as WorkflowEngine
    participant Agent
    participant Gov as Governance
    participant Audit
    Human->>Host: POST /api/runs (scenario or requirement)
    Host->>Coord: StartAsync(run)
    Coord->>Engine: ExecuteAsync(run, inbox)
    loop until no runnable node
        Engine->>Gov: entry gates
        Engine->>Agent: ExecuteAsync (retry · fallback · timeout)
        Agent-->>Engine: artifacts · decisions · change set · questions
        Engine->>Gov: policies + exit gates
        alt approval required
            Engine->>Audit: approval.requested
            Engine-->>Human: run paused (AwaitingApproval)
            Human->>Host: approve / reject
            Host->>Coord: command → run inbox
            Coord->>Engine: drain inbox, continue
        end
        Engine->>Audit: every transition (hash-chained)
    end
    Engine-->>Host: Succeeded · RolledBack · Halted
```

### 3.3 Autonomy boundary

| Agents may | Agents may not |
|------------|----------------|
| Read and write files inside their run's sandbox | Touch the live repository or any path outside the sandbox |
| Run `dotnet` and `git` (allow-listed) inside the sandbox | Run any other executable; see secrets (environment is scrubbed) |
| Propose changes, decisions, questions and plan insertions | Approve anything; skip a gate; change policy |
| Retry transient failures within the node's budget | Retry beyond the budget or after a policy denial |

Humans own: architecture sign-off, approval of governed changes, release go/no-go, clarification of intent,
requirement changes, safe-stop and the global kill switch.

## 4. Cross-cutting concerns

| Concern | Approach |
|---------|----------|
| Configuration | Options classes validated at start-up (`ValidateOnStart`), environment overrides, secrets only via environment / `.env` |
| Errors | `Result<T>` with typed `Error` for expected failures; RFC 9457 problem details at the edge |
| Logging | Source-generated `LoggerMessage` |
| Observability | Health endpoints (`/health/live`, `/health/ready`), `System.Diagnostics.Metrics`, append-only audit |
| Build quality | Warnings as errors, .NET analyzers at `latest-recommended`, code style enforced in build |
