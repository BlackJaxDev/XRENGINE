# One isolated Windows networking walkthrough

This tooling is for one explicitly approved browser/server walkthrough. Publishing these helpers does not activate certificate work. The new workflow initially performs only Windows PowerShell AST parsing, literal C# compilation, and JavaScript syntax checks. No request or activation record is included in the source change. Ordinary browser CI still runs for engine, helper and workflow changes. Its push filter skips only commits whose changed paths are entirely the two fixed request/activation records below, so publishing approval metadata does not cancel an ongoing engine run. A mixed metadata/code change still runs ordinary CI and does not satisfy this workflow's dedicated-request gate.

The immutable publisher input is recorded in `producer.json`: engine commit `b64f65a7fd7d619e47ec3defdfaa6733a35bbb4b`, run `37307383257`, attempt 1, successful Windows job `111754237186`, artifact `11346111752`, exactly 55,824,360 bytes, SHA-256 `9278a05d81f94c48d3c2c224f184ec5b41db9d2fa0b229580bbccb1be6884562`. The qualified producer compared the original native world with the genuine Editor publication. The consumer reuses that exact archive; it does not rebuild Editor/WASM, recook shaders, or regenerate another world.

## Execution and approval boundaries

1. Publish and independently review the inert helpers/workflow. The new workflow parses both PowerShell sources through AST APIs and compiles the owned-job C# literal without constructing its class or calling native methods. JavaScript is checked with `node --check`, never imported for static checking. Review the successful Windows static artifact before requesting preparation.
2. The coordinator may add the one request record described below in a separate, single-commit push changing only that new record. The existing branch is `codex/webgpu-readiness-audit`. Source-only changes, ordinary pushes, PRs, forced/deleted branch events, dispatches, request edits, multi-commit pushes and reruns cannot authorize preparation or trust.
3. The same single non-matrix Windows job repeats static checking, checks out the exact qualified engine commit separately from the helpers, provisions only the previously approved locked tooling, verifies/downloads the exact artifact, builds real Server and ControlPlane.Service, and runs the actual published browser first-frame preflight without a certificate, service, bearer or handoff.
4. `Prepare` returns only after the preflight browser/context and owned process tree have exited. Its state fingerprints every published-site file and every file in both native executable directories, plus the successful preflight and cleanup reports. The job uploads only the explicit sanitized prerequisite files, then waits read-only for at most ten minutes.
5. The coordinator must inspect that exact prerequisite artifact and obtain the required independent security review before publishing the exact-run activation record. The activation binds the request, helper/workflow bytes, trigger commit, run/attempt 1/database job ID, runner, prepared-state hash, static-result hash, prerequisite artifact ID/digest, scope and expiry. No runner writes an activation, creates a credential or obtains repository write permission.
6. `Run` revalidates the request and live GitHub job, all prepared file fingerprints, the original archive digest, prior successful preflight/cleanup reports, and the immutable activation. It also checks the current branch still carries those same activation bytes. Only then does exclusive `CreateNew` consume the one trust opportunity. Any existing, failed, partial, uncertain or interrupted claim remains consumed; no retry, sentinel deletion, alternate run directory, rerun or automatic reactivation is supported.

Keep three identities separate: reviewed helper commit A, dedicated request/launch commit B, and activation publication commit C. The running `GITHUB_WORKFLOW_SHA` identifies B. Request A-file hashes must equal both the checked-out B files and their immutable A contents. The activation is read at C but no scripts are executed from C. The immutable public records survive runner disposal; the local sentinel supplements this exact GitHub run/attempt/job binding rather than serving as the sole replay boundary.

The reviewed `.gitattributes` change forces LF only for this helper directory and this workflow file. Their raw Windows checkout bytes therefore remain identical to the immutable Git blobs even when the runner defaults to `core.autocrlf=true`. The admission gate compares raw SHA-256 values without line-ending normalization or relaxed hashes; unrelated repository text policy is unchanged.

## Bounded request schema

The fixed request path is `.github/diagnostic-requests/network-walkthrough-20261005.json`; it must be newly added by the dedicated push. JSON is bounded to 64 KiB, UTF-8, with exact keys and no duplicate/escaped-key ambiguity. All SHA-256 values are lowercase. `createdUtc` and `expiresUtc` define a window of at most 24 hours. The coordinator fills and reviews these fields; the example is not an activation:

