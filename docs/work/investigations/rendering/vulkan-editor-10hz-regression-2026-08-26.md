# Vulkan Editor 10 Hz Regression

## Status

The cold-load terminal renderer pause and the post-import accepted-frame
generation failure were fixed and live-revalidated on 2026-08-27. The remaining
warm 10-14 Hz CPU/recording bottleneck is diagnosed but intentionally remains
active work in later master-plan phases.

## Problem

The Vulkan editor currently presents at roughly 7-11 Hz on a machine that has
previously shown roughly 200-300 Hz in lighter editor workloads. Determine
whether the current cadence is an intentional limit, a GPU/present problem, or
CPU-side frame construction before beginning the Vulkan core frame-loop and
resident-rendering plan.

## Conclusion

The editor is not capped at 10 Hz. It is CPU/render-thread bound while building
and recording an exact foreground frame. A separate Phase 0 correctness defect
made the worst cold Sponza/Mitsuki run stop rendering entirely: background
texture streaming superseded an exact foreground texture generation while
`PresentNow` readiness was waiting for it, and ordinary supersession was
incorrectly classified as a permanent renderer failure.

The current frame path combines three expensive conditions:

1. `PresentNow + BlockForExact` synchronously completes shadow readiness,
   texture-generation readiness, visible-mesh materialization, framebuffer and
   pipeline preparation, and compute frame data for every desktop swapchain
   image before acquiring the output image.
2. The primary Vulkan command buffer is still rebuilt around per-draw mutable
   descriptor, auto-uniform, buffer, and lifetime state. A sampled trace shows
   the render thread spending more than half of its interval waiting on locks
   below mesh-draw recording.
3. The current Unit Testing World loads the animated Jax Unity prefab and
   Sponza together, streams a large texture set, and enables directional
   shadows. The import did not report completion during the approximately
   eight-minute diagnostic session, so background publication continued to
   contend with foreground descriptor and texture inspection.

The normal non-isolated editor amplifies this with a persisted user override
that forces `GpuIndirectInstrumented` and enables GPU-indirect debug and
validation logging. A clean isolated profile still reproduced 7-10 Hz under
the current scene, however, so that override is additional overhead rather
than the primary cause.

The terminal pause is now resolved. Retryable generation supersession returns
the frame to readiness instead of latching `RendererPaused`; exact foreground
work has writer-priority arbitration, while background upload and pipeline
compile slices yield and resume. This does not make cold texture decode/import
free, and it does not solve the separate warm primary-recording cost.

## Cold Sponza And Mitsuki Failure Revalidation

The failing `2026-08-26_23-54-38_pid42060` run spent about eight seconds loading
Sponza and about 56 seconds importing the Jax/Mitsuki dependency graph. That
graph produced 52 model, 41 material, and 37 Poiyomi conversion operations.
Cold 4K texture decode/resize work commonly took 0.4-1.6 seconds per source;
queue delays reached roughly 36.55 seconds and one GPU-scene preparation group
ran for roughly 15.67 seconds. Those are real cold-content stalls and are
addressed by the master plan's resident publication, streaming, scheduling,
and tail-work phases rather than by presentation pacing.

The apparent crash was a renderer-terminal transition, not an observed Vulkan
device loss or OS process crash. At frame 540, exact readiness waited 246.4 ms
for texture generation 2. Background streaming published generation 3 and
canceled generation 2 before its upload registration; the old readiness path
treated that normal replacement as permanent. It emitted one desktop frame
failure, latched `RendererPaused`, and then produced 211 backpressure warnings.

The fix adds typed retry-vs-terminal supersession disposition, exactly-once
terminal/reproduction records, distinct foreground staging reserve slices,
and foreground/background arbitration with yield/resume counters. The isolated
`vulkan-sponza-mitsuki-phase0` revalidation crossed the former frame-540 failure
and reached render frame 4503 while remaining MCP-responsive. Its logs contain
zero `RendererPaused`, `DesktopFrameFailure`, readiness exceptions, frame
rejections, backpressure warnings, device loss, or unhandled exceptions.

