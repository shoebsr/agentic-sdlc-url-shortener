# 10 — Risks, Trade-offs & Limitations

## 1. Failure scenarios and guardrails

| Failure scenario | Detection | Guardrail / response |
|------------------|-----------|----------------------|
| Agent produces wrong code | Build gate, test gates, security review | Node fails → rollback to checkpoint; human never sees it as releasable |
| Agent produces unsafe code (secret, injection, PII logging) | Policy engine on the observed diff | Deny (rollback) or mandatory human approval with evidence |
| Transient tool failure (timeout, I/O, flaky runner) | Agent outcome marked transient | Bounded retries with backoff, then fallback agent, MTTR recorded |
| Requirement is ambiguous | Vague-term lexicon, missing measurable target | Clarification stage inserted; design blocked by entry gate until resolved |
| Requirement changes mid-run | Human amendment | Descendants invalidated and compensated, pending approvals withdrawn, plan version bumped |
| Human approves something they should not | Approval authority | Role risk ceilings, segregation of duties, every decision audited |
| Runaway or harmful automation | Operator | Per-run safe-stop, global kill switch, per-node timeouts |
| Orchestrator crashes mid-run | Recovery service at start-up | Run marked `Halted` with reason; resume continues from persisted state |
| Audit tampering | Hash verification | Chain breaks at the edited record |
| Shortener: Redis down | Health check (degraded) | Redirects fall back to the database; clicks dropped and logged, never blocking |
| Shortener: analytics worker crash | Pending entries in the stream | `XAUTOCLAIM` by another worker; idempotent persistence prevents double counting |
| Shortener: code space collision | Unique index violation | Bounded regeneration, then 503 `code.exhausted` |
| Shortener: abuse | Rate limits | 429 with `Retry-After`; gateway circuit breakers |

## 2. Trade-offs

| Decision | Benefit | Cost |
|----------|---------|------|
| Deterministic agents by default | Reproducible, key-free, cheap CI | Implementation uses versioned change recipes, not novel code (LLM mode does) |
| Custom engine over Temporal/Elsa | Governance is first-class and visible | Durability is single-node, file-based |
| File-based run store and audit | Zero infrastructure for evaluators | One orchestrator node; no horizontal scale of the control plane |
| Approval after agent execution | Humans review real evidence (diff, policy verdicts) | Agent work is done before approval; mitigated by sandboxing and rollback |
| Eventual consistency for analytics | Redirects never wait on writes | Counts lag by about one flush interval; caps may overshoot by in-flight clicks |
| Capped links bypass the cache | Accurate cap enforcement | One database read per capped redirect (capped links are rare) |
| Policy rules are regex-based | Fast, transparent, testable | Can be bypassed by obfuscation; production would add SAST and secret scanners |
| Single Dockerfile for all services | One build path to maintain | Images contain only the published project, but share build stage time |

## 3. Limitations

1. **Implementation in deterministic mode** applies pre-authored, tested change recipes (`scenarios/patches`) or a
   template. Novel code generation requires an LLM provider (`Orchestrator:Llm`), which is wired but disabled by default.
2. **Requirement understanding** is rule-based (capability catalogue + vague-term lexicon). It recognises the
   capabilities in the catalogue; anything else is flagged as "scope must be confirmed".
3. **Control plane runs on one node.** Run store, inbox and audit are local. Multi-node would need a database-backed
   store, a distributed lock per run and a durable command queue.
4. **Approver identity is a header** (`X-Actor`) validated against a configured list. Production needs OIDC.
5. **Agents run as processes** in a sandbox directory, not in isolated containers.
6. **Release** produces a tagged commit and a patch in the sandbox; it does not open a pull request or deploy.
7. **Scenario tests simulate build and test execution**; live sandbox builds are exercised through the CLI and dashboard, not in CI.
8. **Metrics are in-process** and reset when the host restarts (audit history is durable).

## 4. Assumptions

- SQL Server is the system of record; Redis is cache and queue only.
- All external traffic enters through the gateway, which supplies the trusted client address.
- Brownfield changes must be backward compatible; schema changes are additive.
- Evaluators may not have API keys, Redis or SQL Server, so the default path needs none of them
  (SQLite for tests, in-process cache and click pipeline locally).
