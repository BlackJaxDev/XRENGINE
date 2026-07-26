# Vulkan Camera-Motion Framerate Regression Investigation

**Date:** 2026-07-21  
**Status:** Fixed and optimized on the full desktop `DefaultRenderPipeline` as of 2026-07-25; external OpenXR targets and mutable GPU-driven indirect/count streams remain explicitly quarantined

**Backend / workload:** Vulkan, Debug Unit Testing World, desktop editor viewport

## Problem Statement

After the Vulkan core-hardening changes, individual meshes intermittently rendered in the left or upper-left portion of the viewport and the renderer eventually crashed. The corruption and crash have been addressed in the companion investigation:

- `docs/work/investigations/rendering/vulkan-mesh-jitter-command-buffer-retirement-2026-07-21.md`

The remaining regression is severe CPU-side frame time while the editor camera moves. A stable camera now reuses command buffers, but continuous motion changes visibility, occlusion-query work, and directional-cascade shadow data. Debug frame time rises from roughly 35-45 ms while static to roughly 180-270 ms during the measured camera move, making the editor appear nearly frozen.

The goal is to retain the correctness fixes while making camera motion scale with changed work rather than reprocessing or re-executing hundreds of otherwise reusable command-chain entries.

## Reproduction

1. Build and launch the Debug editor with the Unit Testing World, Vulkan, MCP, and command chains enabled.
2. Allow imported assets and graphics pipelines to finish warming.
3. Hold the camera still and sample `get_render_profiler_stats`.
4. Move the editor camera over approximately four seconds while continuing to sample.
5. Hold the camera still again and sample the settled state.

The reusable measurement script is:

- `Build/_AgentValidation/20260721-vulkan-jitter-crash/scratch/measure-camera-motion.ps1`

Ignored measurement reports and screenshots are under:

- `Build/_AgentValidation/20260721-vulkan-jitter-crash/reports/`
- `Build/_AgentValidation/20260721-vulkan-jitter-crash/mcp-captures/`

## Evidence And Current Understanding

### The original stationary regression was a primary-cache regression

Commit `44028524` replaced the bounded primary-command-buffer variant cache with one reusable command-chain primary per frame slot. The live scene produces several recurring schedule shapes. Overwriting the one primary meant a query/shadow frame and its following clean frame repeatedly evicted one another.

The current fix restores a bounded, exact-signature, per-target/per-slot variant cache. It keeps the cache finite and uses LRU eviction. This removed repeated primary recording in stable and settled samples.

### Camera motion produces a large, changing draw schedule

Frame-operation tracing showed:

- Stable main-view frames: approximately 66-69 mesh draws.
- Motion frames with a directional-cascade refresh: approximately 460-542 mesh draws.
- The grouped directional-cascade shadow pass contributes 393 mesh draws.
- The main-view visible set grows and changes during the move, reaching approximately 130-150 draws in the traced camera path.

Before packet experiments, the command-chain schedule commonly contained 450-530 entries. Most shadow entries were reusable, but the renderer still refreshed frame data for every draw and executed hundreds of one-draw secondary command buffers. The primary command buffer also changed whenever the main-view chain membership changed.

### Disabling command chains only partially helps

`inline-primary-camera-motion.json` measured approximately:

| Phase | Whole-frame average | Primary-recording average |
|---|---:|---:|
| Static | 36.0 ms | 8.7 ms |
| Moving | 143.4 ms | 82.4 ms |
| Settled | 78.6 ms | 9.9 ms |

This proves command-chain management is part of the regression, but not the only cost. Inline motion frames still record as many as approximately 500 draws, dominated by directional-cascade refresh work.

### Query and cascade cadence were doing avoidable work

Implemented changes now:

- Batch Vulkan hardware occlusion queries by camera-motion tier instead of issuing exact visible queries every frame.
- Cap exact visible-draw queries at four per pass while preserving recovery probes.
- Prime visible queries only when the next frame is a query-batch frame.
- Treat normal clip-space directional-cascade movement as reprojectable for the configured bounded stale interval; reserve forced-fresh behavior for larger jumps.

