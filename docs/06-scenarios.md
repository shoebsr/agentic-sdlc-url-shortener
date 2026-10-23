# 06 — Scenarios

Four scenario definitions live in `scenarios/*.yaml`. Three are required by the brief; the fourth demonstrates
policy enforcement and rollback. Each can be launched from the dashboard or the CLI (see [11-setup-guide.md](11-setup-guide.md)).

| Scenario | Kind | What it proves |
|----------|------|----------------|
| `greenfield-url-shortener` | Greenfield | Build from an empty solution; parallel verification; retry, fallback and MTTR under injected faults |
| `brownfield-click-cap` | Brownfield | Codebase reasoning across layers; schema change under change control; real build and tests of modified code |
| `ambiguous-safer-links` | Ambiguous | Ambiguity detection; run-time insertion of a human clarification stage; scope derived from answers |
| `brownfield-policy-violation` | Brownfield | A committed credential is denied by policy and the workspace is rolled back |

---

## 1. Greenfield — URL shortener service

**Requirement.** *Build a URL shortener service with APIs to create short links, optionally with a custom alias and
an expiry, redirect visitors to the destination, and report click analytics per link. The service must be reliable
under load, with rate limiting and health checks, and must not store visitor IP addresses.*

### Requirement understanding
- Capabilities: `shorten-api`, `redirect`, `analytics`, `reliability`, `custom-alias`, `expiry`.
- No vague term without a measurable target → no clarification needed.
- Non-functional baseline added: problem details, security headers, no secrets in source, tests for every behaviour.

### Decomposition
| Task | Layer | Depends on |
|------|-------|-----------|
| ShortCode and DestinationUrl value objects with SSRF guards | Domain | — |
| Create/get/disable endpoints with problem details | Presentation | domain |
| Redirect endpoint with tiered cache | Presentation | domain |
| Click pipeline and analytics query | Infrastructure | redirect |
| Rate limiting, health checks, graceful degradation | Presentation | create API |
| Alias validation and conflicts; expiry enforcement | Domain | — |
| Build + unit + integration; security review; docs; readiness + release | Verification → Release | all implementation tasks |

### Orchestration
- Sandbox baseline `empty` (only build configuration); implementation materialises the service template.
- Injected faults (`requirements:primary;integration-tests:transient:1`):
  the requirements agent fails all retries → **fallback** to the rule-based analyst; integration tests fail once →
  **retry** succeeds → **MTTR** recorded.
- Policy escalation: the change exceeds the unreviewed change budget and touches migrations → implementation
  needs **High** approval even though greenfield does not require it by default.
- Approvals: design (Medium), implementation (High, policy-escalated), go/no-go (High).

### Validation
Build succeeded gate, 100 % tests gate (unit and integration), no critical security findings, documentation present,
readiness checklist with zero blocking items.

---

## 2. Brownfield — maximum-click limit

**Requirement.** *Add an optional maximum-click limit to short links. Once a link reaches its limit it must stop
redirecting and return 410 Gone. Owners can see the limit in the link details. Existing links without a limit must
keep working exactly as before.*

### Requirement understanding
- Detected: `click-cap`. Mentions of existing behaviour (redirect, clicks) are treated as context, not new scope:
  in brownfield work only net-new capabilities are planned.
- Acceptance criteria: optional `maxClicks` 1–1,000,000; 410 `link.exhausted` at the cap; visible in details;
  uncapped links unchanged.

### Codebase reasoning (impact analysis artifact)
- Reads every `.csproj` and builds the project dependency graph.
- Finds the types that define the impacted symbols (`ShortLink`, `LinkRoute`, `ShortLinkService`,
  `CreateShortLinkCommand`, `CreateLinkRequest`, `ShortLinkView`, `ShortenerDbContext`, `DomainErrors`) and groups them
  by layer: Domain, Application, Infrastructure, Presentation.
- Lists the existing API surface and the data flows touched (create, redirect hot path, analytics).
- Flags: schema change required (protected path) and redirect hot path affected.

### Decomposition
| Task | Layer | Depends on |
|------|-------|-----------|
| MaxClicks on ShortLink with validation and exhaustion rule | Domain | — |
| Thread maxClicks through command, route and view; enforce on resolve | Application | domain |
| EF mapping and additive migration | Infrastructure | domain |
| Expose maxClicks on the API contract | Presentation | application |
| Verify, security, docs, release | — | all |

### Design decisions recorded
- Capped links bypass the route cache and are checked against the batched click counter (alternatives: atomic
  decrement per click, Redis `INCR`, caching capped routes).
- Nullable column instead of a sentinel value (additive, backward-compatible migration).

### Orchestration
- Implementation applies `scenarios/patches/click-cap.patch` (14 files, +339/−18: domain, application, migration, API contract, tests and a "Max clicks" field in the web UI) in the sandbox.
- `change-control.protected-paths` escalates because migrations changed → **High** approval with the migration
  files listed as evidence.
- After approval: build → (unit ‖ integration ‖ security) with docs in parallel → go/no-go → release.

### Validation (live run)
Real `dotnet build` and `dotnet test` in the sandbox: unit and integration suites (including four new click-cap
integration tests) green; dependency vulnerability audit clean; run completed in about 30 seconds of machine time.

---

## 3. Ambiguous — make links safer

**Requirement.** *Customers say our short links are not safe enough. Make them safer, and it should stay fast.*

### Requirement understanding
| Ambiguity | Question | Suggested default |
|-----------|----------|-------------------|
| "fast" (no measurable target) | What latency target applies, and at what peak rate? | p95 redirect latency < 50 ms at 1,000 req/s per instance |
| "safe" | Which threat first: malicious destinations, link enumeration, or abusive creation? | Block malicious destination domains |
| existing data | Should the rule apply retroactively to existing links? | No — new links only |

### Orchestration
1. Requirements agent emits the questions and asks the planner to insert a **Human clarification** node before
   impact analysis → **plan v1 → v2**, recorded as a decision.
2. Run pauses in `AwaitingInput`. A human answers (or accepts defaults).
3. Clarification agent re-derives scope from requirement + answers → `destination-blocklist`; the performance
   answer becomes a non-functional requirement.
4. Entry gate `requirements.unambiguous` confirms the ambiguities are resolved before impact analysis and design.
5. Implementation applies `scenarios/patches/destination-blocklist.patch` (configuration-driven domain blocklist,
   subdomain-aware, `400 url.blocked_domain`, unit + integration tests).

### Validation
Same gates as brownfield; decision lineage shows each interpretation (e.g. *Interpreted Q2 safe as: Block malicious
destination domains*).

---

## 4. Brownfield — change blocked by policy

Same requirement as scenario 2, but the implementation also writes a cloud access key into
`appsettings.Staging.json`.

1. `security.secret-leak` returns **Deny** (Critical) on the observed diff.
2. The implementation node fails; it left side effects, so it is **compensated**: the sandbox is reset to its
   pre-implementation checkpoint and the file disappears.
3. Run ends `RolledBack`; rollback count increments.

---

## 5. A defect the orchestrator found

During the first live brownfield run the integration-test gate failed: `Clicks_flow_asynchronously_into_analytics`
saw 3 clicks in the window but a total of 0. Analysis showed a read skew: the analytics endpoint loaded the link's
counter and the click events in two queries, and under parallel load the worker committed between them. The test
polled only one of the two counters. The run was rolled back automatically; the test was fixed to wait for both
counters to converge, and the next live run passed. This is exactly the failure the gates exist to catch.
