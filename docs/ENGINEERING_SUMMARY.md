# Engineering summary

**Objective:** Transform a supported URL-shortener requirement into generated, executed and reviewable engineering output under explicit human governance.

**Plan and rationale:** Keep the stack reproducible with .NET 8 and local persistence. Use a dependency graph with concurrent branches and joins; record immutable-by-convention stage evidence and dependency hashes; generate trusted candidate code outside the maintained checkout; validate it with real tools; bind a separate reviewer's decision to the exact revision and evidence digest.

**Implemented artifacts:** URL API with persisted click analytics, expiration and disable; requirement normalization and clarification; source inspection and impact evidence; dynamic brownfield compatibility gate; candidate source, feature policy, API schemas, contract tests and review documents; real dotnet execution; bounded retry, restricted fallback, quarantine/manifest revocation and safe-stop; role-authenticated control APIs; hash-chain journals, revision history and incident-based metrics; API integration tests and a reproducible HTTP verification script.

**Assumptions:** One application process owns each data directory. Source and templates are trusted. Requirements are limited to analytics, expiration and HTTPS-only policies. Operator and reviewer keys are held by different people. The controller and generated candidates remain on loopback. SDK/NuGet packages are available or can be restored initially.

**Engineering judgment:** A deterministic generator is predictable, inspectable and runnable without an LLM key, but has deliberately limited scope. Generated candidates execute real code and tests; this is not general AI code synthesis. The system controls output locations and commands, but does not provide an OS-level sandbox. Quarantine revokes local release readiness rather than reverting deployed infrastructure. No automatic deployment occurs.

**Validation:** The suite covers actual file persistence, expiration boundaries, concurrent URL operations, branch overlap/join ordering, dependency lineage, changing graphs, clarification, bounded failures, equivalent fallback, stale/self/spoofed approvals, evidence tampering, audit corruption, interrupted execution, API role boundaries, and generated candidate compilation/tests/HTTP behavior. See [VALIDATION.md](VALIDATION.md) for executed results.

**Trade-offs and risks:** Local JSON is suitable for a prototype, not distributed traffic. Role keys authenticate possession, not individual identity. Local hash chains detect divergence but do not defeat privileged disk tampering. Redirect destination abuse/private-network targets remain possible. No general LLM adapter, arbitrary repo refactoring, distributed scheduler, tenant isolation, retention policy or production SLOs are claimed.

**Next production work:** Transactional database and durable queue/leases; managed identity/SSO and TLS; external immutable audit anchoring; VM/container execution isolation, network controls and resource quotas; redirect abuse controls/rate limits; structured agent adapters with restricted tools and PR-only generated changes; CI/release integration and operational telemetry.
