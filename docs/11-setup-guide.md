# 11 — Setup Guide

How to run the system on a new machine and test it by hand. Project owner: **Shoeb Raza**.

| | Option A: Docker (shortener stack) | Option B: local |
|---|---|---|
| Installs needed | Docker Desktop | .NET 10 SDK, Git, SQL Server (any edition) for the shortener API |
| Shortener | Gateway on http://localhost:8080 | API on http://localhost:5080 |
| Orchestrator dashboard | Run locally (§4) | http://localhost:5090 |
| Use it for | Microservices demo (gateway, replicas, Redis, workers) | Development, orchestrator demo |

## 1. Prerequisites

| Tool | Version | Check |
|------|---------|-------|
| .NET SDK | 10.0.x (pinned by `global.json`) | `dotnet --list-sdks` |
| Git | any recent | `git --version` (the orchestrator uses git for sandboxes) |
| Docker Desktop | any recent | `docker info` |
| SQL Server | 2019+ / LocalDB / Express | only for running the shortener API locally |

```powershell
git clone https://github.com/shoebsr/agentic-sdlc-url-shortener.git
cd agentic-sdlc-url-shortener
dotnet build AgenticSdlc.sln
dotnet test AgenticSdlc.sln
```

Tests need no database, Redis or network: they use SQLite in-memory and in-process pipelines.

## 2. What is not in the repository

| Item | Why | How to get it |
|------|-----|---------------|
| `deploy/.env` | Holds the SQL Server password and the analytics HMAC key | Copy `deploy/.env.example` and set your own values |
| `.data/` | Orchestrator run state, audit log and sandboxes | Created on first run |
| `bin/`, `obj/` | Build output | `dotnet build` |
| LLM API keys | Never stored anywhere in the repository | Environment variable, only if LLM mode is wanted (§6) |

## 3. Option A — Docker (shortener microservices)

```powershell
copy deploy\.env.example deploy\.env      # then edit both values
docker compose -f deploy/docker-compose.yml up -d --build
docker compose -f deploy/docker-compose.yml ps
```

Expected: `sqlserver`, `redis`, `links-api` healthy; 2× `redirect` healthy; 2× `analytics-worker` running; `gateway` healthy.

| URL | What |
|-----|------|
| http://localhost:8080/swagger | API documentation through the gateway |
| http://localhost:8080/api/v1/links | Create / manage links |
| http://localhost:8080/{code} | Redirect |

Stop with `docker compose -f deploy/docker-compose.yml stop` (data kept) or `down -v` (data removed).

## 4. Option B — local

### 4.1 Orchestrator (dashboard + API)

```powershell
dotnet run --project src/Orchestrator/Orchestrator.Host
```

Open http://localhost:5090. Run data is written to `.data/` at the repository root.

### 4.2 URL shortener API

The default connection string uses Windows authentication against the local default instance:

```
Server=localhost;Database=UrlShortener;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=True
```

Override it if needed (no file change required):

```powershell
$env:ConnectionStrings__Shortener = "Server=.\SQLEXPRESS;Database=UrlShortener;Trusted_Connection=True;TrustServerCertificate=True"
dotnet run --project src/UrlShortener/UrlShortener.Api
```

Open http://localhost:5080/swagger. The database and schema are created by migrations on start-up. In local mode
the API also serves redirects, and uses an in-memory cache and click pipeline (no Redis needed).

### 4.3 Orchestrator CLI (headless)

```powershell
dotnet run --project src/Orchestrator/Orchestrator.Host -- run brownfield-click-cap --approve-as eng-lead
dotnet run --project src/Orchestrator/Orchestrator.Host -- run ambiguous-safer-links --approve-as eng-lead --accept-defaults --simulate
dotnet run --project src/Orchestrator/Orchestrator.Host -- run greenfield-url-shortener --approve-as eng-lead --simulate
dotnet run --project src/Orchestrator/Orchestrator.Host -- run brownfield-policy-violation --approve-as eng-lead --simulate
```

| Flag | Meaning |
|------|---------|
| `--approve-as <name>` | Delegate pending approvals to a registered approver (recorded in the audit log) |
| `--accept-defaults` | Answer clarification questions with their suggested defaults |
| `--simulate` | Skip real `dotnet build/test` in the sandbox (fast); code changes are still applied for real |
| `--faults <spec>` | Inject faults, e.g. `unit-tests:transient:1` |

Exit code is `0` when the run succeeds.

## 5. Manual test walkthrough (dashboard)

### 5.1 Brownfield — the full governed flow
1. Turn **Simulated** off (real build and tests, ~30 s) and launch **Brownfield - maximum-click limit**.
2. Watch the execution graph: running nodes pulse blue, done nodes turn green, edges animate into running nodes.
3. At **Architecture & design** the run pauses. In **Human checkpoints** choose `eng-lead` → **Approve**.
4. At **Implementation** an approval with risk **High** appears; the evidence lists the migration files that
   triggered the protected-path policy. Approve.