A warm sample in that heavy validation still took about 73.89 ms total, while
resource preparation was zero, queue submit was 0.758 ms, native present was
0.034 ms, and the CPU profile accounted for only about 4.21 ms of active work.
That remaining attribution/recording gap is why Phase 1 now publishes the
unified frame tree, causal waits, actual presentation intervals, and explicit
unattributed time instead of treating the Phase 0 liveness fix as a throughput
fix.

## Phase 1 Presentation And Telemetry Smoke

The isolated `20260827-010729-vulkan-phase1-stable` session reused one build for
three launch profiles:

| Requested profile | Resolved native mode | Limiter | Observed result |
| --- | --- | --- | --- |
| Stable | FIFO | Off | 60 Hz target, bounded one-frame-ahead policy |
| LowLatency | Mailbox | On | 4.516 ms sleep + 0.250 ms spin, `frames_ahead=1` |
| Uncapped | Immediate | Off | 12.923 ms observed presentation interval in the sampled startup frame |

The Stable run advanced from published frame 340 to 794 and its successful
submission serial advanced from 576 to 1119. The latter frame correlated
graphics signal value 794, a valid 854,897,152-byte device-local usage sample
against a 20,402,772,377-byte budget, no device loss, and zero validation
errors. It attributed 82.38% of a 64.49 ms frame and explicitly reported an
11.36 ms unattributed gap. This validates the new schema while proving that the
99% detailed-attribution acceptance gate is not yet met.

All three clean shutdown log sets contain zero renderer pause, desktop frame
failure, readiness exception, frame rejection, backpressure, device-loss,
unhandled-exception, VUID, or validation-error records. The captured viewport
was visibly rendering current Vulkan content rather than a stale/uninitialized
surface.

## Phase 1 Correlated Frame-Tree Revalidation

The final isolated `20260827-013529-vulkan-phase1-observability` session ran the
source containing the shared correlated frame tree. Its rate-limited
`[Vulkan][FrameTree]` records carry the same engine, render, output, and frame-
authority IDs as the allocation-free runtime publication, profiler transport,
collapsible profiler UI, MCP response, and profile-capture schema. Inclusive
root time is partitioned into stage-exclusive time plus root-exclusive
`Unattributed`, and the tree separately reports work, wait, native-driver,
external-runtime, diagnostic, worker-overlap, and required-output critical-path
time.

The cold content load remains expensive. During Jax/Mitsuki prefab authoring,
Poiyomi material conversion, shader-cache loading, texture streaming, and
descriptor invalidation, render frame 335 reported an 11.263 s inclusive frame
with 11.093 s root-exclusive time; frame 336 reported 6.418 s inclusive with
6.317 s root-exclusive time. Those samples now identify the still-uninstrumented
cold authoring/publication interval instead of blaming queue submit or present.
The renderer recovered, continued advancing at the warm 10-14 Hz cadence, and
the stopped-session logs contained zero renderer pause, desktop frame failure,
readiness exception, frame rejection, backpressure, device loss, unhandled
exception, VUID, validation error, or YAML exception.

This closes the Phase 0 terminal-failure regression and the Phase 1 tree/UI/log
implementation. It does not close the Phase 1 99% attribution or observer-
overhead gates, and it does not remove cold import/authoring work. The latter is
owned by the master plan's resident publication, async preparation, streaming,
and tail-work phases; the warm primary-recording bottleneck remains the next
throughput problem.

## Phase 1 Wait And Cold-Readiness Attribution Correction

