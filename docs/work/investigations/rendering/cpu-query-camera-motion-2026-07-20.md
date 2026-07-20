# CPU async-query occlusion during camera motion

Status: the upside-down Vulkan presentation regression and the camera-motion all-visible recovery loop are fixed and validated. Camera-cut, viewport-edge, hierarchy, and stereo stress cases remain as follow-up matrix work.

## Problems

1. The ImGui editor remained upright, but the 3D view was vertically inverted.
2. `CpuDirect` + `CpuQueryAsync` culled after the camera settled, then sustained camera motion made nearly every command draw and prevented recovery.

Yellow debug bounds continue to represent a current `Skip` or `ProbeOnly` decision. They must not be synthesized from an old negative result while a mesh is actually drawn.

## Confirmed root causes

### Vulkan window presentation

The final post-process texture was upright when captured directly, while the same frame was inverted in the OS window. The scene camera, projection, and occlusion data were therefore correct. A previous change removed the source-Y orientation handling from `VPRC_RenderToWindow`, so the direct Vulkan presentation blit sampled framebuffer-backed textures upside down.

The fix restores a Vulkan-only, source-specific `FlipSourceYOnVulkan` uniform and drives it through the pipelines' existing `ShouldFlipVulkanPresentSourceY(...)` policy. The flip is not applied to OpenGL.

### CPU-query temporal policy

- The configured small translation and rotation thresholds were not consumed; `Stable` required effectively exact pose equality.
- Motion thresholds were applied per rendered frame without render-delta normalization.
- A query stored no issuing camera pose or queried bounds, so delayed Boolean evidence could not be rejected using the pose and projection that produced it.
- Negative-result maximum ages collapsed to 6/3/1 frames during small/medium/large motion, despite Vulkan deliberately waiting at least two frames before polling and dense scenes requiring multiple recovery sweeps.
- Workload expansion applied only to `Stable` and used the visible-demotion budget instead of the recovery budget.
- Reveal priority covered the near plane but not viewport-edge entry, accumulated parallax, or projected growth.

### Vulkan frame-data exhaustion blocked recovery

Live validation exposed a second backend failure after the temporal policy changes. `VulkanFrameWideMeshFrameDataReservationManifest` applied a 32-slot minimum independently to every renderer family. Camera motion refreshes dense shadow work containing hundreds of mostly single-draw renderers, so sparse reservations filled the fixed 32 MiB mesh frame-data arena.

Once the arena was full, query draw preparation remained `DescriptorsPending`. No fresh occlusion-query draws were recorded, the recovery age grew without bound, and conservative visibility rendered everything. The per-renderer floor is now four slots; observed larger families still round up to their required power-of-two capacity.

Empty render passes also overwrote the global motion and forced-visible telemetry. They no longer publish active-view motion or conservative-frame state, which makes the MCP diagnostics reflect the pass that actually contains scene commands.

## Implemented design

- Capture an unjittered camera snapshot and world AABB on every submitted query, then transfer that exact state to the resolved result.
- Reproject queried and current AABBs into normalized device coordinates. A zero-sample result remains reusable only while screen-center shift, projected growth, and viewport-edge risk remain bounded.
- Classify motion with translation, rotation, projection, and invalid-snapshot causes; expose allocation-free per-view classification data through occlusion telemetry and MCP profiler output.
- Derive result lifetime from recovery-query budget, scene command count, retest cadence, and backend minimum latency for every motion tier.
- Apply the same pose-aware result contract to the OpenGL GPU-dispatch `CpuQueryAsync` path.
- Keep empty passes from replacing active scene-pass telemetry.
- Reduce the Vulkan per-renderer reservation floor from 32 slots to 4 and cover dense single-draw families with a deterministic regression test.
- Restore source-specific Vulkan Y orientation in both default window-presentation pipelines.

## Rejected experiment

