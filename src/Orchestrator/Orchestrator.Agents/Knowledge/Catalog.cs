namespace Orchestrator.Agents.Knowledge;

public sealed record Capability(
    string Id,
    string Title,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> ImpactedSymbols,
    IReadOnlyList<string> ApiChanges,
    IReadOnlyList<string> DataChanges,
    IReadOnlyList<DesignChoice> Decisions,
    IReadOnlyList<PlannedTask> Tasks,
    bool RequiresSchemaChange = false);

public sealed record DesignChoice(string Decision, string Rationale, IReadOnlyList<string> Alternatives);

public sealed record PlannedTask(string Id, string Title, string Layer, IReadOnlyList<string> DependsOn, string Verification);

public sealed record VagueTerm(string Term, string Category, string Question, string SuggestedDefault);

public static class Catalog
{
    public static readonly IReadOnlyList<Capability> Capabilities =
    [
        new("shorten-api", "Create short links",
            ["url shortener", "create short", "shorten a", "shorten url"],
            ["POST /api/v1/links returns 201 with code, short URL and an owner management token",
             "Invalid, non-http(s), credential-bearing or private-network URLs are rejected with 400 problem details",
             "Generated codes are 7-char base62, collision-retried with a bounded attempt count"],
            ["ShortLinkService", "DestinationUrl", "ShortCode", "LinkEndpoints"],
            ["POST /api/v1/links", "GET /api/v1/links/{code}", "DELETE /api/v1/links/{code}"],
            ["ShortLinks table with unique, case-sensitive Code index"],
            [new("Owner-scoped management tokens instead of global API keys",
                "Each link is managed by whoever created it; only a SHA-256 hash is stored and comparison is constant-time",
                ["Global admin API key", "Full OAuth/OIDC (out of scope for prototype)"])],
            [new("T-create-domain", "ShortCode and DestinationUrl value objects with SSRF guards", "Domain", [], "Unit tests for valid/invalid inputs"),
             new("T-create-api", "Create/get/disable endpoints with problem details", "Presentation", ["T-create-domain"], "Integration tests")]),

        new("redirect", "Redirect visitors",
            ["redirect", "visitors", "resolve"],
            ["GET /{code} returns 302 to the destination with Cache-Control: no-store",
             "Unknown codes return 404; expired or disabled links return 410",
             "Redirect path never blocks on analytics persistence"],
            ["ShortLinkService", "RedirectEndpoints", "LinkRoute", "ILinkRouteCache"],
            ["GET /{code}"],
            [],
            [new("Separate Redirect service from the management API",
                "Read-heavy hot path scales independently and can be cached aggressively",
                ["Single monolithic API", "Edge/CDN redirect only"]),
             new("Two-tier route cache (in-process + Redis) with pub/sub invalidation",
                "Sub-millisecond hits, shared warm cache across replicas, negative caching shields the database from code scanning",
                ["Database only", "Redis only"])],
            [new("T-redirect", "Redirect endpoint with tiered cache", "Presentation", ["T-create-domain"], "Integration tests for 302/404/410")]),

        new("analytics", "Click analytics",
            ["analytics", "click", "stats", "statistics", "track"],
            ["Clicks are captured asynchronously and persisted in batches",
             "Analytics endpoint returns total, window, daily series, top referrers and unique visitors",
             "No raw IP addresses are stored; visitors are identified by keyed HMAC"],
            ["IClickTracker", "ClickEvent", "AnalyticsReader", "ClickSink"],
            ["GET /api/v1/links/{code}/analytics"],
            ["ClickEvents table indexed by (ShortLinkId, OccurredOn)"],
            [new("Redis Streams with consumer groups for click ingestion",
                "Decouples redirect latency from writes, supports horizontally scaled workers and at-least-once delivery with idempotent persistence",
                ["Synchronous database insert", "Kafka (operationally heavier)", "Fire-and-forget logging"])],
            [new("T-analytics", "Click pipeline and analytics query", "Infrastructure", ["T-redirect"], "Integration test polls analytics")]),

        new("reliability", "Reliability and abuse protection",
            ["reliab", "rate limit", "health", "resilien", "under load", "availability"],
            ["Per-client fixed-window rate limits return 429 with Retry-After",
             "/health/live and /health/ready expose liveness and dependency readiness",
             "Redis or analytics outages degrade gracefully without failing redirects"],
            ["RateLimitPolicies", "WebHostingExtensions", "RedisHealthCheck"],
            [],
            [],
            [new("Rate limiting at both gateway and service",
                "Gateway protects the fleet; per-service limits protect each instance even if the gateway is bypassed internally",
                ["Gateway only", "Service only"])],
            [new("T-reliability", "Rate limiting, health checks and graceful degradation", "Presentation", ["T-create-api"], "Rate limit integration test")]),

        new("custom-alias", "Custom aliases",
            ["alias", "custom code", "vanity"],
            ["Aliases are 4-32 chars of [A-Za-z0-9_-], case-sensitive, and cannot shadow reserved routes",
             "A taken alias returns 409"],
            ["ShortCode", "ShortLinkService"],
            [],
            [],
            [],
            [new("T-alias", "Alias validation and conflict handling", "Domain", [], "Unit tests")]),

        new("expiry", "Link expiry",
            ["expir", "ttl", "time-limited"],
            ["Links may carry an expiry within the configured maximum; expired links return 410"],
            ["ShortLink", "LinkRoute"],
            [],
            [],
            [],
            [new("T-expiry", "Expiry validation and enforcement", "Domain", [], "Unit tests with a manual clock")]),

        new("click-cap", "Maximum-click limit",
            ["maximum-click", "max click", "maximum click", "click limit", "usage limit", "one-time", "burn after"],
            ["Links accept an optional maxClicks between 1 and 1,000,000",
             "Once ClickCount reaches maxClicks, the link stops redirecting with 410 and code link.exhausted",
             "Existing links (no limit) behave exactly as before",
             "The limit is visible in link details"],
            ["ShortLink", "LinkRoute", "ShortLinkService", "CreateShortLinkCommand", "CreateLinkRequest", "ShortLinkView", "ShortenerDbContext", "DomainErrors"],
            ["POST /api/v1/links accepts optional maxClicks", "GET /api/v1/links/{code} returns maxClicks", "GET /{code} returns 410 link.exhausted when the cap is reached"],
            ["ShortLinks.MaxClicks nullable int column (additive migration)"],
            [new("Capped links bypass the route cache and are checked against the batched click counter",
                "Uncapped traffic keeps the cached hot path; capped links (rare) read the current count, so overshoot is bounded by one analytics flush interval",
                ["Atomic decrement in the database on every redirect (strict, but a write per click)", "Redis INCR per click (strict, adds a Redis dependency to every redirect)", "Cache capped routes too (overshoot bounded only by cache TTL)"]),
             new("Nullable column instead of a sentinel value", "Additive, backward-compatible migration; no backfill required", ["Default of int.MaxValue"])],
            [new("T-cap-domain", "MaxClicks on ShortLink with validation and exhaustion rule", "Domain", [], "Unit tests"),
             new("T-cap-app", "Thread maxClicks through command, route and view; enforce on resolve", "Application", ["T-cap-domain"], "Service unit tests"),
             new("T-cap-data", "EF mapping and additive migration", "Infrastructure", ["T-cap-domain"], "Migration reviewed"),
             new("T-cap-api", "Expose maxClicks on the API contract", "Presentation", ["T-cap-app"], "Integration tests")],
            RequiresSchemaChange: true),

        new("destination-blocklist", "Destination domain blocklist",
            ["blocklist", "block list", "deny list", "malicious", "phishing", "blocked domain", "block malicious"],
            ["Links to configured blocked domains (and their subdomains) are rejected with 400 and code url.blocked_domain",
             "The blocklist is configuration-driven (Shortener:BlockedDomains) and needs no schema change",
             "Existing links are not retroactively disabled"],
            ["ShortLinkService", "ShortenerOptions", "DomainErrors", "DestinationUrl"],
            ["POST /api/v1/links may return 400 url.blocked_domain"],
            [],
            [new("Configuration-driven domain blocklist checked at creation time",
                "Smallest change that addresses the clarified threat; no new dependency or data migration",
                ["Third-party reputation API (latency, cost, availability)", "Blocklist table with admin API (larger scope)"])],
            [new("T-block-app", "Blocklist option and creation-time check", "Application", [], "Service unit tests"),
             new("T-block-api", "Problem details mapping and integration test", "Presentation", ["T-block-app"], "Integration tests")]),
    ];