The follow-up isolated session
`20260827-020459-vulkan-phase1-wait-attribution` instrumented contended
frame-critical command-pool, descriptor, submission, queue-lease, lifetime,
upload, pipeline-compiler, and synchronization authorities. It crossed a
27.184-second cold Jax/Mitsuki frame, recovered, and advanced beyond frame 4500.
The stopped logs contained zero renderer pause, desktop frame failure, frame
rejection, backpressure, device loss, unhandled exception, YAML exception,
VUID, or validation-error records.

That run found a telemetry defect rather than a new renderer failure: roughly
0.05 ms per frame was double-counted because the pre-collect next-frame-slot
wait was aggregated with the current-slot wait, while query sampling, uniform-
ring reset, and staging trim were reassigned to lifecycle stages other than the
acquire/submit phases in which they executed. The corrected implementation now
times the actual current-slot native wait, keeps the next-slot wait separately
under `QueueSubmit`, and classifies those maintenance intervals in their true
phases.

In the corrected isolated session
`20260827-021317-vulkan-phase1-wait-attribution-fixed`, frame 117 reported
15.5300 ms inclusive, 15.5229 ms stage-exclusive, 0.0071 ms root-exclusive, and
99.9543% attribution. Cold frame 303 reported 153.5739 ms inclusive with
97.4871 ms in `ResourcePrepare`, 54.6869 ms in `CommandRecord`, 0.0095 ms root-
exclusive, and 99.9938% attribution. Frame 592 completed in 1282.2351 ms with
1220.5700 ms in `ResourcePrepare`, 60.0963 ms in `CommandRecord`, only 0.0083 ms
root-exclusive, and 99.9994% attribution.

This resolves the old 11.093-second and 6.317-second `Unattributed` diagnosis:
the cold stall is overwhelmingly synchronous exact-readiness/materialization
work, with command recording a smaller second component. Acquire, queue submit,
native present, and lock waits are not the cause in these samples. Moving this
work off the foreground frame remains owned by the master plan's resident
publication, asynchronous preparation, streaming, and tail-work phases; Phase 1
now measures it truthfully rather than optimizing it away.

That telemetry-correction session also exposed one independent correctness edge
at the startup import boundary. Frame 584 published a package immediately before
the bootstrap restored render settings. An unconditional mesh-submission command-
chain rebuild advanced the command generation, so the accepted desktop attempt
deferred and was rejected by `PresentNowFreshOutputRequired`.

The settings application is now idempotent: each pipeline records the requested
submission strategy actually captured by its generated commands; effective
backend capability resolution remains separate; and applying an unchanged
strategy no longer advances command generation. The follow-up
`20260827-022330-vulkan-phase0-generation-stability` run crossed the same import
reapply boundary with zero accepted-frame deferrals or rejections.

The final `20260827-023231-vulkan-phase0-generation-seeded` run advanced beyond
frame 1100 after import completion. All 33 rate-limited frame-tree records had a
`Completed` outcome and zero stage-over-root overlap. Its longest sampled cold
frame was 21.679 seconds; frame 1116 was 71.8519 ms with 0.0089 ms root-exclusive
time and 99.9876% attribution. The stopped logs contain zero renderer pause,
desktop frame failure, accepted-frame rejection, recording deferral,
backpressure, device loss, YAML exception, VUID, or validation error. One
collect-side `CommandGenerationMismatch` remains when the bootstrap restores
shadow-budget settings. It is discarded before Vulkan acceptance and replaced
by a fresh package, so it demonstrates the stale-snapshot guard rather than a
failed accepted frame. A post-import MCP screenshot confirmed live Vulkan output
rather than a blank or stale surface.

## Cadence Evidence

The latest normal editor run was uncapped and VSync-off. Vulkan frame IDs
advanced from 596 at `20:30:43.561` to 752 at `20:30:58.031`, or 10.78 frames/s.
The same log resolved the intrusive development path:

- `MeshStrategy=GpuIndirectInstrumented`;
- `PerformanceProfile=DevelopmentProfile`;
- `Intrusive=True`;
- `BlockForExact` on every foreground `PresentNow` transaction.