An attempted Vulkan retry path scheduled an occlusion query after preflight returned `DescriptorsPending`. The retry could still be pending inside the query bracket, producing an empty hardware query. Vulkan correctly treated those empty epochs as visible, which recreated the all-visible behavior. That experiment was removed; query submission remains gated on successful draw preparation, and the underlying arena exhaustion was fixed instead.

## Validation evidence

Run root: `Build/_AgentValidation/20260720-110353-cpu-query-motion/`

### Presentation orientation

- Before-fix OS window: `mcp-captures/stable-window.png` — ImGui upright, 3D scene inverted.
- Same frame's final pipeline texture: `mcp-captures/RenderPipeline_FinalPostProcessOutputTexture_20260720_110900.png` — 3D scene upright.
- After-fix OS window: `mcp-captures/fixed-stable-window.png` — both 3D scene and ImGui upright.

This before/pipeline/after sequence isolates the defect to final presentation rather than camera or occlusion math.

### Failed motion run before the reservation fix

Log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-20_12-03-02_pid37664/`

- Mesh frame-data reserved bytes reached 33,554,416, sixteen bytes below the 32 MiB arena limit.
- Reservations reached 119,279 and dynamic-uniform exhaustion reached 61.
- Main-view recovery age exceeded 1,116 frames.
- Query submissions stopped and culling remained zero after camera motion.

### Fixed motion run

Log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-20_12-11-37_pid43760/`

- Early stationary state converged to three culled draws with 2,319,328 reserved bytes and 6,244 reservations.
- A six-second lateral translation retained non-zero culling during safe samples, briefly became conservative while evidence was pending, and returned to three culled draws after settling. Recovery age peaked at 10 frames.
- A six-second 5-degree yaw sweep converged to two culled draws for the changed view; returning to zero degrees converged to three. Return-sweep recovery age peaked at two frames.
- After both translation and rotation sequences, reserved bytes remained bounded at 5,230,848 and dynamic-uniform exhaustion remained zero.
- Query submission and resolution continued during both sequences.
- MCP reported zero Vulkan validation errors.
- The steady-state Vulkan/rendering logs contain no empty-query, `DescriptorsPending`, frame-data-reservation, context-mismatch, device-loss, or Vulkan validation-error diagnostics. Existing broad-barrier audit and unsupported Vulkan GPU-raycast warnings are unrelated.

Copies of the relevant pre-fix and fixed logs are retained under the run root's `logs/` directory.

### Deterministic validation

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VulkanUniformBufferGenerationCacheTests|FullyQualifiedName~CpuRenderOcclusionCoordinatorTests|FullyQualifiedName~CpuOcclusionTemporalPolicyTests|Name=VulkanPresentTextureShaders_ApplySourceOrientationToDirectAndFallbackPaths"`
  - Result: 66 passed, 0 failed, 0 skipped.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`
  - Result: succeeded with 0 warnings and 0 errors.

## Remaining matrix work

- Visually verify current yellow `Skip`/`ProbeOnly` bounds during motion.
- Exercise projected growth, viewport-edge reveal, a camera cut, and the return-to-stable path in one captured sequence.
- Audit independent physical-eye evidence for shared stereo/POV coverage.
- Rebuild current hierarchy union bounds before reusing hierarchy evidence when children move.
- Confirm forward scheduling progress for very large OpenGL GPU-dispatch command counts.

## RenderDoc note

MCP texture/window evidence and profiler/log counters identified both defects, so no GPU capture was needed. `rdc` is not currently on `PATH`; `C:\Program Files\RenderDoc\renderdoccmd.exe` is installed, but Vulkan layer registration should be verified before a future capture.

## Design caveat

A zero-sample hardware query is a Boolean observation from its issuing frame. Reprojecting the proxy AABB only decides when that observation is no longer trustworthy; it cannot reconstruct occluder depth. Stronger temporal guarantees require a depth-history/Hi-Z path with explicit disocclusion handling.
