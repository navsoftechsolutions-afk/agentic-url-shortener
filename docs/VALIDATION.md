# Executed validation results

Verified on 2026-10-28 with .NET SDK 8.0.427 / runtime 8.0.33 on macOS ARM64.

## Repository test suite

```bash
dotnet test tests/AgenticUrlShortener.Tests/AgenticUrlShortener.Tests.csproj
```

Result: **40 passed, 0 failed, 0 skipped**. The real candidate suites executed by integration tests are additional nested tool runs, not extra tests counted in the 40-test total.

Coverage includes URL scheme/credential rejection, expiry bounds and exact expiry time, state reload, unique concurrent creation and lossless click accounting; actual concurrent DAG branches and joins; private stage-input snapshots; dependency hashes; changed brownfield graphs and revision history; clarification; retry/recovery timing; retry exhaustion; restricted fallback; rejection/quarantine; authenticated role separation and spoof/stale/self-approval prevention; evidence/journal corruption; restart recovery; candidate compilation and xUnit execution; a running candidate's HTTP endpoints and OpenAPI schema; binary tampering and unexpected source hidden in data directories.

The real tool-failure regression asserts that a failing candidate cannot use fallback to pass review. The fallback integration test actually runs a package-free console verifier using the same three contract-check methods as the generated xUnit suite.

## Reproducible live HTTP demonstration

With the controller running and fault injection explicitly enabled:

```bash
python3 scripts/verify_workflow.py --failure-demos --output /tmp/agentic-workflow-evidence.json
```

Executed checks:

- Public docs/schema and authenticated management access; anonymous management requests return 401.
- Real URL creation, HTTP 302 redirect, persisted click analytics, disable, and HTTP 404 afterward.
- Greenfield generation of HTTPS-only/expiration/analytics code, execution of three generated contract tests, one injected retry, pause at approval, blocked operator approval, and completion after a separate reviewer's decision.
- Brownfield requirement change, compatibility-node insertion, archived old evidence, blocked stale approval, regeneration/testing, and revision-2 completion.
- Ambiguous requirement stops before implementation; explicit clarification regenerates tested code; rejection quarantines the candidate.
- Persistent injected failure exhausts three attempts, records two retries, safely stops, and quarantines. Explicit replan clears the fault and requires fresh validation.
- Primary-runner outage exhausts retries, executes the equivalent package-free verifier, and records fallback/recovery.
- Metrics expose recovered/unrecovered incidents and measured failure-to-recovery intervals.

[DEMO_RESULTS.json](DEMO_RESULTS.json) records credential-free workflow snapshots, actual test counters, audit event types and cumulative metrics from the executed demonstration. Snapshots capture each demonstrated state; a persistent-failure workflow is subsequently replanned during the script. Local state includes prior demonstration runs, so cumulative totals need not equal the number of snapshots.

## Build and publication boundary

A release `dotnet publish` succeeded. Its output was checked to exclude access keys, URL/workflow state and generated candidate workspaces. The controller also rebuilt successfully with generated applications present under its default data directory. Its project file explicitly limits compile/content inputs, preventing those applications from entering the controller build or publication.

GitHub Actions repeats the test suite, the HTTP demonstration, and the build/publication regression on pushes and pull requests. Generated state and credentials are ignored by Git and are not committed.

## Practical limits

The workflow generates real files and executes real tools. Requirement interpretation and code composition remain deterministic and limited to the documented features. Source assertions are narrow checks, not a full security analyzer. Candidates are local review artifacts with no authentication; execution is not OS-sandboxed; the JSON store is single-process; and local audit chains do not defeat privileged rewriting of both state and journal. No production deployment was performed.
