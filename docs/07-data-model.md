# 07 — Data Model

## 1. URL shortener (SQL Server)

```mermaid
erDiagram
    ShortLinks ||--o{ ClickEvents : "has"
    ShortLinks {
        uniqueidentifier Id PK "UUID v7 (time-ordered)"
        varchar32 Code UK "case-sensitive (BIN2 collation)"
        nvarchar2048 Destination
        varchar64 ManagementTokenHash "SHA-256 of owner token"
        datetimeoffset CreatedAt
        datetimeoffset ExpiresAt "nullable"
        datetimeoffset DisabledAt "nullable, soft delete"
        bit IsCustomAlias
        bigint ClickCount "batched counter"
    }
    ClickEvents {
        uniqueidentifier Id PK "idempotency key"
        uniqueidentifier ShortLinkId FK
        datetimeoffset OccurredAt
        date OccurredOn "day bucket for aggregation"
        nvarchar255 ReferrerHost "host only"
        nvarchar512 UserAgent "truncated"
        varchar64 VisitorHash "HMAC, never raw IP"
    }
```

| Design choice | Reason |
|---------------|--------|
| UUID v7 keys | Insert-friendly ordering without exposing a sequence |
| Unique index on `Code` with binary collation | Case-sensitive codes; collisions detected by the database, not by a racy read |
| Soft delete via `DisabledAt` | Analytics history retained; a disabled code is never reissued |
| `ClickCount` denormalised | Link details and caps read one row; updated in the same transaction as the events |
| `OccurredOn` day bucket + index `(ShortLinkId, OccurredOn)` | Daily series and window queries are index range scans |
| Referrer host only | Full referrer URLs can carry tokens |
| `VisitorHash` | Unique visitors without storing personal data |

Migrations: `InitialCreate` (baseline). Brownfield scenario adds `AddMaxClicks` (nullable `int`, additive).

## 2. Orchestrator run state

```mermaid
erDiagram
    WorkflowRun ||--|{ WorkflowNode : "plan"
    WorkflowRun ||--|{ NodeState : "state per node"
    WorkflowRun ||--o{ Artifact : "produces"
    WorkflowRun ||--o{ Decision : "lineage"
    WorkflowRun ||--o{ ApprovalRequest : "governance"
    WorkflowRun ||--o{ ClarificationQuestion : "open questions"
    WorkflowRun {
        string Id
        string Title
        enum Kind
        string Requirement
        string InitiatedBy
        enum Status
        int PlanVersion
        map Settings
    }
    WorkflowNode {
        string Id
        enum Stage
        string Agent
        string FallbackAgent
        list DependsOn
        list EntryGates
        list ExitGates
        enum Risk
        bool RequiresApproval
        bool Compensable
    }
    NodeState {
        enum Status
        int Attempts
        int Executions
        string ExecutedBy
        string LastError
        list GateOutcomes
    }
    Artifact {
        string Key
        string Kind
        string ProducedBy
        int Version
        string Hash
        map DerivedFrom "input hashes"
    }
    Decision {
        string Id
        string NodeId
        string Actor
        string Summary
        string Rationale
        list Alternatives
        map InputHashes
        int PlanVersion
    }
    ApprovalRequest {
        string Id
        string NodeId
        enum Risk
        string Reason
        list Evidence
        enum Status
        string DecidedBy
    }
```

Storage layout (under the data root, default `.data/`):

| Path | Content |
|------|---------|
| `runs/<runId>.json` | Full run state, written atomically after every transition |
| `audit.jsonl` | Append-only, hash-chained audit records for all runs |
| `workspaces/<runId>/` | Git sandbox for the run (baseline commit, checkpoints, release tag) |