    public static readonly IReadOnlyList<VagueTerm> VagueTerms =
    [
        new("fast", "performance", "What latency target applies (for example p95 redirect latency), and at what peak request rate?", "p95 redirect latency below 50 ms at 1,000 requests/s per instance"),
        new("quick", "performance", "What latency target applies, and at what peak request rate?", "p95 redirect latency below 50 ms at 1,000 requests/s per instance"),
        new("safe", "security-scope", "Which threat should this change address first: malicious destination domains, link enumeration, or abusive creation volume?", "Block malicious destination domains"),
        new("secure", "security-scope", "Which threat should this change address first: malicious destination domains, link enumeration, or abusive creation volume?", "Block malicious destination domains"),
        new("better", "success-measure", "What measurable outcome defines success for this change?", "The acceptance criteria in the clarified specification"),
        new("scalable", "capacity", "What peak traffic and growth should the design support?", "10x current peak with horizontal scaling"),
        new("robust", "reliability", "Which failure modes must be tolerated, and what availability target applies?", "99.9% monthly availability for redirects"),
        new("user-friendly", "usability", "Which user journeys should improve, and how will we measure it?", "Clear problem-details error messages for API clients"),
        new("soon", "schedule", "What is the target release date?", "Next scheduled release"),
    ];

    public static readonly VagueTerm ExistingDataQuestion = new(
        "existing", "data-scope", "Should the new rule apply retroactively to existing links?", "No - apply to new links only; existing links are reviewed separately");

    public static readonly IReadOnlySet<string> ExistingCapabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        "shorten-api", "redirect", "analytics", "reliability", "custom-alias", "expiry",
    };

    public static IReadOnlyList<Capability> Detect(string text) =>
        [.. Capabilities.Where(c => c.Keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))];

    public static IReadOnlyList<Capability> Detect(string text, bool brownfield)
    {
        var detected = Detect(text);
        return brownfield && detected.Any(c => !ExistingCapabilities.Contains(c.Id))
            ? [.. detected.Where(c => !ExistingCapabilities.Contains(c.Id))]
            : detected;
    }

    public static Capability? Find(string id) => Capabilities.FirstOrDefault(c => c.Id == id);
}
