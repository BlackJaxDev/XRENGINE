# Vulkan Camera-Motion Framerate Regression Investigation

**Date:** 2026-07-21  
**Status:** Active; visual corruption is fixed, camera-motion performance is not yet resolved  
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

The current source now treats `PendingMeshDraw.ShadowUniformState.IsShadowPass` as the authoritative view-kind signal before falling back to pass-name heuristics. The editor build succeeds; a post-change live benchmark is pending.

### Mixed programs inside a shadow secondary are Vulkan-valid in this path

Each scheduled draw independently binds its graphics pipeline, layout-aware descriptor sets and dynamic offsets, vertex/index buffers, and push constants. Aggregated packet hashes are ordered and include every draw's structural signature, prepared-program binding identity, descriptor schema, and descriptor publication dependency. Compatibility also requires the same pass, target, view, and frame-op planner state.

The remaining lifecycle caveat is shader/program relinking under an unchanged binding identity: command-chain dependencies need an explicit program/pipeline revision or a guaranteed invalidation on layout destruction/relink.

### Reused secondaries must retain their baked uniform-slot mapping

A scheduled secondary bakes each draw's dynamic-uniform-buffer offset. The current frame recomputes occurrence slots from the visible draw order. If an earlier occurrence for the same renderer/family becomes invisible or reorders, refreshing the new slot cannot make the old baked offset valid.

The current source now stores an ordered uniform-slot signature after recording each chain. Before reuse it compares the freshly assigned slot mapping and forces re-recording when the mapping differs. This is a correctness guard for the original per-mesh wrong-transform/wrong-camera symptom; build and live validation are pending.

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

## Open Correctness Items

1. Add regression coverage proving dynamic overlay/gizmo/profiler/UI-like mesh commands cannot aggregate into reusable `FrameDataOnly` packets.
2. Add regression coverage proving a changed same-renderer/family occurrence order invalidates a secondary with a different baked uniform-slot signature.
3. Add an explicit graphics-program/pipeline-layout revision to command-chain dependencies, or prove that every relink/layout destruction path invalidates affected chains.
4. Re-run Standard Validation after the shadow-view classification and uniform-slot changes, then inspect both motion screenshots and `log_vulkan.log`.

## Likely Next Steps

1. Build and test the packet-volatility and uniform-slot validity guards.
2. Benchmark the corrected shadow-view classification. Confirm that the 393 cascade draws collapse into bounded multi-draw shadow chains while main-view draws remain fine-grained.
3. Compare chain counts, chains recorded/reused/refreshed, packet construction, primary recording, submission, and allocations against the baseline report.
4. If per-draw frame-data refresh remains dominant, profile `TryRefreshReusableCommandBufferFrameData` separately for main and shadow views. Determine whether immutable material data can be skipped for shadow refreshes while still updating model/camera/cascade data.
5. Add the program/pipeline revision invalidation test and remaining packet/slot regression tests.
6. Run the focused Vulkan unit-test filters, a clean editor build, and `git diff --check`.
7. Run a warmed Release measurement before setting a final performance gate; Debug absolute frame times are not a shipping target.
8. Capture at least two final camera positions and inspect the newest Vulkan/rendering logs for VUIDs, descriptor/resource mismatches, device loss, and crash markers.

## Exit Criteria

- No recurrence of displaced meshes, stale-camera rendering, or device loss during sustained camera motion.
- Shadow refresh frames use bounded multi-draw shadow chains rather than approximately 393 one-draw secondaries.
- A changing main visible set does not invalidate unrelated shadow work or large packets of otherwise reusable main draws.
- Warmed Release camera-motion frame time is no longer dominated by command-buffer recording/refresh.
- Focused tests, build, Vulkan validation, logs, and multi-position screenshots are clean.