An isolated Debug session with fresh user data resolved to `CpuDirect`, disabled
CPU occlusion, and still advanced from frame 2840 at `20:51:24.525` to frame
2992 at `20:51:44.788`, or 7.50 frames/s. This rules out the saved
GPU-indirect override as the sole explanation.

There is no source or runtime evidence of a 10 Hz timer:

- target render frequency was `unrestricted`;
- effective unit-test VSync was `Off`;
- native present remained far below one millisecond;
- the Vulkan timer contains no 10 Hz clamp.

## Warm Frame Attribution

The final profiler snapshot was taken after the scene had been running for
more than seven minutes, with warm pipelines and no Vulkan validation errors.

| Measurement | Result |
| --- | ---: |
| Whole-frame p50 | 133.097 ms (7.51 Hz) |
| Whole-frame p90 / p95 | 142.894 / 189.271 ms |
| Sampled whole frame | 190.691 ms |
| Vulkan lifecycle | 187.888 ms |
| Primary command recording | 151.598 ms |
| Scene command-buffer recording | 151.745 ms |
| Currently unattributed lifecycle time | 34.359 ms |
| GPU command-buffer time | 8.906 ms |
| Queue submit | 1.304 ms |
| Native present | 0.048 ms |
| Pipeline compile time / pending pipelines | 0 ms / 0 |
| Vulkan validation errors | 0 |

The `resource_prepare` lifecycle stage reported zero even though
`DriveDesktopPresentNowReadiness` ran synchronously. The 34.359 ms gap is
therefore consistent with the current missing timing coverage around exact
readiness. This is an attribution defect as well as a performance problem.

The same frame recorded 54,875 reflected auto-uniform member scans, 922
fast-path auto-uniform draws, and 124 legacy-fallback auto-uniform draws. The
GPU finished the frame much sooner than the CPU, and compilation, queue submit,
and native presentation were all small. The bottleneck is repeated draw-state
publication/recording, not Vulkan WSI or shader execution.

## Sampled CPU Call Stacks

A 10-second `dotnet-trace` sampled-thread-time capture showed
`VulkanFrameLoop.Render` at 2.06% of aggregate process thread-time and
`Monitor.Enter_Slowpath` at 1.16%. Comparing those like-for-like sampled values
means approximately 56% of the render-thread interval was waiting to enter
monitors.

The largest render-thread lock sites were below:

- `VkMaterial.TryGetAutoUniformMaterialWritePlan`;
- `VulkanCommandRuntime.TrackCommandBufferResource`;
- `VkMaterial.TryGetMaterialDescriptorSet`;
- `VkMeshRenderer.EnsureBuffers`;
- texture descriptor-readiness and descriptor-image accessors;
- the imported-texture streaming generation registry.

These waits occur inside both exact pre-acquire materialization and primary
mesh-draw recording. Background texture publication and other workers share
the same mutable state, so the current hot path serializes rather than consuming
an immutable, already-published frame snapshot.

## Scene And Profile Amplifiers

The current ignored local `Assets/UnitTestingWorldSettings.jsonc` enables:

- the animated `jax2026.prefab` plus Sponza;
- directional-light shadows;
- skinning;
- ModelGrid light probes;
- ImGui and the full default render pipeline.

The normal editor also loads
`%LOCALAPPDATA%\XREngine\Sandbox\Config\user_settings.asset`, whose current
overrides include:

- `GPURenderDispatchOverride=true`;
- `EnableGpuIndirectDebugLoggingOverride=true`;
- `EnableGpuIndirectValidationLoggingOverride=true`;
- TSR and 4x MSAA overrides.

This overrides the Unit Testing World's explicit `GPURenderDispatch=false` and
explains why the normal run reports the intrusive instrumented path while the
isolated clean run reports `CpuDirect`.

