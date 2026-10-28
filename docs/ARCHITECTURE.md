# Architecture and governance

## Components

| Component | Responsibility |
| --- | --- |
| `ApplicationHost` / `AccessKeys` | HTTP contracts, request validation, role authentication and authorization |
| `UrlService` | Cryptographic codes, bounded input, expiration, click accounting, disable |
| `JsonStore<T>` | Serialized single-process updates, flushed temporary files and atomic replacement |
| `WorkflowEngine` | Stateful dependency graph, joins, retry/fallback decisions, revision control and human gates |
| `EngineeringStages` | Source inspection, generation, real testing, review, compatibility checks and manifests |
| `ToolRunner` | Fixed dotnet argument lists, filtered environment, output limits and process-tree timeout |
| `AuditJournal` | Separately appended, flushed hash-chain records and state-prefix verification |
| `templates/candidate` | Trusted application, contract-test and package-free verifier templates |

## Requirement understanding and codebase reasoning

The parser recognizes analytics/clicks, expiration/expiry, and HTTPS-only requirements. Ambiguous security requirements stop before planning until explicit criteria are supplied. This is a bounded rule-based generator, not general natural-language engineering or an LLM adapter.

The design stage asynchronously reads `UrlService.cs`, `UrlModels.cs`, and `JsonStore.cs`, recording actual hashes and line counts. The security stage independently reads source and enforces narrow checks for cryptographic codes, credential rejection, expiry bounds and atomic replacement. Contract tests validate corresponding runtime behavior. The implementation stage verifies that inspected source has not changed before copying it into a candidate and generating a feature policy.

## Dependency graph and gates

Requirements -> planning -> concurrent design/security -> implementation -> concurrent tests/documentation -> review -> approval -> release readiness. Brownfield expiration adds a compatibility node between design and implementation. That node reads the existing URL store through the current schema and checks nullable expiration compatibility; it performs no destructive migration.

A node runs only when every dependency is completed. Every stage receives a private state snapshot and can publish state only through its returned result, preventing sibling stages from mutating shared dictionaries or bypassing governance. Each result carries a content hash, revision, dependency-result hashes and generated-file hashes. Tests consume the generated implementation, documentation consumes normalized criteria and implementation evidence, and review verifies passing test counters, source files and input lineage. A unit test uses a rendezvous between design and security to prove overlap and checks that implementation starts after both complete. Real stages perform asynchronous file/process I/O.

## Artifacts and decisions

Candidates are generated under `DATA_DIR/workspaces/{workflow}/r{revision}/`. The generator writes a runnable application, feature policy, OpenAPI schema, xUnit tests, shared contract assertions, a package-free verifier, and review documentation. Actual test logs, TRX counters, and candidate assembly/runtime manifests become hashed evidence. The controller project explicitly includes only its own top-level source and API schema; generated workspaces and credentials are excluded from compilation and publication. Only trusted code/templates become executable; requirement text is recorded as data.

The operator creates and replans workflows. A distinct authenticated reviewer must provide the exact revision, review digest, and a reason. Identity comes from the API key; a supplied reviewer name must match. Review records the evidence seal in the hash-chained journal; approval recomputes the seal and verifies file contents and the set of source inputs. Changed or added files block readiness. A successful decision writes `RELEASE.json`; this never runs a deployment.

Quarantine is an enforced workflow state and marker; it does not revoke the local OS user's ability to run files manually. Replanning quarantines the prior candidate, archives its artifact/evidence lineage in revision history, clears demonstration faults and approval, recomputes the graph, and requires all gates again. Rejected and safe-stopped candidates carry `QUARANTINED.json`. A release manifest, if present during rollback, is moved to `REVOKED-RELEASE.json`. Completed workflows cannot be replanned.

## Failure and recovery

Retryable tool failures receive at most three attempts with bounded backoff. A real test assertion/build failure fails closed immediately. Only primary tool unavailability permits fallback: a package-free console application executes the same three shared contract-check methods as xUnit. Failed assertions cannot be converted into fallback success. Missing SDKs also prevent fallback and therefore stop the workflow.

Every tool invocation has a 120-second limit, drains stdout/stderr while retaining at most 200,000 characters per stream, avoids a shell, and terminates the process tree on timeout. A stage executor allows one tool at a time; the engine bounds concurrent creation/advance calls to two. Replans are serialized per workflow; this is a local prototype, not a durable job queue.

On restart, in-progress workflows do not silently resume external work. A valid durable journal tail can be reconciled with its last state snapshot, then the candidate is quarantined and safely stopped. A corrupt/truncated journal fails closed and requires operator investigation/restoration. Legacy v1 records remain readable, but decisions and verified audit operations require a new v2 workflow.

## Observability and reliability

Each journal event records sequence, timestamp, revision, actor, detail, previous hash and current hash. Journal records are appended and flushed independently of state snapshots. Audit reads verify the chain and snapshot prefix; control operations require exact agreement. This detects accidental alteration and divergence. It cannot prevent a privileged local attacker from rewriting both state and journal and recomputing hashes. Production requires an externally anchored immutable audit service.

Metrics distinguish current and legacy workflows and report completion among terminal runs, retry/rollback/fallback counts, recovered/unrecovered incidents, actual recovery intervals, and end-to-end latency. MTTR is the mean elapsed time from the first failed attempt to successful retry/fallback for each node/revision. Unresolved incidents are excluded from that mean and counted separately. End-to-end latency includes human waiting time.

## Boundaries and production work

The controller authenticates management APIs; generated candidates are unauthenticated local review artifacts. Keys represent two local roles, not verified individual enterprise users. API-key transport requires TLS beyond loopback. Source generation does not modify the maintained checkout or automatically merge/publish anything.

The tool workspace is an output boundary, not an OS sandbox: generated trusted code executes with the host user's filesystem/network privileges. Accepting arbitrary source or third-party templates would require a container/VM sandbox and resource quotas. JSON state supports one application process, not multiple writers or distributed transactions. Redirect abuse, private-network targets, rate limiting, data retention, SSO, external audit anchoring, queue-based recovery and a real LLM adapter remain production extensions.
