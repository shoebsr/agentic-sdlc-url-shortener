# 03 — Flowcharts

Every important runtime flow, end to end.

**Orchestrator**
1. [End-to-end SDLC run](#1-end-to-end-sdlc-run)
2. [Workflow graph per scenario](#2-workflow-graph-per-scenario)
3. [Node lifecycle](#3-node-lifecycle)
4. [Executing one node: gates, retries, fallback, policies](#4-executing-one-node-gates-retries-fallback-policies)
5. [Human approval](#5-human-approval)
6. [Ambiguity and dynamic re-planning](#6-ambiguity-and-dynamic-re-planning)
7. [Requirement amended mid-run](#7-requirement-amended-mid-run)
8. [Failure, rollback and compensation](#8-failure-rollback-and-compensation)
9. [Safe-stop, kill switch and restart recovery](#9-safe-stop-kill-switch-and-restart-recovery)
10. [Audit hash chain](#10-audit-hash-chain)

**URL shortener**

11. [Create a short link](#11-create-a-short-link)
12. [Redirect (hot path)](#12-redirect-hot-path)
13. [Click analytics pipeline](#13-click-analytics-pipeline)

---

## 1. End-to-end SDLC run

```mermaid
flowchart LR
    A([Requirement]) --> B[Requirements<br/>analysis]
    B --> C{Ambiguous?}
    C -- yes --> D[Human<br/>clarification]
    C -- no --> E
    D --> E[Impact analysis<br/>brownfield only]
    E --> F[Architecture &<br/>design ⚑]
    F --> G[Task<br/>decomposition]
    G --> H[Implementation<br/>in sandbox ⚑]
    H --> I[Build<br/>verification]
    H --> J[Documentation]
    I --> K[Unit tests]
    I --> L[Integration tests]
    I --> M[Security review]
    K & L & M & J --> N[Release readiness<br/>go / no-go ⚑]
    N --> O[Release package]
    O --> P([Tagged release +<br/>reviewable patch])
```

⚑ = human approval checkpoint. Implementation is approved when it is brownfield or when a policy escalates it.

## 2. Workflow graph per scenario

| Scenario | Nodes | Parallel branches | Approvals |
|----------|-------|-------------------|-----------|
| Greenfield | 11 | build ‖ docs, then unit ‖ integration ‖ security | design, implementation (policy-escalated: change budget, protected paths), go/no-go |
| Brownfield | 12 (adds impact analysis) | same | design, implementation (change control + migration), go/no-go |
| Ambiguous | 12 + clarification inserted at run time | same | clarification answers, design, implementation, go/no-go |

## 3. Node lifecycle

```mermaid
stateDiagram-v2
    [*] --> Pending
    Pending --> Running: dependencies done + entry gates pass
    Pending --> AwaitingInput: needs human clarification
    AwaitingInput --> Pending: answers submitted
    Pending --> Failed: entry gate fails
    Running --> Pending: interrupted by safe-stop
    Running --> Failed: agent fails after retries and fallback
    Running --> Failed: policy denies / exit gate fails
    Running --> AwaitingApproval: governed checkpoint or policy escalation
    Running --> Succeeded: gates pass, no approval needed
    AwaitingApproval --> Succeeded: approved
    AwaitingApproval --> Failed: rejected
    Succeeded --> Pending: invalidated by re-plan
    Succeeded --> RolledBack: compensated after a later failure
    Failed --> RolledBack: compensated (left side effects)
    Failed --> Pending: re-planned
```

## 4. Executing one node: gates, retries, fallback, policies

```mermaid
flowchart TD
    S([Node ready]) --> EG{Entry gates pass?}
    EG -- no --> NF[Node Failed<br/>run stops scheduling]
    EG -- yes --> RUN[Invoke primary agent<br/>with timeout]
    RUN --> OK{Succeeded?}
    OK -- no --> TR{Transient and<br/>attempts left?}
    TR -- yes --> BO[Exponential backoff<br/>audit: node.retry] --> RUN
    TR -- no --> FB{Fallback agent<br/>defined?}
    FB -- yes --> FBR[Invoke fallback agent<br/>audit: node.fallback] --> OK2{Succeeded?}
    OK2 -- no --> NF
    OK2 -- yes --> STORE
    FB -- no --> NF
    OK -- yes --> STORE[Store artifacts with version,<br/>hash and input lineage]
    STORE --> DEC[Record decisions]
    DEC --> INS{Agent asked to<br/>insert nodes?}
    INS -- yes --> RP[Insert node into DAG<br/>plan version +1] --> POL
    INS -- no --> POL{Policy verdicts}
    POL -- Deny --> NF
    POL -- Allow / RequireApproval --> XG{Exit gates pass?}
    XG -- no --> NF
    XG -- yes --> AP{Governed checkpoint or<br/>policy escalation?}
    AP -- yes --> AA[Approval requested<br/>node AwaitingApproval]
    AP -- no --> SUC[Node Succeeded<br/>MTTR recorded if it recovered]
```

## 5. Human approval

```mermaid
flowchart TD
    R([Approval requested]) --> P[Run paused on this branch<br/>other branches keep running]
    P --> H[Human chooses approver,<br/>approve or reject, comment]
    H --> I[Command posted to the run's inbox]
    I --> A{Approval authority}
    A -- not a registered approver --> X1[Refused — 409]
    A -- approver is the run initiator --> X2[Refused — segregation of duties]
    A -- risk above approver ceiling --> X3[Refused — escalate to a higher role]
    A -- authorised --> D{Decision}
    D -- approve --> S[Node Succeeded<br/>decision + approval wait recorded]
    D -- reject --> F[Node Failed]
    S --> C[Engine continues downstream]
    F --> RB[Rollback of compensable work]
```

## 6. Ambiguity and dynamic re-planning

```mermaid
flowchart TD
    A([Vague requirement:<br/>'make links safer, keep it fast']) --> B[Requirements agent]
    B --> C{Vague terms without<br/>measurable target?}
    C -- no --> N[Normal flow]
    C -- yes --> Q[Questions with suggested defaults<br/>Q1 performance · Q2 threat · Q3 existing data]
    Q --> I[Planner inserts 'Human clarification'<br/>before impact analysis — plan v1 → v2]
    I --> W[Run AwaitingInput]
    W --> H[Human answers or accepts defaults]
    H --> CL[Clarification agent re-derives scope<br/>from requirement + answers]
    CL --> G{Entry gate:<br/>ambiguities resolved?}
    G -- yes --> D[Impact analysis → design → … with clarified scope]
```

## 7. Requirement amended mid-run

```mermaid
flowchart TD
    A([Requirement changed]) --> V[Plan version +1]
    V --> T[Find requirements node<br/>and all descendants]
    T --> C{Descendant succeeded<br/>and compensable?}
    C -- yes --> CO[Compensate: restore sandbox checkpoint]
    C -- no --> RS
    CO --> RS[Reset to Pending]
    RS --> AP[Withdraw pending approvals<br/>'superseded by plan vN']
    AP --> D[Record decision with new requirement hash]
    D --> RE[Engine re-executes from requirements]
```

Re-planning also triggers automatically when a node re-executes and its output hash changes: its succeeded
descendants are invalidated because they were derived from the old output.

## 8. Failure, rollback and compensation

```mermaid
flowchart TD
    F([A node fails:<br/>policy deny · exit gate · rejection · agent error]) --> S[Stop scheduling new nodes]
    S --> W[Let in-flight branches finish]
    W --> R[Walk the DAG in reverse topological order]
    R --> C{Compensable and<br/>succeeded, awaiting approval,<br/>or failed with side effects?}
    C -- yes --> CP[Call agent.CompensateAsync<br/>implementation: git reset to checkpoint<br/>release: delete tag]
    CP --> RB[Node RolledBack]
    C -- no --> NX[Skip]
    RB --> AP[Withdraw pending approvals]
    NX --> AP
    AP --> END([Run RolledBack — or Failed if nothing to undo])
```

## 9. Safe-stop, kill switch and restart recovery

```mermaid
flowchart LR
    subgraph Stop
        K([Kill switch or per-run safe-stop]) --> C[Cancel run token]
        C --> I[In-flight agents cancelled]
        I --> P[Interrupted nodes back to Pending<br/>state persisted]
        P --> H[Run Halted with reason]
    end
    subgraph Resume
        H --> R{Resume requested<br/>and kill switch released?}
        R -- yes --> E[Engine continues from persisted state]
    end
    subgraph Restart
        X([Host restarts]) --> RS[Recovery service finds runs left 'Running']
        RS --> H
    end
```

## 10. Audit hash chain

```mermaid
flowchart LR
    E([State transition]) --> L[Acquire exclusive lock on audit file]
    L --> T[Read last record:<br/>sequence N, hash H]
    T --> B[New record: sequence N+1,<br/>previousHash = H]
    B --> HS[hash = SHA-256 of canonical fields + previousHash]
    HS --> A[Append line, flush, release lock]
    A --> P[Publish to live subscribers]
```

`GET /api/audit/verify` recomputes every hash; editing or deleting any past record breaks the chain at that sequence.

## 11. Create a short link

```mermaid
flowchart TD
    S([POST /api/v1/links]) --> RL{Per-client rate limit}
    RL -- exceeded --> E0[429 + Retry-After]
    RL -- ok --> U{DestinationUrl valid?<br/>http/https · no credentials ·<br/>not private / loopback / metadata IP}
    U -- no --> E1[400 problem details]
    U -- yes --> SR{Points back to<br/>this shortener?}
    SR -- yes --> E2[400 url.self_reference]
    SR -- no --> EX{Expiry within policy?}
    EX -- no --> E3[400]
    EX -- yes --> T[Issue owner token<br/>store SHA-256 hash only]
    T --> AL{Custom alias?}
    AL -- yes --> V{Valid, not reserved?} -- no --> E4[400]
    V -- yes --> INS
    AL -- no --> G[Generate 7-char base62 code] --> INS[(Insert — unique index)]
    INS -- duplicate alias --> E5[409 code.taken]
    INS -- generated collision --> R{Attempts left?} -- yes --> G
    R -- no --> E6[503 code.exhausted]
    INS -- ok --> OK[201 + Location + token returned once]
```

## 12. Redirect (hot path)

```mermaid
flowchart TD
    S([GET /code]) --> C1{L1 memory cache}
    C1 -- hit --> CHK
    C1 -- miss --> C2{L2 Redis}
    C2 -- hit --> F1[Fill L1] --> CHK
    C2 -- miss / Redis down --> DB[(SQL Server)]
    DB --> NEG{Found?}
    NEG -- no --> NC[Cache 'missing' for 30s] --> E404[404]
    NEG -- yes --> F2[Fill L1 + L2] --> CHK
    CHK{Disabled or expired?}
    CHK -- yes --> E410[410 Gone]
    CHK -- no --> TRK[Publish click event<br/>non-blocking]
    TRK --> R[302 + Cache-Control: no-store]
```

## 13. Click analytics pipeline

```mermaid
flowchart LR
    R[Redirect replicas] -->|XADD, approximate MAXLEN| S[(Redis Stream)]
    S -->|XREADGROUP| W1[Worker 1]
    S -->|XREADGROUP| W2[Worker 2]
    W1 & W2 -->|XAUTOCLAIM idle entries<br/>from crashed consumers| S
    W1 & W2 --> P[Batch persist in one transaction:<br/>skip already-stored IDs ·<br/>insert events · increment counters]
    P -->|success| ACK[XACK]
    P -->|failure| RETRY[Bounded retry with backoff,<br/>entry stays pending]
```

Delivery is at-least-once; persistence is idempotent on the click ID, so redelivery never double-counts.