```json
{
  "schema": 1,
  "requestId": "1b6a802b-34b4-4e71-9f85-a968334df1be",
  "repository": "BlackJaxDev/XRENGINE",
  "branch": "codex/webgpu-readiness-audit",
  "workflowPath": ".github/workflows/browser-network-walkthrough-once.yml",
  "helperCommit": "<reviewed-helper-commit-A>",
  "sourceCommit": "b64f65a7fd7d619e47ec3defdfaa6733a35bbb4b",
  "artifactSha256": "9278a05d81f94c48d3c2c224f184ec5b41db9d2fa0b229580bbccb1be6884562",
  "createdUtc": "<UTC-time>",
  "expiresUtc": "<UTC-time-within-24-hours>",
  "files": {
    ".github/workflows/browser-network-walkthrough-once.yml": "<SHA256>",
    "Tools/BrowserSmoke/network-walkthrough/Invoke-NetworkWalkthrough.ps1": "<SHA256>",
    "Tools/BrowserSmoke/network-walkthrough/run-real-network-walkthrough.mjs": "<SHA256>",
    "Tools/BrowserSmoke/network-walkthrough/admit-network-walkthrough.mjs": "<SHA256>",
    "Tools/BrowserSmoke/network-walkthrough/Test-NetworkWalkthroughStatic.ps1": "<SHA256>",
    "Tools/BrowserSmoke/network-walkthrough/producer.json": "<SHA256>",
    "Tools/BrowserSmoke/network-walkthrough/README.md": "<SHA256>"
  },
  "preflightSeconds": 180,
  "liveSeconds": 480,
  "cleanupSeconds": 60,
  "certificateMinutes": 30
}
```

## Post-prerequisite activation schema

The fixed path is `.github/diagnostic-activations/network-walkthrough-20261005.json`. The coordinator publishes it only after reviewing the successful prerequisite result and final security review for this one run. Discovering a record never authorizes a different run. A mismatched, expired or previously used record fails closed rather than being replaced or retried automatically.

```json
{
  "schema": 1,
  "requestId": "1b6a802b-34b4-4e71-9f85-a968334df1be",
  "requestSha256": "<exact-request-file-SHA256>",
  "helperCommit": "<reviewed-helper-commit-A>",
  "triggerCommit": "<dedicated-request-commit-B>",
  "workflowSha256": "<reviewed-workflow-file-SHA256>",
  "runId": "<one-run-ID>",
  "runAttempt": 1,
  "jobId": "<one-database-job-ID>",
  "runnerName": "<that-job-runner-name>",
  "preparedStateSha256": "<uploaded-prepared-state-file-SHA256>",
  "staticResultSha256": "<uploaded-Windows-static-result-file-SHA256>",
  "prerequisiteArtifactId": "<uploaded-prerequisite-artifact-ID>",
  "prerequisiteArtifactDigest": "sha256:<GitHub-prerequisite-archive-digest>",
  "approvedAtUtc": "<UTC-time>",
  "expiresUtc": "<UTC-time-within-one-hour-and-request-expiry>",
  "certificateMinutes": 30,
  "liveSeconds": 480,
  "cleanupSeconds": 60
}
```

At activation verification at least nine minutes must remain, and the overall CI job must retain the complete live/cleanup window. The live step also rejects excessive admission overhead before consuming trust. No actual record is generated by these helpers. Publishing an activation with a later helper/run/job or an edited scope requires new authorization; this mechanism grants no standing permission.

## Exact artifact and dependency assumptions

The runner reads GitHub with only `contents: read` and `actions: read`; checkout credentials are not persisted. Artifact download uses the existing authenticated GitHub CLI, copies binary stdout without emitting tokens or signed redirect URLs, checks the exact approved byte length and SHA-256 before extraction, and enforces download/expansion bounds. Extraction rejects links, reparse entries, device/escaping/ambiguous Windows paths, duplicate paths and nonregular entries. Every extracted file is created exclusively below a fresh contained root.

The consumer invokes the qualified source's `verify-network-kinematic-publication.mjs --published-only <site>`, which calls the shipping `validateSharedWorldPackage` and verifies every declared file's actual hash/length and the complete no-extra/link inventory. It compares the resulting world/package hashes, counts and lengths with `producer.json`. `nativeInputCompared:false` describes the consumer accurately; original-native identity/byte preservation is producer evidence. The expanded package's `asset.contentHash` correctly equals its newly computed `manifestHash`, not the old native-only package hash.

Provisioning reuses the already approved versions/routes: the qualified `global.json` through existing `setup-dotnet@v6`, Node 22 through `setup-node@v6`, locked `npm ci`, and the official Chromium revision selected by the existing Playwright 1.63.0 lockfile. Its browser cache is inside the disposable runner's temporary directory. Only the same recorded OscCore-NET9/OpenVR.NET gitlinks used by the successful Windows publisher are initialized. No WASM workload, Python command, shader compiler, alternate browser, new package/version, custom download source or new permission is added. Normal native builds can restore the existing declared NuGet dependencies through the repository's existing sources. Provisioning failures stop before trust; they do not authorize a substitute toolchain or broader installation.

