# Vulkan Phase 5.4 retirement and swapchain lifecycle

Status: Phase 5.4 implementation and runtime acceptance complete, 2026-08-31.

Live-resize follow-up (2026-08-31): the original discrete-resize cohort did not
cover held Win32 border drags. The reported stale-frame stretching regression
is now repaired and validated with actual held width/height drags, fresh Default
pipeline scene/compute/UI work, no native validation errors, and no device-idle
calls. See the [live resize investigation](vulkan-live-window-resize-relayout.md)
for the separate runtime evidence and corrected acceptance scope.

The subsequent mouse-release flicker is also repaired. Recording uses the
attempt's latched interactive state, and the resize-release generation handoff
keeps the last complete held image visible until an authored scene/ImGui
successor is presented. Actual held-drag release captures contain no missing UI,
black scene, fresh full-surface clear, native VUID, or device-idle call.

## Scope and acceptance

Close the seven resource-retirement and swapchain-lifecycle rows in the Vulkan
core frame-loop master tracker. Preserve prior Phase 5.0–5.3 acceptance; do not
claim Phase 8 performance or XR promotion. No tests are added or changed during
feature validation.

Acceptance requires bounded, observable destruction across resource classes,
native destruction outside global retirement locks, exact completion on every
relevant queue, asynchronous bounded desktop and secondary ImGui generations,
completion-safe mapped storage and command-pool reuse, and no normal lifecycle
device-wide idle. Validate the real Vulkan runtime with standard and
synchronization validation and inspect resized viewport images.

## Investigation

- Existing recording workers already own lane/frame-slot/family arenas with
  separate transient and retained pools. Pool reset checks exact child and slot
  completion. Utility thread pools are separate from this recording topology.
- Desktop resize already coalesces requests and transfers the maximum prior
  image timeline value to all replacement image slots. Old generations are
  bounded, but cleanup repeatedly invokes uncapped resource drains.
- Resource drains select ready entries under the retirement lock and normally
  destroy outside it. Temporary ready lists allocate on every invocation;
  per-invocation limits do not impose a shared frame budget. Deduplication must
  remain reserved until destruction completes.
- The graphics completion domain includes primary and secondary native queues.
  Advancing its completion value to the maximum observed sequence can falsely
  complete an earlier submission on the other queue.
