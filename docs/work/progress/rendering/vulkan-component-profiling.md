# Vulkan Component Profiling Implementation And Evidence

Updated: 2026-10-01
Status: Profiling infrastructure implemented; production acceptance remains open.

The implementation checklist (now retired; open checks are in the
[Vulkan core validation doc](../../testing/rendering/vulkan-core-validation.md#component-profiling-and-renderbench))
now has bounded CPU spans, selected GPU timestamps, clock correlation, intrusive
counter replay, versioned artifacts, repeated comparison, promotion guards, and
developer/MCP workflows. These facilities explain renderer cost; they do not
establish a production optimization by themselves.

## Implemented Contract

- CPU spans retain stage, invocation, parent, frame, thread/worker, timestamps,
  allocations, and wait reasons in preallocated buffers. Lost records and
  unwarmed threads invalidate attribution. Post-capture analysis unions child
  intervals for exclusive time and subtracts waits per worker before calculating
  work overlap and imbalance. EventSource markers are opt-in.
- Selected GPU scopes use exact fixture pass names, bounded per-slot pools,
  synchronization2 timestamps where enabled, and delayed availability reads.
  Every selected target must occur on every retained frame. Unknown/duplicate
  names, overflow, abandonment, and unsupported required calibration fail.
  Production selected-pass recording requires explicit `GraphicsOnly` mode;
  split queues need queue-aware scope boundaries before they can be supported.
- Calibration records device/QPC pairs and deviation bounds before and after
  the run. Combined Chrome traces include calibration drift in their uncertainty;
  unsupported optional calibration preserves separate queue-local intervals.
- Performance queries enumerate counter metadata and selected identities,
  required replay passes, units, storage, scope, and concurrency-impact flags.
  A profiling lock covers immutable control recording and identical submissions.
  Only `noop-control` is supported for replay. Required unavailable counters fail
  preparation; optional counters report unsupported.
- Result schema 2 retains recipes, source/binary/backend identities, environment,
  target properties, intervals, raw streams, percentiles, mean, sample standard
  deviation, MAD, work counters, allocation totals, gates, and optional traces and
  PNGs. Gate and frame-stream artifacts survive rejected captures.
- Comparisons require at least four independent processes per variant in ABBA
  order. Requested and resolved recipes, outputs, hardware, driver, extensions,
  fixture, observer policy, variance, budgets, and CPU/GPU tails are checked.
  Observer comparisons are diagnostic. Baseline replacement requires explicit
  acceptance. Broader evidence must identify the same source and worker/mutation
  experiment; presentationless proxies cannot establish production frame savings.
- Quick, Compare, and Gate commands use existing builds. The named MCP manager
  serializes Start/Run/Stop per session and checks PID/start time, command line,
  endpoint identity, and port ownership. JSON date parsing preserves UTC and
  fractional start-time precision.
- The profile-session manager keeps one dedicated owner thread for executor
  preparation, stabilization, capture-thread warmup, measured frames, delayed
  query drainage, and cancellation cleanup. This preserves thread affinity
  across the complete profile lifecycle.
- Secondary-recording fixtures cache and warm the completion `WaitHandle`
  before capture and unconditionally block with native `WaitOne` for worker
  completion. Count polling was removed because `CountdownEvent.IsSet` can race
  with worker `Signal` and a subsequent `Reset`. Worker allocation totals are
  snapshotted at `EndCapture`, before delayed query drainage.
- A distinct `production-default-static` / `ProductionFullFrame` recipe now
  routes a fixed moderate-static scene through the production host, viewport,
  and `DefaultRenderPipeline`. Its fixture manifest sets the
  `productionFullFrame` evidence scope and exposes submission and primary
  command-buffer counts. The
  worker allocation counter is unmeasured, and a requested worker allocation
  budget fails. Clean comparison also rejects missing or unmeasured worker
  allocations. Its 64 MiB capture-thread budget is a diagnostic guard based on
  the currently measured production allocation level; it is not a zero-
  allocation claim or clean promotion evidence. Exact measured-receipt readback
  produces one shared hash/PNG/oracle input after capture. The visual gate
  requires the red fixture anchor; a black submission cannot pass correctness.
- External capture support attaches a bounded set of pre-existing vendor
  capture artifacts from the current task run after measured work. It records
  hashes and missing-file status; it does not launch vendor capture/replay or
  prove that a driver/tool can capture this rendering mode.

## Local Validation Evidence

These results describe component infrastructure and diagnostic observers.
One production diagnostic image now passes correctness. Clean production
comparison, controlled hardware budgets, and desktop/XR promotion remain
separate gates.

Disposable evidence is under
`Build/_AgentValidation/20261001-111944-component-profiling/`.

| Check | Observed result |
| --- | --- |
| Release RenderBench build | Zero warnings and errors. |
| Focused CPU/session/statistics/comparison/external configuration and Vulkan host tests | Final approved fixture alignment passed 38/38 with zero failures or skips in `logs/tests-fixtures-approved.log`. Three host tests now install the existing scheduler scope; comparison fixtures declare measured zero allocations. Earlier missing-scheduler and missing-result-field failures are retained in `logs/tests-final.log` and `logs/tests-comparison-final.log`. |
| Clean Quick secondary capture | 180 retained frames; zero capture-thread and worker allocations; all gates passed; result schema validated. |
| Targeted CPU capture | 60 frames, complete attribution, zero capture-thread and worker allocations. |
| Selected GPU passes with validation | Pass3 and Pass7 on 60 retained frames; all 20 gates passed; zero overflow/abandonment and validation errors. |
| Synchronization validation | Enabled with standard validation; no errors. One loader/registry warning was unrelated to image synchronization. |
| Required calibration | Device/QPC pairs obtained; pre/post deviation 3,136/1,920 ns. Exported output PNG viewed and matched the fullscreen fixture. |
| CPU observer comparison | Four processes per side, ABBA; CPU p95 1.00275 to 1.249375 ms (+24.59%). Work counts/output matched. Diagnostic variance limit was widened to 100%; this is not an accepted overhead budget. |
| Small selected GPU observer | Four processes per side, ABBA, 64x64 single pass; individual baseline GPU p95 0.004576–0.004768 ms and targeted 0.006784–0.006848 ms. The comparator rejected variance above its widened 100% diagnostic limit; no aggregate comparison was accepted. |
| Large selected GPU observer | Four processes per side, ABBA, 3840x2160 single pass; reported GPU p95 0.124456 to 0.127192 ms (+2.20%). Output/work matched with zero managed allocations and complete queries. Diagnostic variance limit 100%; not an accepted observer budget. |
| Named MCP lifecycle | Eleven checks passed after the native completion-event correction: capture, arm, cancellation, timeout, capability rejection, process ownership, and one/two-worker matrix. |
| External capture attachment | A required 68-byte dummy artifact was copied, hashed, and listed as attached in `reports/external-artifact-hook/`; this validates the artifact hook only, not a vendor capture. |
| Earlier production rejection | Thirty coarse GPU samples completed at matching engine IDs; selected `OpaqueDeferred` queries and required calibration completed. After the 36-word material-row fix, that rejected snapshot had seven visible draws, a completed material-bucket readback of `[0, 7, 0]`, 35 of 35 material rows ready, zero invalid material IDs, and zero native indirect API calls. The exact output image remained black and the red-anchor gate rejected it (`reports/production-bucket-runtime/`). |
| Production diagnostic image | After resolving cold mesh/index wrappers at the renderer facade, `reports/production-material-probe/` and the trace-free repeat in `reports/production-final/` each passed all 21 gates across 30 captured frames. Selected `OpaqueDeferred` and coarse GPU samples completed; standard and synchronization validation reported zero errors and one loader warning. Visibility read seven draws, material buckets were `[0, 7, 0]`, 35/35 rows were ready with zero invalid IDs, and three planned, native, and count-path indirect calls were recorded. Both exact-receipt outputs contained 360 red-anchor pixels and SHA-256 `0A260808D8EA09AA928D870675927215F10C2229243462397B02DC9777DF689E`. Both PNGs were viewed and showed the expected gray wall and red anchor. Final capture-thread allocation was 21,736,920 bytes; worker allocation remains unmeasured. These diagnostic passes are not clean promotion evidence. |
| Required hardware counters | Adapter did not enable `VK_KHR_performance_query`; preparation failed explicitly. Supported performance-query hardware remains unvalidated. |
| Unknown selected GPU target | Preparation rejected a valid-pass-plus-typo selection before capture. |
| Production lifecycle and clock | Final `reports/production-lifecycle-final/` passed all 21 gates on 30 frames, retained the same output hash, and reported zero validation errors. Explicit simulation time was 2.7333388 seconds, including cold attempts and drainage; capture-thread allocation was 21,332,560 bytes. A one-second timeout recipe exited 1 with an explicit `TimeoutException` and produced no accepted result (`logs/production-timeout-check.log`). |
| Legacy fixed-frame CLI | Eight-frame deterministic-clear control completed in `logs/legacy-control-final.log`. Fixed-frame shortcuts do not install the unbounded recipe sentinel as a native cancellation timer; versioned recipes retain their explicit timeout. |

The adapter was an NVIDIA GeForce RTX 4070 Laptop GPU. Thermal/clock state and
competing workloads were not controlled. No clean baseline was accepted, and no
production optimization was promoted.

## Remaining Acceptance And Scope

The Deferred/Uber RenderBench fixtures are fullscreen pass proxies. They are
useful for synchronization, query, and observer experiments; a complete
`DefaultRenderPipeline` cohort uses the production presentation-independent
host and its [cross-target validation plan](../../testing/rendering/vulkan-core-validation.md#presentation-independent-renderer).
The new production recipe exposes command-receipt submission and primary
command-buffer counters, selected GPU pass timestamps, and calibrated
timestamps in `GraphicsOnly` mode. Selected scopes require a dedicated uncached
primary: cached primary owners and reuse policy are rejected before recording,
so session-owned query pools cannot be replayed by later clean frames. Secondary
keys and clean primary identities remain unchanged. Hardware-counter replay is
supported only by the immutable control fixture. Production worker allocations
remain unmeasured. The
[production output investigation](../../investigations/rendering/2026-10-01-component-profile-production.md)
records the rejected images, their root cause, and the passing diagnostic image.
Desktop WSI and OpenXR/RVC evidence remain separate requirements.

The open checklist retains editor/window/MCP observer baselines, dense-query
reuse effects, accepted small/large GPU observer budgets, controlled hardware
variance, supported-device counter replay, desktop cached instrumentation, and an
optimization with demonstrated broader-frame savings. External capture
artifact attachment is implemented, while actual vendor capture/replay remains
hardware validation. The local RenderDoc injection attempt produced no `.rdc`;
the requested explicit frame capture could not start. A tool process exiting
successfully was not accepted as capture evidence. Clean production comparison,
desktop WSI, and OpenXR/RVC evidence remain open. CI regression enforcement remains disabled until a
controlled runner establishes variance. Required desktop/XR budgets remain
with the existing acceptance plan.

Reproduce developer captures with the
[profiler commands](../../../developer-guides/diagnostics/profiler.md#repeated-command-line-profiles).
Run the MCP lifecycle and process-ownership smoke command with an existing build:

```powershell
pwsh Tools/Tests/Test-RenderProfileMcp.ps1 `
  -RunRoot Build/_AgentValidation/<existing-run> `
  -RenderBenchDirectory Build/RenderBench/Release/AnyCPU/Release/net10.0-windows7.0
```
