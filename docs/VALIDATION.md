# Validation results

Verified on 2026-10-28 with .NET SDK 8.0.427 on macOS ARM64.

## Automated tests

Command:

```bash
dotnet test tests/AgenticUrlShortener.Tests/AgenticUrlShortener.Tests.csproj
```

Result: **9 passed, 0 failed, 0 skipped**.

The initial run exposed a compiler error in the conditional requirements arrays and a stale-object assertion in the URL lifecycle test. The arrays now have explicit types. The lifecycle test checks the resolved link, persisted click count, and persisted click event.

## Live HTTP checks

Started the app with the README command on `http://localhost:5080` and exercised the documented endpoints with HTTP requests and assertions:

- `/docs`: HTTP 200 with the endpoint directory.
- URL lifecycle: HTTP 201 creation, 10-character code, HTTP 302 redirect, one recorded click in analytics, HTTP 204 disable, then HTTP 404 on redirect.
- Greenfield workflow with an injected test failure: `WaitingApproval`, release pending with no release artifact, one retry, two test attempts, and error/retry/approval-required audit events.
- Approval: the named reviewer was recorded; the workflow became `Completed`, all DAG nodes completed, and the completed state was returned by a subsequent GET.
- Brownfield replan: revision advanced to 2, rollback count increased, and the workflow required approval again with release still pending.
- Ambiguous workflow: clarification was recorded; rejection produced `Rejected`, recorded a rollback, and left release pending with no release artifact.
- Metrics: three workflows, one completed, two retries, and two rollbacks. The remaining workflows were one awaiting approval and one rejected.

The workflow's engineering outputs remain deterministic simulations as described in the README. The test-suite and HTTP results above were actually executed. Generated local JSON data and build output are excluded from Git.
