# Three engineering scenarios

Reproduce all scenarios with `python3 scripts/verify_workflow.py --failure-demos` while the server is started with `ENABLE_FAILURE_INJECTION=true`. The script loads credentials without printing them. See the README for equivalent manual API commands.

## 1. Greenfield: generate a service

Requirement: `Build HTTPS-only URL shortener with expiration and click analytics`.

- Normalize supported features and acceptance criteria.
- Plan the DAG; inspect source and check policy on independent branches.
- Generate a separate runnable service, HTTPS/expiry/analytics policy, API schema, tests and review documents.
- Inject one transient runner fault, then run real xUnit contract tests on retry.
- Review hashes and passing results; stop at `WaitingApproval` with release pending.
- Reject operator self-approval. A separate reviewer approves the current revision/digest and receives `Completed` plus a readiness manifest.

Validation: repository tests actually compile the generated application, execute its generated tests, start its HTTP service on a random loopback port, reject HTTP targets, create/redirect/analyze/disable a link, and approve the sealed evidence.

## 2. Brownfield: add expiration to existing links

Initial requirement: `Add click analytics to existing links`. Replan to `Add expiration and click analytics to existing links` using the current revision.

- Identify the maintained service/model/store and record inspected hashes.
- Initial graph has no compatibility node.
- Requirement change quarantines revision 1, preserves its evidence, and increments revision to 2.
- The new plan adds a data-compatibility gate: existing JSON links load, and expiration remains nullable.
- Generate a new policy enabling expiration; run tests at the expiration boundary and after persistence reload.
- Reject a stale revision-1 approval; require a fresh reviewer decision for revision 2.

Validation: graph/lineage tests, current-revision API checks, generated contract checks and the complete HTTP demonstration. This does not claim an arbitrary refactoring agent: it extends the bounded supported service through generated policy and composition.

## 3. Ambiguous: define secure links

Initial requirement: `Make short links secure`, scenario `ambiguous`.

- Record the unresolved security criterion and stop at `WaitingClarification`.
- Do not generate code or run implementation tools while the criterion is unclear.
- Replan with `Require HTTPS-only links and click analytics`.
- Generate the explicit policy and run real validation.
- The reviewer can reject the candidate; rejection records the authenticated decision, quarantines the candidate, and leaves release pending.

Validation: clarification tests, replan history, source-policy assertions, runtime contract tests, rejection/quarantine checks and verified audit reads.

## Failure demonstrations

- `simulateTestFailure:true`: one injected transient failure, then real tests; one retry and a recovered incident.
- `failureMode:"persistent-tests"`: three failed attempts, two retries, no fallback, quarantine and `SafeStopped`. Explicit replanning clears the demonstration fault and requires new validation.
- `failureMode:"runner-unavailable"`: three failed primary attempts, then the package-free console verifier runs the same shared assertions. Record fallback and recovery.
- Actual failed contract tests: immediate safe-stop; no fallback can turn a failure into success.
- Added/changed source after review: approval safely stops and quarantines the candidate.
- Interrupted process: journal reconciliation followed by safe-stop; corrupt journal prevents resumption.
