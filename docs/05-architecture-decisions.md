# 05 — Architecture Decision Records

Each record states the decision, the context, the alternatives considered and the consequences.

---

## ADR-01 — Separate control plane (orchestrator) from target system (shortener)

**Decision.** Two bounded contexts in one repository; the orchestrator works on a sandboxed copy of the shortener.
**Context.** The brief needs both a product and an engine that delivers it.
**Alternatives.** Orchestrator edits the repository in place; orchestrator embedded in the shortener.
**Consequences.** Changes are reviewable and reversible; the shortener stays free of orchestration concerns.

## ADR-02 — Custom DAG engine instead of a workflow product

**Decision.** A small, purpose-built engine (graph, scheduler, inbox, journal).
**Alternatives.** Temporal, Elsa, Durable Functions, GitHub Actions as the orchestrator.
**Rationale.** The brief's differentiators (governance gates, policy verdicts on change sets, approvals with
segregation of duties, run-time node insertion, compensation, lineage) are the product here; hiding them inside a
framework would make them harder to show and to defend. The engine, coordinator, inbox and graph together are
about 1,200 lines with no infrastructure dependencies, so every behaviour is unit-testable.
**Consequences.** Durability is file-based and single-node; production would move the run store and inbox to a
database or durable queue (see [10-risks-and-tradeoffs.md](10-risks-and-tradeoffs.md)).

## ADR-03 — Per-run command inbox (single writer)

**Decision.** Human commands are queued and applied by the engine loop that owns the run.
**Alternatives.** Lock the run for every API call; optimistic concurrency with retries.
**Consequences.** No races between approvals and running branches; approvals take effect while other branches
are still executing; commands are serialised in audit order.

## ADR-04 — Saga-style compensation for rollback

**Decision.** Compensable nodes implement `CompensateAsync`; on failure, compensation runs in reverse topological order.
**Alternatives.** Transactional workspace; discard the whole workspace.
**Consequences.** Rollback is explicit and auditable per node; nodes that only produce documents need no compensation.

## ADR-05 — Git-backed sandbox workspaces

**Decision.** Each run gets a git repository seeded from the source; checkpoints are commits.
**Consequences.** Diffs feed policies with exactly what changed; restore is `git reset --hard` to a checkpoint;
the release is a tagged commit plus a patch that a human merges.

## ADR-06 — Deterministic agents with a pluggable LLM provider

**Decision.** Default to deterministic agents; `ILlmClient` with Anthropic (official SDK, `claude-opus-5-5`) and
OpenAI-compatible adapters.
**Alternatives.** LLM-only agents.
**Rationale.** Reproducible demos and tests, no secrets required to evaluate, cost-free CI. The LLM path is a
drop-in that reuses every gate, so its output is governed identically.
**Consequences.** In deterministic mode implementation comes from versioned change recipes rather than novel code.

## ADR-07 — Policies evaluate the observed change set, not the agent's claims

**Decision.** Policies inspect the sandbox diff (files, added lines, content).
**Consequences.** An agent cannot hide a change by omitting it from its summary; the same policies apply to
recipe-based and LLM-generated code.

## ADR-08 — Hash-chained, append-only audit log

**Decision.** Each record carries the previous record's hash; appends happen under an exclusive file lock.
**Alternatives.** Plain log files; database table without chaining.
**Consequences.** Tampering is detectable (`/api/audit/verify`); concurrent writers cannot fork the chain.

## ADR-09 — Microservices split by traffic shape (shortener)

**Decision.** Links API (writes), Redirect (reads, hot path), Analytics worker (asynchronous), behind a gateway.
**Alternatives.** Single API.
**Consequences.** The hot path scales independently and is never blocked by writes or analytics; one more
deployable per concern, mitigated by a shared Presentation library and one Dockerfile.

## ADR-10 — Ocelot API gateway

**Decision.** Ocelot with Polly QoS (timeouts, circuit breakers) and client-address header overwrite.
**Alternatives.** YARP, NGINX, cloud gateway.
**Consequences.** Configuration-only routing in .NET; gateway overwrites `X-Forwarded-For` so services can trust it
and clients cannot spoof their address.

## ADR-11 — Redis Streams with consumer groups for clicks

**Decision.** Redirect publishes click events to a capped stream; workers batch-persist and acknowledge.
**Alternatives.** Synchronous insert; Kafka; logging-based analytics.
**Consequences.** Redirect latency is independent of analytics; at-least-once delivery with idempotent persistence;
pending entries from crashed workers are reclaimed.

## ADR-12 — Two-tier route cache with negative caching

**Decision.** In-process L1 (short TTL) + Redis L2, pub/sub invalidation, cached "missing" entries.
**Consequences.** Sub-millisecond hits, shared warm cache across replicas, database shielded from code scanning.
Capped links bypass the cache so the cap is enforced from the current count.

## ADR-13 — Owner management tokens instead of global API keys

**Decision.** Each link returns a random 256-bit management token once; only its SHA-256 hash is stored.
**Alternatives.** Global admin key; full OAuth/OIDC.
**Consequences.** No shared secret to leak; owners manage only their links; OIDC is the production upgrade path.

## ADR-14 — SQL Server as system of record, SQLite for tests

**Decision.** EF Core with provider selected by configuration; versioned migrations for SQL Server.
**Consequences.** Tests run without infrastructure; provider differences (e.g. `DateTimeOffset` ordering in SQLite)
are handled in the model configuration.

## ADR-15 — Privacy by design for analytics

**Decision.** Store only the referrer host and an HMAC of address + user agent; never the raw IP.
**Consequences.** Unique-visitor counts remain possible; the stored data is not personal data on its own.

## ADR-16 — Dashboard without frameworks or CDNs

**Decision.** Static HTML/CSS/JS served by the orchestrator host, live updates via server-sent events.
**Consequences.** No build toolchain, works offline, strict CSP (`script-src 'self'`); all dynamic content is escaped.
