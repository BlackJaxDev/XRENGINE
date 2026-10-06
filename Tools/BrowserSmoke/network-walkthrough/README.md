# One isolated Windows networking walkthrough

This tooling is for one explicitly approved browser/server walkthrough. Publishing these helpers does not activate certificate work. The new workflow initially performs only Windows PowerShell AST parsing, literal C# compilation, and JavaScript syntax checks. No request or activation record is included in the source change. Ordinary browser CI still runs for engine, helper and workflow changes. Its push filter skips only commits whose changed paths are entirely the two fixed request/activation records below, so publishing approval metadata does not cancel an ongoing engine run. A mixed metadata/code change still runs ordinary CI and does not satisfy this workflow's dedicated-request gate.

The immutable publisher input is recorded in `producer.json`: engine commit `e4a90cf2f865a08421bb68c293d27616465e1558`, run `37386054995`, attempt 1, successful Windows job `112019576000`, artifact `11380253911`, exactly 56,380,647 bytes, SHA-256 `ad562f2b62ab3c24361056ecc90aa3a9e9f1458e83eeb11213898af1e6e11f50`. The qualified producer compared the original native world with the genuine Editor publication. The consumer reuses that exact archive; it does not rebuild Editor/WASM, recook shaders, or regenerate another world.

## Execution and approval boundaries

1. Publish and independently review the inert helpers/workflow. The new workflow parses both PowerShell sources through AST APIs and compiles the owned-job C# literal without constructing its class or calling native methods. JavaScript is checked with `node --check`, never imported for static checking. Review the successful Windows static artifact before requesting preparation.
2. The coordinator may add the one request record described below in a separate, single-commit push changing only that new record. The existing branch is `codex/webgpu-readiness-audit`. Source-only changes and request-file deletion run static checks only. Ordinary pushes, PRs, forced/deleted branch events, dispatches, request edits, multi-commit pushes and reruns cannot authorize preparation or trust.
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
  "sourceCommit": "e4a90cf2f865a08421bb68c293d27616465e1558",
  "artifactSha256": "ad562f2b62ab3c24361056ecc90aa3a9e9f1458e83eeb11213898af1e6e11f50",
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

