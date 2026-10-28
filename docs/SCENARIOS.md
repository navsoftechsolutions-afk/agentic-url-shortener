# Three required scenarios

## 1. Greenfield — new URL shortener
Input: `{"requirement":"Build URL shortening with redirect and analytics","scenario":"greenfield"}`.
Decomposition: requirements -> planning -> design/security (parallel) -> implementation -> tests/documentation (parallel) -> review -> approval -> release readiness.
Validation: xUnit URL lifecycle, input rejection, approval gate. Expected: `WaitingApproval`, then `Completed` after approval. Review `Artifacts` and `Audit`.

## 2. Brownfield — extend expiration
Input: `{"requirement":"Enhance existing short links with expiration","scenario":"brownfield"}`.
Decomposition: same dependency DAG; implementation scope references existing UrlService; inspect existing link model, resolution path and tests. Validation: `ExpiresInDays` range and expired link rejection. Demonstrate changed requirement with `/replan`, which invalidates outputs and approval. This is a workflow demonstration, not an automated source-code patch.

## 3. Ambiguous — secure links
Input: `{"requirement":"Make short links secure","scenario":"ambiguous","simulateTestFailure":true}`.
Requirements agent records unresolved definition of “secure” and a conservative assumption; security stage lists remaining risks. Injected test failure triggers bounded retry; audit shows recovery. Reviewer should reject until security expectations are clarified, or use `/replan` with explicit acceptance criteria. Validation: retry event, review gate, rejection rollback counter and no release.

## Failure controls
- Simulated transient test failure: one failed attempt followed by retry.
- Human rejection: marks workflow `Rejected`, cancels pending release and logs rollback.
- Upstream change: `/replan` increments revision, invalidates all dependent evidence, requires new approval.
- Retry exhaustion: code path exists but no public failure-injection switch for persistent failure.