5. Verification starts; unit tests, integration tests and security review run in parallel, docs alongside.
6. Approve the go/no-go. The run ends **Succeeded**.
7. Open artifacts: `impact.analysis`, `design.doc`, `plan.tasks`, `tests.integration`, `release.package` (coloured diff).

### 5.2 Governance
On any pending approval, choose `product-owner` (the initiator) → Approve: refused by segregation of duties.
Choose an approver whose ceiling is below the request's risk: refused.

### 5.3 Ambiguous requirement and re-planning
Launch **Ambiguous - make links safer** (Simulated on is fine). A dashed **Human clarification** node is inserted
(plan v2). Answer the questions or leave them blank to accept defaults → **Submit answers**. Decision lineage shows
how each answer was interpreted.

### 5.4 Rollback
Launch **Brownfield - change blocked by policy**. The secret-leak policy denies the change; the run ends **RolledBack**
and the timeline shows `node.rolled_back`.

### 5.5 Resilience
Launch **Greenfield** (Simulated on). The timeline shows `fault.injected` → `node.retry` → `node.fallback`, and
`node.recovered`. The header metrics update retries and MTTR.

### 5.6 Operator controls
- **Safe-stop** while a run is executing → `Halted`; **Resume** continues from persisted state.
- **Amend requirement** while a run is waiting → descendants invalidated, plan version increases.
- **Kill switch** halts every running workflow until released.

### 5.7 Shortener through Swagger
1. `POST /api/v1/links` with `{"url":"https://www.schwab.com","customAlias":"test-123"}`; keep the `managementToken`.
2. Browse to `/test-123` → redirected.
3. `GET /api/v1/links/test-123/analytics` with header `X-Management-Token` → clicks, daily series, referrers.
4. `{"url":"http://169.254.169.254"}` and `{"url":"javascript:alert(1)"}` → 400.
5. `DELETE /api/v1/links/test-123` with the token, then browse to it → 410.

## 6. Optional: LLM mode

No key is needed for anything above. To let agents reason with an LLM:

```powershell
$env:ANTHROPIC_API_KEY = "<your key>"
$env:Orchestrator__Llm__Provider = "Anthropic"     # model defaults to claude-opus-5-5
dotnet run --project src/Orchestrator/Orchestrator.Host
```

For an OpenAI-compatible endpoint (OpenAI, Azure OpenAI, Ollama, …):

```powershell
$env:Orchestrator__Llm__Provider = "OpenAICompatible"
$env:Orchestrator__Llm__BaseUrl = "https://api.openai.com/v1"
$env:Orchestrator__Llm__Model = "<model>"
$env:Orchestrator__Llm__ApiKeyEnvironmentVariable = "OPENAI_API_KEY"
$env:OPENAI_API_KEY = "<your key>"
```

## 7. Configuration reference

| Key | Default | Purpose |
|-----|---------|---------|
| `Orchestrator:DataRoot` | `<repo>/.data` | Run store, audit log, sandboxes |
| `Orchestrator:Engine:MaxParallelNodes` | 4 | Parallel branch width |
| `Orchestrator:Engine:AllowAutoApproval` | false | CLI/CI auto-approval (capped by `AutoApproveUpTo`) |
| `Orchestrator:Governance:Approvers` | eng-lead, security-officer, product-owner | Roles and risk ceilings |
| `Orchestrator:Governance:MaxFilesWithoutReview` / `MaxLinesWithoutReview` | 12 / 400 | Change budget |
| `Shortener:PublicBaseUrl` | `http://localhost:5080` | Base of generated short URLs |
| `Shortener:BlockedDomains` | empty | Destination blocklist (after the ambiguous scenario's change) |
| `ConnectionStrings:Redis` | empty | Empty = in-process cache and click pipeline |
| `Database:Provider` | SqlServer | `SqlServer` or `Sqlite` |
| `RateLimits:*PerMinute` | 30 / 120 / 600 | Create / management / redirect limits per client |

## 8. Troubleshooting

| Symptom | Fix |
|---------|-----|
| Shortener API cannot connect to SQL Server | Set `ConnectionStrings__Shortener` for your instance (§4.2) |
| `docker compose` says `MSSQL_SA_PASSWORD must be set` | Create `deploy/.env` from `.env.example` |
| Port 5080 / 5090 / 8080 already in use | Stop the other process or change `applicationUrl` in `Properties/launchSettings.json` |
| Live scenario fails at restore | The sandbox needs NuGet access for the first build |
| A run shows `Halted` after a restart | Expected: open it and press **Resume** |