Texture logs repeatedly showed CPU decode/resize/mip-build operations taking
hundreds of milliseconds, dense Vulkan residency publication, and descriptor
invalidations. The session never logged `UnitTestingWorld: All model imports
completed` before it was stopped.

## Directional-Shadow Multiplier

The prior controlled investigation in
`archive/current-jsonc-framerate-2026-07-26.md` reproduced the same approximate
10 Hz regime and isolated directional cascades:

| Moving-camera configuration | FPS | Scene record | GPU |
| --- | ---: | ---: | ---: |
| Directional light on | 10.47 | 61.72 ms | 4.29 ms |
| Directional light off | 19.17 | 27.93 ms | 2.77 ms |

Removing the light eliminated roughly one million shadow-pass triangles and
about 42 ms of CPU time while changing GPU time by only about 1.5 ms. Shadows
are therefore a major multiplier when camera, animated geometry, or streaming
publication dirties the frame, but they are not the only CPU cost.

## Why The Old 200-300 Hz Result Is Not The Current Workload

No retained log was found that ties the old 200-300 Hz observation to the exact
current commit, scene, build, and user profile. The available measurements show
that warm Release/Sponza cohorts can run around 100-140 Hz, while Debug,
instrumented, shadowed, moving or actively streaming cohorts are much slower.

The current launch is materially different from a lightweight warm editor
frame: it uses Debug binaries, a large live Unity-avatar import plus Sponza,
exact synchronous readiness, directional cascades, per-draw mutable binding
work, and—in the normal profile—instrumented GPU-indirect diagnostics. It also
refuses to present stale or partially ready content. The prior high number
therefore cannot be used as an apples-to-apples baseline without recovering its
commit and settings.

## Implications For The Master Plan

The current diagnosis directly supports the consolidated master plan's first
implementation priorities:

1. Time and attribute the complete pre-acquire readiness interval, including
   shadows, texture-generation convergence, mesh materialization, and all
   swapchain-image preparation.
2. Remove monitor acquisition and mutable-object discovery from primary
   recording by publishing immutable frame/resource/binding snapshots.
3. Make resident draw templates, prepared bindings, and command-chain reuse
   survive ordinary camera/animation changes; emit only compact dynamic data.
4. Separate static and dynamic shadow work so four cascades do not force a
   broad frame re-record.
5. Use clean Release profiles for performance gates and keep intrusive
   GPU-indirect logging opt-in.
6. Keep a lightweight editor baseline separate from cold import/streaming
   stress workloads.

## Evidence Locations

- Normal editor logs:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-08-26_20-29-14_pid30148/`
- Isolated session logs:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260826-204248-vulkan-10hz-baseline/logs/`
- Disposable trace:
  `Build/_AgentValidation/20260826-204233-vulkan-10hz-diagnosis/reports/vulkan-10hz-cpu.nettrace`
- Failing cold-load logs:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-08-26_23-54-38_pid42060/`
- Fixed heavy-load validation logs:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260827-001724-vulkan-sponza-mitsuki-phase0/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-08-27_00-18-27_pid32204/`
- Phase 1 profile smoke logs:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260827-010729-vulkan-phase1-stable/logs/`
- Phase 1 viewport evidence:
  `Build/_AgentValidation/20260827-010800-vulkan-phase1-stable/mcp-captures/Screenshot_20260827_010919_569_9a3c735d58c34239a0ad3457a0acd113.png`
- Final Phase 1 correlated-tree logs:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260827-013529-vulkan-phase1-observability/logs/`
- Corrected Phase 1 wait-attribution logs:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260827-021317-vulkan-phase1-wait-attribution-fixed/logs/`
- Final Phase 0 generation/Phase 1 attribution logs:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260827-023231-vulkan-phase0-generation-seeded/logs/`
- Final post-import Vulkan viewport evidence:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260827-023231-vulkan-phase0-generation-seeded/mcp-captures/Screenshot_20260827_023555_640_e944e73801154db78c9ec04e262bbed1.png`