These reduce avoidable work but do not eliminate the cost of a frame that genuinely refreshes the cascade atlas.

### Packet aggregation has a reuse-granularity tradeoff

Three packet strategies were tested:

1. **Same prepared program only:** safe, but ineffective for this workload because imported material variants effectively have distinct prepared bindings. Chain count remained near the per-draw count.
2. **All compatible draws in a pass/view:** reduced approximately 500 chains to roughly 30-45, but regressed sustained motion. A one-mesh change in the main visible set invalidated and re-recorded an entire packet of up to 64 draws. This broad strategy has been rejected.
3. **Shadow views only:** intended to aggregate the stable 393-draw cascade membership while retaining per-draw reuse for the changing main view. The first benchmark did not activate because generic shadow render-graph passes such as `DepthPrePass` were incorrectly classified as `RenderViewKind.Main`.

The current source now treats `PendingMeshDraw.ShadowUniformState.IsShadowPass` as the authoritative view-kind signal before falling back to pass-name heuristics. Focused tests and the post-change default-policy live benchmark pass.

### Mixed programs inside a shadow secondary are Vulkan-valid in this path

Each scheduled draw independently binds its graphics pipeline, layout-aware descriptor sets and dynamic offsets, vertex/index buffers, and push constants. Aggregated packet hashes are ordered and include every draw's structural signature, prepared-program binding identity, descriptor schema, and descriptor publication dependency. Compatibility also requires the same pass, target, view, and frame-op planner state.

Shader/program relinking under an unchanged binding identity is now covered by
an explicit successful-link generation. Ordered packet dependencies hash every
draw's program binding identity and link generation, so an affected secondary
cannot survive a relink with stale pipeline-layout state.

### Reused secondaries must retain their baked uniform-slot mapping

A scheduled secondary bakes each draw's dynamic-uniform-buffer offset. The current frame recomputes occurrence slots from the visible draw order. If an earlier occurrence for the same renderer/family becomes invisible or reorders, refreshing the new slot cannot make the old baked offset valid.

The current source now stores an ordered uniform-slot signature after recording each chain. Before reuse it compares the freshly assigned slot mapping and forces re-recording when the mapping differs. This is a correctness guard for the original per-mesh wrong-transform/wrong-camera symptom; the focused regression test and multi-position live validation pass.

## Measurement Ledger

All numbers below are Debug diagnostic samples and are attribution evidence, not Release performance gates.

| Report | Static avg | Moving avg | Settled avg | Result |
|---|---:|---:|---:|---|
| `default-parallel-primary-variant-query-batching-camera-motion.json` | 34.6 ms | 189.8 ms | 77.8 ms | Correct primary reuse, excessive per-draw chains during motion |
| `inline-primary-camera-motion.json` | 36.0 ms | 143.4 ms | 78.6 ms | Better than per-draw chains during motion, but still records hundreds of draws |
| `cross-program-aggregate-validation-camera-motion.json` | 41.0 ms | 122.9 ms | 86.6 ms | Short validation sample; chain count fell to roughly 26-42 and no Vulkan VUID/device loss was logged |
| `cross-program-aggregate-camera-motion-warm.json` | 41.4 ms | 271.4 ms | 71.8 ms | Broader/longer motion coverage exposed whole-packet invalidation; strategy rejected |
| `shadow-only-aggregate-camera-motion-warm.json` | 35.1 ms | 223.6 ms | 87.8 ms | Shadow draws were still misclassified as Main, so aggregation did not activate |

Results vary with asset/pipeline warmup and which part of the four-second camera path the sampler captures. Chain counts, dirty reasons, and stage timings are therefore more useful than comparing one short average in isolation.

## Visual And Validation Evidence

The mixed-program validation run completed camera motion without a Vulkan VUID, `ErrorDeviceLost`, or fatal exception in:

- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-21_16-54-33_pid15140/`

Two inspected MCP captures rendered coherent geometry without the earlier left/top-left displaced-mesh corruption:

- `Build/_AgentValidation/20260721-vulkan-jitter-crash/mcp-captures/Screenshot_20260721_165519_680_218c50eb0f9145faa754b322ee0d372b.png`
- `Build/_AgentValidation/20260721-vulkan-jitter-crash/mcp-captures/Screenshot_20260721_165542_413_8d4dc57b7d21439f9f1a869dbe9d864f.png`

The second camera position intersects dark foreground geometry, but its scene geometry remains spatially coherent; it does not reproduce the previous screen-quadrant displacement.

## Current Source Changes Relevant To Performance

- Restored bounded exact primary-command-buffer variants instead of one overwrite-prone primary per frame slot.
- Retained multiple recurring command-chain schedule shapes per frame slot.
- Corrected descriptor allocation identity to be prepared-program scoped.
- Batched Vulkan hardware occlusion queries and capped exact visible-query work.
- Adjusted normal directional-cascade motion to use bounded stale reuse/reprojection.
- Added ordered aggregate descriptor dependency tracking for multi-draw packets.
- Limited cross-program multi-draw aggregation to shadow-view packets.
- Corrected shadow-view detection to use captured shadow state rather than only pass names.
- Restricted reusable packets to operations classified as `FrameDataOnly`; dynamic overlay/gizmo/profiler/UI-like mesh commands remain inline.
- Stored and validated the ordered dynamic-uniform slot mapping baked into each reusable secondary.

## Correctness Closure

1. Dynamic overlay/gizmo/profiler/UI-like mesh commands are covered by volatility-classification tests and cannot aggregate into reusable `FrameDataOnly` packets.
2. Ordered baked uniform slots have a direct regression test; reordering changes the signature and forces re-recording.
3. Successful program relinks increment a generation included in ordered command-chain pipeline dependencies.
4. The default-policy live run completed multi-position camera motion with coherent captures and clean Vulkan validation/logs.

Further work is optimization rather than a correctness prerequisite: reduce
descriptor-publication/resource-plan churn in post-processing chains, profile
frame-data refresh separately for main and shadow views, and complete
cross-vendor acceptance before moving mutable GPU-driven indirect/count streams
into reusable secondaries.

## 2026-07-24 Physics Testing Follow-Up

The reported Physics Testing run was:

- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-24_20-02-31_pid30780/`

That run selected a mailbox Vulkan swapchain and later resized from `1920x1080`
to `2560x1369`. It logged display-synchronized VSync focus transitions. Startup
also rendered the four-cascade directional group in `78.70 ms` against the
configured `2.00 ms` shadow budget. The run contained no Vulkan VUID or device
loss, but it did not have the per-frame profile stream enabled, so its exact
manual-motion interval cannot be divided precisely between frame recording and
shadow refresh from that log alone.

A controlled Debug/Physics Testing comparison used the isolated session:

- `Build/_AgentValidation/mcp-sessions/vk-camera-fps-20260724/`

The same four-second interpolated camera path produced:

| Configuration / phase | Whole-frame average | Achieved-rate average | Vulkan-frame average | Primary-recording average | Reuse outcome |
|---|---:|---:|---:|---:|---|
| Command chains off, stationary | 6.04 ms | 159.53 Hz | 3.00 ms | 0.00 ms | clean primary reuse |
| Command chains off, moving | 16.82 ms | 53.38 Hz | 12.91 ms | 10.51 ms | 18/18 primaries recorded |
| Command chains off, settled | 6.41 ms | 141.01 Hz | 3.09 ms | 0.00 ms | 4/4 primaries reused |
| Command chains on, moving | 9.22 ms | 98.63 Hz | 4.75 ms | 0.00 ms | 18/18 primaries reused; 25.28 chains/frame reused |