The browser launch requests the Chromium sandbox with Playwright's
`chromiumSandbox: true`. Earlier preparations inherited
[Playwright's sandbox-off default](https://playwright.dev/docs/api/class-browsertype#browser-type-launch-option-chromium-sandbox).
The helper keeps the four software-mode arguments from `smoke.config.mjs` in
their existing order. It appends `--use-webgpu-adapter=swiftshader` for this
walkthrough only. The existing `--enable-unsafe-webgpu` flag
[allows CPU adapters](https://chromium.googlesource.com/chromium/src/+/ae047a7ca076abd0d10f856ff9bb59639b6d8de3/gpu/command_buffer/service/webgpu_decoder_impl.cc)
and [bypasses Chromium's WebGPU adapter blocklist](https://developer.chrome.com/blog/supercharge-web-ai-testing).
Use this mode only with the trusted loopback pages and the existing request
route. The launch does not add Dawn `allow_unsafe_apis`, relax TLS, or change
certificate trust.

[Chromium 153.0.8010.12 GPU startup code](https://chromium.googlesource.com/codesearch/chromium/src/+/refs/tags/153.0.8010.12/gpu/ipc/service/gpu_init.cc)
preloads `vk_swiftshader.dll` when the WebGPU adapter selector requests
SwiftShader. [Chromium's WebGPU test configuration](https://chromium.googlesource.com/chromium/src/+/HEAD/third_party/blink/web_tests/FlagSpecificConfig)
pairs that selector with `--enable-unsafe-webgpu`.
[Dawn's test guide](https://dawn.googlesource.com/dawn/+/HEAD/webgpu-cts/README.md)
also documents the selector. These source facts support a launch candidate.
They do not prove that this runner used the sandbox or created a SwiftShader
adapter.

After the real first-frame check passes, `BrowserEnvironmentProof` reads the
current renderer device's `adapterInfo`. It does not request another adapter.
The public result contains only `swiftshader`, `other`, or `unavailable`, plus
an optional fallback boolean. A short-lived browser CDP session reads
`SystemInfo.getInfo`. The public result records `chromiumSandboxRequested: true`
and `webGpuAdapterRequested: 'swiftshader'` as launch requests. After the first
frame, it records `gpuProcessSandboxed` as the observed boolean.
[Chromium's GPU info](https://chromium.googlesource.com/chromium/src/+/a134480ae1adb62656040363afc9987d7adf4c78/gpu/config/gpu_info.cc)
supplies `sandboxed` as a boolean, and its
[SystemInfo handler](https://chromium.googlesource.com/chromium/src/+/0355b2473dd07c2fde13c657f783bf219b7869a3/content/browser/devtools/protocol/system_info_handler.cc)
places it in `gpu.auxAttributes`. The result contains no raw CDP reply or
adapter text. During preparation, the check fails before trust if the adapter
is not SwiftShader, the GPU process sandbox is not reported as enabled, or
proof is unavailable. The CDP boolean describes the GPU process. It does not
prove the renderer process sandbox state.

If the page fails, the browser probe reads the host's retained cold failure before cleanup. The public result contains only a presence flag and closed-list startup stage, exception kind, and engine condition labels. Unknown text maps to `other`. An absent host failure stays absent and does not identify a cause. The probe does not export page status text, exception text, stacks, request URLs, browser console records, or shader source. This classification does not change the first-frame gate or permit a network run after failed preparation.

The service uses literal IPv4 loopback, advertises `localhost`, and issues `wss://localhost:15200/realtime`. The single certificate has SAN `DNS=localhost`, a nonexportable private key in `CurrentUser\My`, explicit UTC `NotBefore=now` rounded to certificate whole-second precision and `NotAfter=start+30 minutes`. Its positive validity interval is checked to be no more than 30 minutes before trust is added; the provider's default backdating is not used. Only its public certificate is added to `CurrentUser\Root`. The browser uses normal TLS validation; no ignored certificate errors, TLS flags, LocalMachine store, firewall change or signing certificate is used. The service accepts only the exact ephemeral page origin. A restrictive `connect-src` policy permits only that gateway plus same-origin/local blob/data requests; request routing and WebSocket observation add checks without proxying or replacing Chromium's WSS handshake.

The outer supervisor starts its eight-minute clock before certificate creation, bounds that contained creation child to 20 seconds, then gives the live Node/service/browser job only the remaining time. Node has independent bounded callbacks and a hard exit. The native wrapper assigns processes atomically to Windows Job Objects through `PROC_THREAD_ATTRIBUTE_JOB_LIST`, retains exact handles and uses kill-on-close. No image-name kill or unpinned PID lookup controls teardown. The existing production worker's nested job is preserved with no breakaway flag.

Preparation also starts each native build in its own owned Job Object. Both builds
share the first 25 minutes of the invocation clock. This leaves the existing
three-minute preflight and one-minute cleanup windows inside the 30-minute step.
After each build's root exits, the helper records its exit code, stops remaining
members of that exact job, and requires zero active members before continuing.
The final cleanup check includes both build jobs. An uncertain exit blocks the
prepared-state record and activation. The build environment is allowlisted and
explicitly disables automatic SDK development-certificate creation. Private
minimal build logs have a four-MiB checked bound and are deleted with the private
working directory before prerequisite evidence is published. No build log is
uploaded. Public build evidence contains only result, exit code, remaining child
count and confirmed exit status.

Cleanup waits up to 30 seconds for all owned members to exit, then gives a separate contained cleanup child 25 seconds to delete the exact Root entry, delete the exact My certificate with `-DeleteKey`, verify absence, and remove generated private worker/checkpoint files only after confirmed exit. A pending live cleanup marker replaces the earlier preparation marker before trust, so a hard interruption cannot make old successful cleanup evidence authorize uploads. The overall cleanup budget is one minute. The literal certificate/cleanup helper is regenerated from the verified wrapper in each phase; an old disk copy is never accepted as authority.

Configured synthetic API bearers stay in memory/service environment; the existing worker launcher explicitly removes those variables before its worker launch. Node/browser/service environments are allowlisted to exclude runner credentials, tracing and dumps. Browser handoffs remain transient and never enter URLs, browser storage, files or arbitrary exception logs. Production ACL-restricted `worker.json` and CurrentUser-DPAPI checkpoint/journal behavior remains unchanged.

## Evidence and remaining validation

Only explicit public-result files are uploaded, never directories, archives, service configuration, generated scripts, raw logs, browser profiles, handoffs, private workers or checkpoints. Prerequisite evidence is uploaded only after successful preparation/cleanup. Final evidence upload requires confirmed process exit, exact certificate removal, private-file removal and no cleanup errors. The authoritative final result must be `mode: Run`, `result: passed`, with successful cleanup; a preparation result is not a network pass.

Machine/agent termination, failed provider calls or a certificate creation failure before its exact thumbprint is recorded can prevent provable cleanup. Those outcomes consume any attempted trust action, suppress final uploads when cleanup is uncertain, and require disposal or exact manual cleanup of the disposable runner before reuse. Certificate expiry is not proof of private-key deletion. Cancellation is not used as normal teardown; concurrency does not cancel an active run.

Before activation, retain successful Windows AST/C# evidence, complete artifact/native/preflight evidence and an independent review of the exact final helper/workflow hashes. Source inspection or syntax checking alone does not prove Windows Job Object nesting, certificate-provider behavior, browser WSS, worker simulation or cleanup. The actual one-run walkthrough remains pending until its exact activation is published by the coordinator.

Preparation run `37392995221` on trigger `5618fff2e3eb138596d7405afc9b5923ea7a2468`
failed before request re-verification or run-directory creation. PowerShell
resolved two installed `node.exe` applications and treated their combined
paths as one command name. Node and GitHub CLI resolution now selects the first
application in PATH order. No activation, service, preflight browser, or
certificate action occurred. The failed request is retired. A later preparation
requires a fresh reviewed request bound to the new helper bytes and successful
Windows static evidence; the failed job and its admission are not reused.

Preparation run `37397520088` on trigger
`24b7dfcefdfc9f509747a5f5f55e4e5370542516` passes request re-verification, then
rejects the checkout URL before run-directory creation. The official checkout
action records `https://github.com/BlackJaxDev/XRENGINE` without a `.git`
suffix. The helper now requires that exact canonical URL. Source commit,
clean-tree, artifact, and content-hash checks remain unchanged. This failed
request is also retired without an activation or trust claim. A fresh helper
static result and request review are required before another preparation.

Preparation run `37399174427` on trigger
`51adb83e4f07e400d601d92efb45263d7b68a579` passes the source, archive, and
published-package checks, then fails the Server build because the pinned
workload set is absent on this native-only runner. The two native build commands
now set `MSBuildEnableWorkloadResolver=false` for that invocation. SDK pins and
browser workload configuration are unchanged; no workload is installed here.
The same commands disable persistent managed build servers, node reuse, and
shared compilation. These flags do not contain all native compiler descendants;
the next preparation exposed that separate gap, described below.
The workflow also disables the SDK's automatic ASP.NET development-certificate
generation before setup or build commands. The separately activated, bounded
test certificate remains the only certificate operation in this workflow.

The failure report confirms `certificateMutationReached:false`,
`trustAttemptConsumed:false`, and complete owned-process/private-file cleanup
without errors. The failed request is retired. Local SDK evaluation reproduces
the workload-set failure without the property and resolves the original native
target framework with it. This verifies SDK evaluation only; native compilation
and browser preflight remain required on the next exact Windows preparation.

Preparation run `37402062022` on trigger
`9bf5da9276caf9bdb82807033cc6242cbe757420` verifies the published package and
builds the real Server and ControlPlane.Service with zero warnings and zero
errors. The published browser then fails its first-frame gate. The old readiness
booleans cannot identify the cause because host teardown resets them. The new
closed-list cold-failure fields preserve a bounded cause classification without
exporting the message.

The result confirms that trust was not consumed and no certificate mutation
occurred. The preflight Job Object closed, but GitHub later terminated native
build descendants named `vctip` and `mspdbsrv`. The earlier cleanup flag therefore
did not prove native-build process cleanup. Both builds now use the owned-job
protocol above. Their fresh Windows containment and preflight evidence are still
required. The failed request is retired; no activation was created or published.

Preparation run `37404814127` built both native targets in their owned jobs and
confirmed zero compiler descendants after cleanup. Browser preflight then
failed with `AdapterUnavailable` before trust. The updated launch is the next
source candidate for that failure. It needs a fresh reviewed helper hash,
dedicated request, successful Windows static and preflight evidence, and the
existing independent review before any exact-run activation. A passing source
check or launch-option check is not runtime acceptance. No certificate mutation
or trust action was reached in run `37404814127`.
