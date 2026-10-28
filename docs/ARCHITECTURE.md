# Architecture and control flow

## Components
- `Program.cs`: minimal HTTP API and validation/error mapping.
- `UrlService`: URL creation, redirect lookup, click tracking, expiration, disable.
- `JsonStore<T>`: single-process serialized access and atomic file replacement.
- `WorkflowEngine`: explicit DAG scheduler, task state, parallel paths, retry, approval, replan, audit, metrics.
- `WorkflowState`: durable cross-stage artifacts, revision, decisions, attempts, node statuses.

## Gates and autonomy
Entry: requirement nonempty and scenario allowed. Each DAG node runs only after all dependencies complete. Independent design/security and tests/documentation nodes are scheduled concurrently with `Task.WhenAll`. Approval node pauses workflow. Only named human approval unlocks release-readiness node. Rejection cancels pending release; no deployed infrastructure is rolled back. Completed runs cannot be rewritten.

## Failure behavior
A node retries at most 3 attempts with small backoff. Exhaustion triggers SafeStopped. Replan invalidates downstream outputs, clears prior approval, increments revision and logs the change. An injected test error demonstrates retry. No external side effects are performed by agents.

## Observability
Each transition records timestamp, event and detail. Metrics: completion ratio among terminal runs, retry count, rollback count, end-to-end latency and a workflow-duration proxy for MTTR. This is not incident-level MTTR. State is stored on disk and survives application restarts; in-flight tasks do not automatically resume after a crash.

## Security and production gaps
No authentication, authorization, rate limits, SSRF/private-IP destination restrictions, malware detection, redirect abuse reporting, PII redaction, signed approval, multi-process concurrency, or real distributed tracing. Do not expose publicly. Production design: authenticated roles, allow/deny-list and DNS/IP checks, rate limiting, SQL transactions, queue/lease-based durable workers, immutable audit sink, OTel traces, LLM tool sandbox, repository PR-only changes, CI test evidence and signed releases.