This isolates the current Physics Testing cliff to inline-primary invalidation:
without command chains, a changed camera pose deliberately dirties the inline
desktop primary and records it again. With command chains enabled, the thin
primary stays reusable and per-draw camera data is refreshed through reusable
secondary ranges. The `30 FPS` observation is therefore not a hard-coded camera
limit; it is the frame-pacing result when the additional recording and
camera-dependent work push the reported scene toward a roughly `33 ms` frame.

Directional cascades remain an amplifier rather than a ruled-out cost. Camera
motion changes cascade fit/content, the atlas policy can force fresh refreshes,
and critical directional refreshes bypass the soft time budget. The controlled
run recorded slow grouped refreshes of `28.18-92.56 ms` in some startup/camera
transition frames. A shadow-disabled reverse-path experiment changed the
visible draw schedule and was not a valid isolated comparison, so it is not
used to assign a percentage to shadow work.

Both controlled configurations completed with zero Vulkan validation errors,
device loss, dropped frame operations, or submission rejections. Captures from
different camera positions changed coherently and did not show stale-frame
sampling:

- `Build/_AgentValidation/mcp-sessions/vk-camera-fps-20260724/mcp-captures/static-session/Screenshot_20260724_202047_000_9fa4a121cc564533831bc52472025906.png`
- `Build/_AgentValidation/mcp-sessions/vk-camera-fps-20260724/mcp-captures/command-chains-on/Screenshot_20260724_202949_274_0e69d5ca34dc40f9bc1e03b914a615f3.png`

`rdc doctor` passed, including Vulkan-layer registration. A RenderDoc capture
was not needed for this pass because the Vulkan CPU-stage counters and the
command-chain A/B isolated the dominant cost before GPU replay inspection.

## Permanent Fix And Default-Policy Validation

The permanent desktop fix makes hybrid command recording the safe default
instead of relying on a launch-time opt-in:

- `Vulkan.CommandRecording.Mode` defaults to `Auto`.
- `Auto` uses the hybrid path for validated desktop targets, keeping a thin
  reusable primary while refreshing camera/model/material data independently
  from cached secondary command structure.
- `XRE_VULKAN_COMMAND_CHAINS=0/1` remains a diagnostic override.
- External OpenXR-owned targets require an explicit `=1` experiment and mutable
  GPU-driven indirect/count operations remain inline on the Vulkan primary.
- Ordered uniform-slot dependencies and per-program successful-link
  generations close the known stale-secondary correctness gaps.

The isolated Debug/Physics Testing session
`Build/_AgentValidation/mcp-sessions/vk-hybrid-default-20260724/` ran with
`XRE_VULKAN_COMMAND_CHAINS` unset. Only command-chain tracing was enabled. The
same four-second camera path measured:

| Phase | Whole-frame average | Achieved-rate average | Vulkan-frame average | Primary-recording average |
|---|---:|---:|---:|---:|
| Stationary before | 10.69 ms | 78.7 Hz | 7.13 ms | 0.00 ms |
| Moving | 11.45 ms | 78.1 Hz | 7.91 ms | 0.00 ms |
| Stationary after | 11.07 ms | 80.6 Hz | 7.51 ms | 0.00 ms |

Camera motion added approximately `0.76 ms` to the sampled whole-frame
average, rather than the inline path's previous `10.51 ms` primary-recording
spike. During motion the hybrid schedule continued to record/reuse secondaries
while the thin primary remained reusable. The profiler ended with zero Vulkan
validation errors, dropped frame operations, or dropped draws.

Inspected settled and mid-motion captures retained coherent scene geometry:

- `Build/_AgentValidation/mcp-sessions/vk-hybrid-default-20260724/mcp-captures/Screenshot_20260724_210455_846_aa2bcacc0cd5413e9531f05c84ba0a60.png`
- `Build/_AgentValidation/mcp-sessions/vk-hybrid-default-20260724/mcp-captures/Screenshot_20260724_210458_230_6e4a01b00a794dccae310e1a0f16c178.png`

