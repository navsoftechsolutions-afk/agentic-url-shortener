# Engineering summary

**Objective:** A reviewable .NET 8 URL shortener and bounded agentic SDLC orchestration demonstration.

**Key decisions:** Minimize setup with .NET and file-based persistence. Model tasks as a dependency DAG with durable artifacts, parallel branches, synchronization, retries and human-controlled release-readiness. No third-party LLM key required; this keeps the demo reproducible but limits autonomy.

**Artifacts:** HTTP service, workflow engine, xUnit tests, architecture, three scenarios, setup/CLI instructions, audit and metrics endpoints.

**Trade-offs:** JSON persistence is suitable for one local process, not distributed traffic. Agent evidence is deterministic and does not modify code. Approval identity is user-provided, not authenticated. Rollback cancels release readiness, not infrastructure. Reliability metrics are prototypes, not production SLOs.

**Risks:** Public redirect abuse, unsafe destinations, unauthenticated approval, file durability and crash recovery, no distributed locks, and no sandbox for generated code. Treat this as a local interview prototype.

**Validation:** Run `dotnet test` and manually exercise greenfield/brownfield/ambiguous workflows, retries, replan, rejection, analytics and disable. Record actual results rather than claiming unexecuted tests passed.

**Executed results:** All 9 tests and the documented live HTTP workflow checks passed. See [VALIDATION.md](VALIDATION.md) for the environment, checks, and initial issues corrected.

**Next production steps:** authenticated approvals, database transactions, real agent/LLM adapters with tool permissions, sandboxed PR generation, CI test execution, OpenTelemetry, durable queue, replay/recovery and threat model.