- An empty queue submission fence is not WSI presentation completion. Desktop
  and secondary swapchain lifetimes require actual presentation-release proof;
  graphics completion remains a separate prerequisite. See the
  [Khronos semaphore lifetime guide](https://docs.vulkan.org/guide/latest/swapchain_semaphore_reuse.html).

## Evidence

### Implementation decisions

- Resource-class budgets belong to the resource runtime and reset once per
  production frame. Slot drains share native-destruction admission; each stable
  queue receives a bounded rotating scan share. Ready batches use retained
  staging storage, leave the queue lock before native calls, and preserve
  deduplication reservations until native destruction settles.
- High-water draining relaxes ordinary work caps, never GPU completion proof.
  Explicit teardown has a separate budget scope admitted only after every queue
  domain completes or device loss is established.
- Queue-domain completion advances through the contiguous proven frontier.
  Observing a later submission on a different native queue cannot retire an
  earlier unobserved submission in the same domain.
- Each desktop or detached ImGui swapchain generation owns a fixed pool of
  maintenance1 presentation fences. A token is reserved before image acquisition
  and committed only for Vulkan results that enqueue presentation. Presentation
  fences are never published as generic graphics queue completion.
- Desktop retirement reserves capacity for both an old and a failed replacement
  generation (eight retained generations maximum), and releases at most one
  completed generation per ordinary frame. Existing extent coalescing and
  strongest-prior-timeline inheritance remain in place.
- Detached windows poll their own graphics, acquire, and presentation fences.
  Resize and close no longer block on queue-wide marker waits. Pending close
  pressure stops new platform-window creation before allocating native state.
- Missing maintenance1 proof is explicit: presented generations remain retained
  within admission bounds. Teardown refuses to free unproven WSI ownership.
  Failed native teardown keeps a strong renderer/API owner and stops new Vulkan
  renderer admission until cleanup succeeds or the process restarts.

### Review corrections

The independent GPU/lifetime review identified and drove fixes for acquire-fence
reuse, detached-surface release, non-enqueued presentation errors, dispatching an
already-quarantined presentation token, failed acquire-recovery submissions,
initialization-failure cleanup, and public API disposal after a failed native
teardown boundary. Runtime acceptance is recorded below.

### Build hygiene

The isolated editor build exposed ignored pre-move source copies under
`XREngine.Data/Core/Assets`, `XREngine.Runtime.Bootstrap/Assets`, and
`XREngine.Editor/Assets`. The five stale files were preserved under this run's
`scratch/ignored-duplicate-source/`; the tracked replacement files remain
unchanged. An identical duplicate `IsFinite(Vector3)` helper was removed from
the concurrently edited humanoid partial class to unblock compilation. Other
animation work is outside this investigation.

Current disposable evidence root:
`Build/_AgentValidation/20260831-105036-vulkan-phase54/`.

- Isolated Vulkan build: zero warnings/errors, including the completed rotating
  scan integration and command-child readiness pooling.
- Full isolated editor build: zero warnings/errors. Named session
  `phase54-lifecycle`, first runtime PID 56664, confirmed Khronos standard and
  synchronization validation plus swapchain maintenance1 enabled.
- First live attempt did not complete a scene frame. Resource-plan freezing
  repeatedly rejected a changed native-buffer revision; its newly allocated
  physical images were lost before the outer rollback could observe ownership.
  This reproduced eventual device-memory exhaustion. MCP profiler evidence was
  captured; viewport capture timed out because no scene target completed. The
  named session was stopped. Fixing this leak and bounded same-state freeze
  retry is required before resize acceptance.

### Native regression acceptance

The existing production RenderBench scenarios were rebuilt against the new
retirement implementation with Khronos standard and synchronization validation:

- `reports/phase53-materials-final`: four normal/reversed-depth children, 240
  frames each at 640x360, all passed.
- `reports/phase53-streaming-final`: the same four-child matrix, all passed.
- `reports/phase52-buffers-4096-final`: four normal/reversed-depth children at
  4096x4096, all passed, including the required in-flight lifetime overlap.
  The earlier 640x360 probe did not reliably keep work in flight; its failed
  overlap receipts are retained, rather than weakening the assertion.
- `reports/phase53-materials-final-shutdown-smoke`: the final forced-drain
  implementation completed all eleven receipts and healthy native teardown.

These successful runs report zero native validation errors and warnings. No new
test methods were added. A forced drain now drains every fixed staging batch,
counts both native completion and removal of non-native pending entries as
progress, and refuses device teardown while pending/quarantined ownership remains.

### Live startup and command-buffer findings

- Resource-plan admission now owns its unpublished allocator before allocation
  or freezing can throw. A superseded native-buffer revision retries the same
  candidate a bounded number of times and otherwise uses fresh-frame retry;
  strict accepted-plan validation is unchanged. Subsequent live launches no
  longer leak those failed candidates or exhaust device memory.
- Advanced visibility compute programs now opt into their required background
  shader admission. The live output subsequently reaches `Bound`.
- The current Advanced pipeline still has no final-output consumer and publishes
  no scene-view work in this desktop configuration. Its black viewport therefore
  cannot establish visual lifecycle acceptance. The captured scene package has
  393 resident draws but zero views/submissions, all Advanced stages have zero
  commands, and only the six visibility textures exist. Established-pipeline
  lifecycle validation explicitly launches this isolated process with
  `XRE_ADVANCED_RENDER_PIPELINE_MODE=Disabled`; Vulkan remains selected, and
  persistent project/user preferences are not changed. Completing Advanced's
  later scene/shading/output stages is outside section 5.4.
- Dynamic UI had allocated a new secondary every frame because its completed
  parent still retained an immutable execute reference. The delayed path now
  resets only the completed per-image overlay parent before mutating that
  secondary; cached scene primaries remain reusable. Inline UI first marks its
  scene owner dirty and releases that parent through the tracked reset path.
  The fourth live launch reports zero allocations on sampled warmed frames,
  versus one allocation per frame before this correction.
- The on-demand retirement diagnostic includes actual `DeviceWaitIdleCalls`,
  per-class caps, admissions/completions, backlog/age/deferred work, uncapped
  activation, quarantine count, and desktop presentation-fence progress.

### Missing stable retirement capability

Live measurement found four device-idle calls across two resizes. Both the
scene and UI pipeline reached the stable resource-retirement capability lookup;
Vulkan did not implement that interface, so each call used the generic
`WaitForGpu` fallback. The new Vulkan capability forwards to its existing
descriptor/reference invalidation and completion-tracked native retirement.
It does not clear unrelated image-access history or wait for unrelated output.

### Final desktop and detached-window acceptance

The ninth isolated launch (PID 53548, log session
`xrengine_2026-08-31_12-10-23_pid53548`) includes the complete retirement
capability and passes the following native Vulkan cohort with standard and
synchronization validation enabled:

- A diagnostic cube in `MathIntersections` with the unrelated test root disabled,
  using explicit `XRE_FORCE_DEBUG_OPAQUE_PIPELINE=1`. Two camera views visibly
  change the marker's projection. Native window captures were actually viewed.
- Desktop client extents 1920x1080, 1273x701, and 1711x915, followed by
  minimize/restore and five additional odd/large resize cycles. Desktop
  generation advances from 1 to 16 during these operations and reaches 20 at
  final read-back (frame 28392). Settled retirement backlogs and native
  quarantine counts are zero. The final presentation generation has
  242 submitted and 242 completed maintenance1 fences.
- `DeviceWaitIdleCalls` remains **zero** throughout the cohort. This directly
  verifies the missing-capability fix rather than relying only on call-site
  inspection. Shutdown waits are separate from this normal-frame measurement.
- A real detached Inspector opens at 500x420, resizes through 613x477,
  431x333, and 700x511, and closes independently. Its images show valid changing
  UI and the primary continues presenting. A disposable, render-thread-queued
  placement helper exercises the ordinary ImGui platform callbacks without
  mouse-coordinate ambiguity. The existing optional native-window-disposal
  policy is unchanged: a hidden GLFW window may remain after its GPU/surface
  ownership is released.
- The final steady sample spans frames 13815 through 15374: process command
  allocations remain **142 to 142**, and all 80 sampled frames report zero new
  command buffers. Resize-generation allocations are separately expected;
  after the burst they settle at 274 with no continuing allocation.
- The sampled complete-frame retirement-drain p99 is 0.1314 ms in the steady
  interval; odd/large resize samples are 0.0150/0.0198 ms and the settled resize
  burst is 0.0154 ms. These are diagnostic samples, not portable high-refresh
  promotion evidence. The cumulative streaming histogram is recorded below.
- Final live logs contain zero VUID/validation errors. All captured normal
  operation checkpoints settle successfully; transient resize deferral is an
  explicit bounded admission outcome, not a failed frame ownership settlement.
  The named session was stopped through its owning session manager; its final
  logs are copied to `logs/final-editor/` in the evidence root. No other editor
  process was stopped.

The latest headless `warmed-production-clear-allocation-gate` also passes 240
capture frames after 30 warmup/5 stability frames with zero capture/worker
allocations, all 17 stability gates passing, and 240/240 delayed GPU queries
drained. The `phase53-pipelines-final` cold/warm matrix passes all eight children
with zero native validation errors/warnings; warm children load 443,166 bytes of
native cache and do no steady-state pipeline compilation or waiting.

### Cumulative retirement timing acceptance

The meter sums all measured retirement drains in each production frame and
publishes that duration at the next frame boundary. Cold diagnostic reads do
not publish or reset samples. Preallocated, fixed one-microsecond histogram
buckets round upward; long counters, an overflow count, and an observed maximum
prevent long sessions or different Stopwatch frequencies from silently
underreporting the tail. High-water drains are included. Shutdown is outside
the ordinary-frame cohort.

The source Phase 9 retirement-stage target for this diagnostic cohort is
**p99 below 0.5 ms**, assessed from the reports rather than imposed as a portable
benchmark assertion. This does not promote a high-refresh default or promise
that every individual drain remains below that target.

The tenth isolated launch (PID 5188, log session
`xrengine_2026-08-31_12-25-29_pid5188`) validates the final telemetry build:

- Twelve alternating odd/large resize requests, followed by minimize/restore
  and another large extent, advance the desktop to generation 25.
- The resize-burst checkpoint contains 3,922 complete frame durations with
  p99 **0.104 ms**. The restored checkpoint contains 5,881 durations with
  p50/p95/p99 **0.015/0.026/0.084 ms**.
- Nine durations exceed the histogram's 2.047 ms finite range; the observed
  maximum is **8.9727 ms**. These are retained and included in the percentile
  calculation, not discarded to pass the target.
- Actual normal-frame device-idle calls, quarantine count, and settled
  swapchain backlog remain zero. Final Vulkan/rendering logs contain no native
  validation errors. Both the odd-size and restored camera-B images were viewed
  and show valid, changed projection.
- The named session is stopped. Evidence is in `reports/tenth-*.json`,
  `mcp-captures/tenth-*.png`, and `logs/final-timing-editor/` under the run root.

The final existing `phase53-streaming` matrix also passes all four children
against the same conservative histogram, with standard/synchronization
validation enabled and zero native errors, warnings, or quarantined resources:

| Child | Complete frame samples | Retirement p99 (ms) | Maximum (ms) |
| --- | ---: | ---: | ---: |
| Normal, repeat 0 | 211 | 0.306 | 11.5764 |
| Reversed, repeat 0 | 212 | 0.103 | 5.7290 |
| Normal, repeat 1 | 211 | 0.052 | 5.7347 |
| Reversed, repeat 1 | 211 | 0.072 | 6.4562 |

Each child includes one overflow duration; all p99 values remain below 0.5 ms.
Exact contents, bounded uploads, fresh-plan retry, cancellation ownership, and
shutdown retain their existing scenario validation. The new telemetry is
available as `textureStreamingScenario.retirement` in
`reports/phase53-streaming-retirement-p99-conservative-final/scenario-result.json`
and its child reports. Live MCP exposes the same data under
`vulkan.retired_resources.metering`, including `drainDurationSampleCount`,
`drainDurationP50Milliseconds`, `drainDurationP95Milliseconds`,
`drainDurationP99Milliseconds`, `drainDurationOverflowCount`, and
`maximumPublishedDrainDurationMilliseconds`.

The post-histogram `warmed-production-clear-allocation-gate-histogram` rerun
passes 240 capture frames with zero capture-thread or worker allocations and
all 17 stability gates passing. CPU p50/p95 are 0.1946/0.3186 ms and GPU p95 is
0.006304 ms for this diagnostic workload. The final RenderBench build reports
zero warnings and zero errors. The master has all 41 Phase 5 rows checked; the
source Phase 9 has all 22 lifecycle rows checked.

### Separate integration limitations

The imported Default scene additionally exposed an exact-readiness stall with
four queued texture preparations, zero active preparations/transfers, and no
progress for 30 seconds. The coordinator did not weaken exact-readiness or
replace the requested Vulkan backend. Raw `Task.Run` preparation outside the
owned execution topology is a hypothesis requiring worker/task evidence, not
a proven root cause. This is separate from the validated window/resource
lifecycle cohort. GPU auto-exposure compute programs also now opt into their
required asynchronous shader admission, matching Advanced's correction.

No full Advanced scene/shading/output completion, imported-scene readiness
repair, hardware OpenXR acceptance, native GLFW disposal-policy change, or
portable high-refresh promotion is claimed here. No new tests were added;
user acceptance of these code changes has not yet been reported.