The flushed session logs contain zero VUIDs, validation errors, error/fatal
entries, device loss, or unhandled exceptions.

## Exit Criteria

- [x] No recurrence of displaced meshes, stale-camera rendering, or device loss during sustained camera motion.
- [x] Shadow refreshes are isolated from changing main-view packets and can use bounded shadow-only aggregation.
- [x] A changing main visible set does not force inline primary re-recording.
- [x] Default desktop camera-motion frame time is no longer dominated by primary command-buffer recording.
- [x] Focused tests, editor build, Vulkan validation, logs, and multi-position screenshots are clean.

## 2026-07-25 Full DefaultRenderPipeline Optimization

The follow-up deliberately kept `ForceDebugOpaquePipeline=false`. All numbers
in this section are from `DefaultRenderPipeline`; `DebugOpaque` was not used as
the solution.

The remaining steady-state cost was distributed across resource-plan scans,
short-lived frame-operation and binding snapshots, editor/scene collection,
and repeated generated-program identity construction. The production-path
cleanup now:

- retains exact resource-registry, pass-name, viewport, output, descriptor, and
  image-view snapshots;
- pools frame operations, mesh draws, binding snapshots, uniform arrays,
  descriptor update scratch, profiler bridges, and other frame-bounded state;
- installs a changed resource-plan schedule immediately instead of forcing a
  one-frame inline-primary fallback;
- skips Forward+ texture/buffer/dispatch work when there are no local point or
  spot lights;
- removes LINQ, captured closures, boxing, string construction, and temporary
  collections from the measured scene, UI, event, transform, culling, and
  Vulkan submission hot paths;
- probes an allocation-free generated-program state before constructing names
  or hashing shader source, and caches the mesh-version label that previously
  changed reference identity on every draw.

The final profiler-off allocation trace,
`Build/_AgentValidation/mcp-sessions/vk-default-pipeline-final13-20260725/reports/default-pipeline-gc-camera-motion-final16-alloc.nettrace`,
contains no sampled `EnsureProgram`, `BuildGeneratedProgramAxes`,
`BuildGeneratedProgramIdentity`, `CaptureGeneratedProgramState`,
`VersionKindLabel`, `ProfilerScope`, or listener-name formatting allocation.
Remaining Vulkan samples are first-use or structural collection growth during
visibility/resource changes rather than a recurring generated-program miss.

Retained-history reads avoid perturbing the update thread with continuous MCP
JSON serialization. The warmed Debug Physics Testing results were:

| Phase | Latest frame | p50 | p95 | Output / equivalent rate | Primary outcome |
|---|---:|---:|---:|---:|---|
| Stationary retained sample | 4.79 ms | 4.33 ms | 4.87 ms | 209.7 Hz reported | clean reuse |
| Smooth four-second motion, final cache | 5.44 ms | 5.55 ms | 7.89 ms | 181.2 Hz reported | 0 recorded, 1 reused in final frame |
| Stationary confirmation after motion and one-shot CPU dump | 4.84 ms | 4.41 ms | 5.10 ms | about 227 Hz from median frame time | clean reuse |

The motion and stationary confirmations ended with zero Vulkan validation
errors and zero dropped frame operations. An intentionally extreme eight-leg
camera-jump stress test did produce five genuine structural records while the
visible/shadow set changed radically; that is bounded changed work, not the
old every-frame primary invalidation.

The final inspected Vulkan readback is:

- `Build/_AgentValidation/mcp-sessions/vk-default-pipeline-final13-20260725/mcp-captures/Screenshot_20260725_011606_243_5626bcad4e724e73a3e635ddd5f44e54.png`

One diagnostic pitfall was fixed during measurement:
`dump_cpu_frame_profile` previously enabled detailed frame logging when no
snapshot existed and left it enabled. That made later traces measure profiler
tree construction and boxed scopes. The dump now restores the disabled state,
and external profiler scopes use a thread-local pooled bridge.
