# 08 — Security

## 1. URL shortener

| Threat | Control |
|--------|---------|
| Open redirect to internal systems (SSRF-style abuse) | `DestinationUrl` rejects loopback, private, link-local (incl. cloud metadata `169.254.169.254`), CGNAT, IPv6 ULA/link-local, IPv4-mapped private addresses and `.local` / `.internal` / `localhost` hosts |
| Script and file schemes | Only `http` and `https` are accepted (`javascript:`, `data:`, `file:` rejected) |
| Credential phishing in URLs | `user:pass@host` rejected |
| Redirect loops | Links to the shortener's own host rejected |
| Malicious destinations (ambiguous scenario) | Configuration-driven domain blocklist, subdomain-aware |
| Route shadowing | Aliases cannot use reserved paths (`api`, `health`, `swagger`, …) |
| Unauthorised management | Per-link owner token (256-bit random), only SHA-256 stored, constant-time comparison, length-capped input |
| Abuse / scraping | Per-client fixed-window rate limits (create, management, redirect) at the service; timeouts and circuit breakers at the gateway; 429 + `Retry-After` |
| Client address spoofing | Gateway overwrites `X-Forwarded-For`; services trust forwarded headers only when configured (`Http:TrustForwardedHeaders`) with a forward limit of 1 |
| Code enumeration load | Negative caching of unknown codes |
| Personal data | No raw IPs stored; keyed HMAC visitor fingerprint; referrer host only; header values truncated |
| Oversized requests | Kestrel body limit 16 KB |
| Information leakage | No server banner; RFC 9457 problem details with stable codes, no stack traces |
| Browser hardening | `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, CSP `default-src 'none'` on API responses, `Cache-Control: no-store` on redirects |
| Injection | EF Core parameterised queries only; no raw SQL |
| Secrets | None in source; connection strings and keys from environment / `deploy/.env` (git-ignored); compose fails fast if they are missing |
| Vulnerable dependencies | NuGet audit as error in the build; a vulnerable transitive package was pinned to a patched version |
| Containers | Non-root runtime user, minimal runtime image |

## 2. Orchestrator

| Threat | Control |
|--------|---------|
| Agent escapes its sandbox | Workspace paths resolved and checked against the workspace root; `security.workspace-boundary` denies `..` / absolute paths in change sets |
| Agent runs arbitrary commands | Tool allow-list (`dotnet`, `git`); working directory must be inside the workspaces root |
| Secrets leak to agents or generated code | Environment scrubbed of `*KEY*`, `*TOKEN*`, `*SECRET*`, `*PASSWORD*`, connection strings before any tool runs; `security.secret-leak` denies committed credentials |
| Unsafe code merged | Policies for insecure APIs, PII logging, protected paths, dependencies, destructive schema; human approval for escalations |
| Self-approval / insufficient authority | Registered approvers only, risk ceilings per role, segregation of duties (initiator cannot approve) |
| Tampering with history | Hash-chained audit with verification endpoint; exclusive-lock appends |
| Runaway automation | Per-node timeouts, bounded retries, per-run safe-stop, global kill switch |
| Untrusted LLM output | Applied only in the sandbox after `git apply --check`; same gates, policies and approvals as any change |
| LLM key exposure | Key read from an environment variable at call time; never logged, persisted or passed to tools |
| Path traversal via run IDs | Run IDs validated against a strict character set before touching the file system |
| Dashboard XSS | Every dynamic value HTML-escaped; Markdown renderer escapes first, then adds its own tags; strict CSP (`script-src 'self'`, no inline script, `object-src 'none'`, `frame-ancestors 'none'`) |
| ReDoS in policy regexes | All regexes compiled with a match timeout |

## 3. Production hardening not in the prototype

| Area | Next step |
|------|-----------|
| Identity | OIDC for dashboard and approval APIs; approver identity from the token instead of a header |
| Sandboxing | Run agents in ephemeral containers with no network except a package mirror |
| Secrets | Managed secret store (Key Vault) with workload identity |
| Transport | TLS everywhere, mTLS between services |
| Audit | Ship audit records to WORM storage / SIEM |