This design assumes the existing repository writers/coordinator, reviewed workflow/helper commits, GitHub's job identity/artifact service and disposable hosted runner are trustworthy. Public records are authority maintained by the coordinator through existing repository access, not a new cryptographic signing or secret-storage service. It does not defend against a malicious repository administrator or compromised runner. Ordinary source changes and registry updates cannot silently change the approved file hashes or lockfile dependency identities.

## Browser, certificate, time and cleanup contracts

The exact published page is served on a fresh `http://127.0.0.1:<ephemeral>` origin. Both modes require the existing `EngineCanvasHost` to report running/presented, `HasPresentedCanvasFrame()` true and preparation state 1; renderer initialization alone is insufficient. Preflight closes fully before trust. The live mode uses real focused W key events, real managed admission, worker simulation counters and a fresh post-suspension reservation/handoff. This does not claim exact server/client pose agreement or complete replay/mobile/expiry qualification.

The service uses literal IPv4 loopback, advertises `localhost`, and issues `wss://localhost:15200/realtime`. The single certificate has SAN `DNS=localhost`, a nonexportable private key in `CurrentUser\My`, explicit UTC `NotBefore=now` rounded to certificate whole-second precision and `NotAfter=start+30 minutes`. Its positive validity interval is checked to be no more than 30 minutes before trust is added; the provider's default backdating is not used. Only its public certificate is added to `CurrentUser\Root`. The browser uses normal TLS validation; no ignored certificate errors, TLS flags, LocalMachine store, firewall change or signing certificate is used. The service accepts only the exact ephemeral page origin. A restrictive `connect-src` policy permits only that gateway plus same-origin/local blob/data requests; request routing and WebSocket observation add checks without proxying or replacing Chromium's WSS handshake.

The outer supervisor starts its eight-minute clock before certificate creation, bounds that contained creation child to 20 seconds, then gives the live Node/service/browser job only the remaining time. Node has independent bounded callbacks and a hard exit. The native wrapper assigns processes atomically to Windows Job Objects through `PROC_THREAD_ATTRIBUTE_JOB_LIST`, retains exact handles and uses kill-on-close. No image-name kill or unpinned PID lookup controls teardown. The existing production worker's nested job is preserved with no breakaway flag.

Cleanup waits up to 30 seconds for all owned members to exit, then gives a separate contained cleanup child 25 seconds to delete the exact Root entry, delete the exact My certificate with `-DeleteKey`, verify absence, and remove generated private worker/checkpoint files only after confirmed exit. A pending live cleanup marker replaces the earlier preparation marker before trust, so a hard interruption cannot make old successful cleanup evidence authorize uploads. The overall cleanup budget is one minute. The literal certificate/cleanup helper is regenerated from the verified wrapper in each phase; an old disk copy is never accepted as authority.

Configured synthetic API bearers stay in memory/service environment; the existing worker launcher explicitly removes those variables before its worker launch. Node/browser/service environments are allowlisted to exclude runner credentials, tracing and dumps. Browser handoffs remain transient and never enter URLs, browser storage, files or arbitrary exception logs. Production ACL-restricted `worker.json` and CurrentUser-DPAPI checkpoint/journal behavior remains unchanged.

## Evidence and remaining validation

Only explicit public-result files are uploaded, never directories, archives, service configuration, generated scripts, raw logs, browser profiles, handoffs, private workers or checkpoints. Prerequisite evidence is uploaded only after successful preparation/cleanup. Final evidence upload requires confirmed process exit, exact certificate removal, private-file removal and no cleanup errors. The authoritative final result must be `mode: Run`, `result: passed`, with successful cleanup; a preparation result is not a network pass.

Machine/agent termination, failed provider calls or a certificate creation failure before its exact thumbprint is recorded can prevent provable cleanup. Those outcomes consume any attempted trust action, suppress final uploads when cleanup is uncertain, and require disposal or exact manual cleanup of the disposable runner before reuse. Certificate expiry is not proof of private-key deletion. Cancellation is not used as normal teardown; concurrency does not cancel an active run.

Before activation, retain successful Windows AST/C# evidence, complete artifact/native/preflight evidence and an independent review of the exact final helper/workflow hashes. Source inspection or syntax checking alone does not prove Windows Job Object nesting, certificate-provider behavior, browser WSS, worker simulation or cleanup. The actual one-run walkthrough remains pending until its exact activation is published by the coordinator.
