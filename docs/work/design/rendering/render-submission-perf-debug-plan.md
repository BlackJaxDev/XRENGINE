# Render Submission Performance Debug & Optimization Plan

Status: Draft (2026-05-10)
Owner: Rendering
Related code:

- [XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs](../../../../XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs)
- [XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs)
- [XREngine.Runtime.Rendering/Rendering/Commands/RenderCommandCollection.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/RenderCommandCollection.cs)
- [XREngine.Runtime.Rendering/Rendering/Compute/SkinnedMeshBoundsCalculator.cs](../../../../XREngine.Runtime.Rendering/Rendering/Compute/SkinnedMeshBoundsCalculator.cs)
- [XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.cs](../../../../XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.cs)
- [XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_BuildAccelerationStructure.cs](../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_BuildAccelerationStructure.cs)
- [XRENGINE/Engine/Subclasses/Engine.CodeProfiler.cs](../../../../XRENGINE/Engine/Subclasses/Engine.CodeProfiler.cs)
- [XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs](../../../../XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs)

## 1. Problem Statement

After landing the GPU zero-readback prepass fix (no more striping artifacts) and Path A direct-write of skinned-mesh leaf AABBs into `_commandAabbBuffer`, two regressions/holdovers remain:

1. **CPU-direct submission path** (`RenderCommandCollection.RenderCPU` -> `RenderCommandMesh3D` / `GLMeshRenderer.Render`) is now **slower** than its pre-Path-A baseline.
2. **GPU-indirect zero-readback path** is **still slow**, and was the original motivation for the work.

Both problems are observed with **no lights active**, so cost is concentrated in:

- Per-frame command-buffer bookkeeping / sync.
- Per-mesh CPU work in submission loops.
- Driver state churn (uniform/program/VAO rebinds).
- New Path A frame work once active (sentinel writes + per-slot/per-mesh reduce dispatch).

## 2. Goals & Non-Goals

### Goals

- Establish a **repeatable, minimal-overhead measurement workflow** for both submission paths.
- Produce a **ranked, evidence-backed list of hot spots** for each path.
- Define **A/B isolation toggles** that prove or disprove each hypothesis cheaply.
- Define **specific code-level optimizations** with predicted wins, prerequisites, and validation steps.
- Audit the new Path A wiring for hot-path allocations and tiny GL uploads introduced this session.

### Non-Goals

- No new submission strategy in this doc.
- No GPU driver upgrade or extension dependency changes.
- No lighting-path optimization — measurements run lights-off until baseline is restored.

## 3. Background: Current Submission Topology

| Path | Entry | Per-mesh work | Per-frame buffer cost |
| --- | --- | --- | --- |
| CPU-direct | `RenderCommandCollection.RenderCPU` (swapped-list iteration) | `RenderCommandMesh3D` -> `GLMeshRenderer.Render`: program setup, material uniforms, model/previous model uniforms, VAO bind/config, `glDrawElements*` | `RenderCommandCollection.SwapBuffers`; GPUScene only when GPU dispatch or CPU/GPU mirroring is active |
| GPU-indirect | `HybridRenderingManager.DispatchRenderIndirect` | per-batch program/material rebind, `glMultiDrawElementsIndirect[Count]` | `GPUScene.SwapCommandBuffers` full copy of command + transparency metadata buffers |
| GPU-indirect ZeroReadback | as above + bucket/material-table scatter | CPU material-slot lookup + bucket loop/readback-assisted diagnostics | as above + scatter buffers |

Path A (new) is intended to add, **per registered skinned mesh per acceleration-structure build**:

- A `WriteCommandAabbSentinel(commandIndex)` per command slot owned by the renderer (CPU `glBufferSubData` of 32 B per slot).
- A `DispatchPathADirectWrite` reduce-style dispatch into `_commandAabbBuffer`.
- `SkinnedMeshBoundsCalculator.RefreshAllSkinnedAabbs` snapshots the registry HashSet into a freshly allocated `RenderableMesh[]` per call.

Important activation caveat: the prepass fast path currently returns `Result(Array.Empty<Vector3>(), localBounds, basis)` as a GPU-only success signal, while `RenderableMesh.ApplySkinnedBoundsResult` treats zero positions as failure. Before measuring Path A, verify this contract and the registration path are actually working.

## 4. Diagnostic Strategy

### 4.0 P0 sanity checks

Do these before collecting baseline numbers:

1. Confirm `SkinnedBoundsGpuDirectAabbWrite` is still default-off for normal editor runs.
2. Fix or explicitly account for the GPU-only bounds result contract:
   - `SkinnedMeshBoundsCalculator.TryComputeFromPrepassOutput` returns empty CPU positions on success.
   - `RenderableMesh.ApplySkinnedBoundsResult` currently treats empty positions as failure.
   - Path A registration in `ProcessSkinnedBoundsRefresh` only runs after `ApplySkinnedBoundsResult` succeeds.
3. Add or expose low-overhead counters for:
   - Path A registered mesh count, refreshed mesh count, command slot count, sentinel upload count, and reduce dispatch count.
   - `GPUScene.SwapCommandBuffers` copied bytes, skipped-copy frames, command-buffer version, and transparency-metadata version.
   - Indirect dispatch barrier count, material batch count, active zero-readback bucket count, and GPU readback bytes.
4. Create a repeatable measurement preset that logs the effective values of every debug flag below.
5. Warm shaders/programs and discard the first few seconds/frames before timing. Prefer Release/profile builds for performance capture; use Debug only for functional triage.

### 4.1 Enable instrumentation

1. `Engine.Profiler.EnableFrameLogging = true` (default in DEBUG; ensure not overridden).
2. Optional: `XRE_PROFILER_ENABLED=1` env var when launching from VS Code.
3. Optional: `Engine.Profiler.RenderStallThresholdMs = 16f` to make `profiler-render-stalls.log` fire on every dropped 60 Hz frame instead of >500 ms.
4. Verify the following are **off**:
   - `DebugSettings.DumpIndirectArguments`
   - `DebugSettings.ValidateBufferLayouts`
   - `DebugSettings.LogCountBufferWrites`
   - `DebugSettings.ProbeSourceCommandsBeforeCopy`
   - `DebugSettings.ForceCpuIndirectBuild`
   - `DebugSettings.DisableCountDrawPath`
   - `DebugSettings.SkipIndirectTailClear`
   - any `DumpIndirectCommandsOneShot` / `LogIndirectParityChecklist` path, or discard its warmup-only frames from captures

   `DumpIndirectArguments`, `ValidateBufferLayouts`, and `SkipIndirectTailClear` are already wired to `false` in [XREngine.Editor/Program.cs](../../../../XREngine.Editor/Program.cs) (~L503–L509). Treat that as the source of truth; if a future commit flips them on for triage, gate the change behind an env var, not a code default.

### 4.1.1 Baseline measurement preset

The plan's P1 baseline assumes the values below. These are the live defaults in [Engine.Rendering.Settings.cs](../../../../XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs) as of 2026-05-10, so the only thing that needs explicit attention is making sure `Assets/UnitTestingWorldSettings.jsonc` and any active world settings file do not override them away from these values.

| Property | Required for baseline | Live default | Notes |
| --- | --- | --- | --- |
| `EnableNvidiaDlss` | `false` | `false` | Vendor upscale off during measurement. |
| `EnableIntelXess` | `false` | (verify) | Same. |
| `EnableIntelXessFrameGeneration` | `false` | `false` | Same. |
| `GpuOcclusionCullingMode` | `Disabled` | `Disabled` | Removes CPU query coordinator from baseline; re-enable in a follow-up A/B. |
| `SkinnedBoundsRecomputePolicy` | `Never` | `Never` | Keeps skinned-bounds refresh out of B1; B2 reactivates it explicitly. |
| `CalculateSkinnedBoundsInComputeShader` | `false` | `false` | Path B off for the pre-Path-A baseline. Flip on for the Path A measurement run. |
| `SkinnedBoundsGpuDirectAabbWrite` | `false` | `false` | Path A off for the pre-Path-A baseline. Flip on for the Path A measurement run. |
| `UseGpuBvh` | `true` | `true` | Leave on; toggling is part of §4.4. |
| `ForceMeshSubmissionStrategy` | `null` (use env var) | `null` | Use `XRE_FORCE_MESH_SUBMISSION_STRATEGY` instead so the same settings file works for all three strategies. |
| `ZeroReadbackMaterialDrawPath` | `FullBucketScan` | `FullBucketScan` | Production zero-readback path. |
| `MsaaSampleCount` | `4` | `4` | Leave default; not part of this regression. |

Strategy switching for baseline runs is done via the environment variable `XRE_FORCE_MESH_SUBMISSION_STRATEGY` (see [docs/architecture/rendering/mesh-submission-strategies.md](../../../architecture/rendering/mesh-submission-strategies.md)). Accepted values: `CpuDirect`, `GpuIndirectInstrumented`, `GpuIndirectZeroReadback`. The env var takes precedence over `ForceMeshSubmissionStrategy` and beats the resolver, so each VS Code task can launch with a different strategy without mutating the settings JSONC.

The §7 P1 captures are produced by the `Measurement-Baseline-*` VS Code tasks (see `.vscode/tasks.json`):

- `Measurement-Baseline-CpuDirect`
- `Measurement-Baseline-GpuIndirectInstrumented`
- `Measurement-Baseline-GpuIndirectZeroReadback`

Each task sets `XRE_PROFILER_ENABLED=1`, the strategy env var, and a distinct `XRE_WINDOW_TITLE`, and depends on `Build-Editor`. After P1 captures are committed, re-run all three tasks with `SkinnedBoundsGpuDirectAabbWrite=true` (and `CalculateSkinnedBoundsInComputeShader=true` for B2) to quantify the Path A regression.

### 4.2 Capture targets

Logs land under `Build/Logs/<configuration>_<tfm>/<platform>/<session>/`:

- `profiler-fps-drops.log` — primary. Includes `HotPath`, `AppendRenderMatrixStatsSnapshot`, `AppendSkinnedBoundsStatsSnapshot`, `AppendOctreeStatsSnapshot`, `TopRootTimings`.
- `profiler-render-stalls.log` — render-thread stalls.
- `profiler-main-thread-invokes.log` — `MainThreadInvoke` flooding.
- `profiler-conditional-loop-spikes.log` — for-loop body spikes.

In-engine: ImGui Editor → Engine Profiler Panel. Standalone: `XREngine.Profiler` UDP receiver.

### 4.3 Scenes

Use the Unit Testing World's two-Sponza baseline so command counts, material counts, transparency content, and submesh distribution match a realistic workload, and so the forward vs. deferred cost is directly comparable in the same frame.

- **B1 — two-Sponza static** *(primary baseline)*: two copies of `Models\Sponza2\Sponza.obj` placed side-by-side via [Assets/UnitTestingWorldSettings.jsonc](../../../../Assets/UnitTestingWorldSettings.jsonc).
  - Sponza A: `MaterialMode: Uber`, `Translation: (0,0,0)` — exercises the uber-shader forward path.
  - Sponza B: `MaterialMode: Deferred`, `Translation: (0,0,-10)` — exercises the deferred path.
  - Lights off, camera framed so both copies are visible, no skinning, no animation.
  - To enable: set both Sponza2 entries' `Enabled: true` in the settings file (Sponza A is already enabled; flip Sponza B's `Enabled: false` → `true`). Regenerate via `Generate-UnitTestingWorldSettings` if schema fields shift.
  - Same scene exercises both submission paths for every render strategy, so a single capture produces forward and deferred numbers simultaneously without re-launching.

- **B2 — Sponza + skinned overlay** *(Path A baseline)*: B1 plus 100 unlit skinned avatars idling on top of the Sponza floor. Used to measure Path A overhead against a realistic forward+deferred backdrop instead of in isolation.

- **B2-paused — paused skinned overlay**: same content as B2, animation paused after warmup. Path A dirty-skip validation scene.

B1 alone does not exercise Path A (no skinned meshes). Use B1 for CPU-direct, command swap, batching, barriers, and zero-readback material overhead. Use B2/B2-paused for Path A.

Run each scene through three submission strategies via `XRE_FORCE_MESH_SUBMISSION_STRATEGY` (no code or settings change between runs):

- `CpuDirect`
- `GpuIndirectInstrumented`
- `GpuIndirectZeroReadback` (cycle `EZeroReadbackMaterialDrawPath` variants in a follow-up A/B once the FullBucketScan baseline is captured)

Per-strategy hot-path analysis splits forward (Sponza A) and deferred (Sponza B) draws by `EDefaultRenderPass` — both pass IDs appear in the same `profiler-fps-drops.log` HotPath tree.

### 4.4 A/B isolation toggles

Toggle one at a time, capture 60 s of profiler output, compare median frame.

| Toggle | Hypothesis tested |
| --- | --- |
| `SkinnedBoundsGpuDirectAabbWrite` off | Path A sentinel uploads + reduce dispatches are dominating B2/B2-paused. Only meaningful after P0 proves Path A is active. |
| `CalculateSkinnedBoundsInComputeShader` off | Compute pipeline cost itself (Path B) is dominating skinned scenes. |
| `SkinnedBoundsRecomputePolicy = Never` | Skinned-bounds refresh is irrelevant to current regression. |
| `MeshSubmissionStrategy` cycle | Confirms which path is being exercised; reveals strategy-specific cost. |
| `UseGpuBvh` off | Isolates internal BVH build/refit and Path A command-AABB refresh cost. |
| `GpuOcclusionCullingMode = Disabled` | CPU query coordinator pass-overhead. |
| `EnableNvidiaDlss` / `EnableIntelXessFrameGeneration` off | Vendor upscale overhead. |
| `DebugSettings.DumpIndirectArguments` off | Dump path adds barrier + readback per frame. |

### 4.5 GPU-side capture

RenderDoc / Nsight Graphics single frame on B1, B2, and B2-paused when validating Path A:

- Count `MultiDrawElementsIndirect[Count]` calls per frame (target: 1 per material batch).
- Count `glUseProgram`, `glBindBufferBase`, `glBindVertexArray` calls per draw — driver-side state churn often masquerades as GPU time in CPU traces.
- Inspect `_commandAabbBuffer` via buffer dump after Path A dispatches; confirm sentinel writes really were necessary.
- For strict zero-readback validation, treat `FullBucketScan` as the production path. `ActiveBucketList`, `MaterialTable`, and `BindlessMaterialTable` are readback-assisted diagnostics.

## 5. Suspect Hot-Spot Inventory

Ordered by expected impact in the lights-off scene.

### 5.1 CPU-direct path

| # | Suspect | Location | Evidence required |
| --- | --- | --- | --- |
| 1 | CPU collect/sort cost: `RenderTree.CollectVisible`, `RenderInfo.CollectCommands`, `AddCPU`, `SortedSet` insertion, render-distance updates. | `VisualScene3D.CollectRenderedItems`, `RenderCommandCollection.AddCPU` | Profiler root timings and command-count counters before/after collect. |
| 2 | Per-command swap overhead and small allocation in key reset (`Keys.ToArray()`). | `RenderCommandCollection.SwapBuffers` | Profiler scope; Report-NewAllocations; command count vs swap time. |
| 3 | Per-mesh GL setup: program state checks, material uniforms, engine uniforms, model/prev-model uniforms, VAO binds/config, draw submission. | `GLMeshRenderer.Rendering.cs` `Render` | `GLMeshRenderer.Render.*` profiler scopes; Nsight `glUseProgram`, uniform, VAO, and draw call counts. |
| 4 | Per-command profiler scope / transform-id push overhead when frame logging is enabled. | `RenderCommand.OnPreRender`, `RenderCommandMesh3D.Render` | A/B with profiler frame logging on/off; inspect `RenderCommand.Render` total. |
| 5 | `s_cpuOcclusionCoordinator.BeginPass` per-pass overhead if `GpuOcclusionCullingMode == CpuQueryAsync`. | `RenderCommandCollection.RenderCPU` | Toggle `GpuOcclusionCullingMode = Disabled` and remeasure. |

### 5.2 GPU-indirect / ZeroReadback path

| # | Suspect | Location | Evidence required |
| --- | --- | --- | --- |
| 1 | `GPUScene.SwapCommandBuffers` copies the whole updating command buffer and transparency metadata buffer to all-loaded buffers every frame, even when unchanged. | [GPUScene.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs) `SwapCommandBuffers` | Profiler scope; copied bytes/frame counter; skipped-copy counter after version stamping. |
| 2 | `MemoryBarrier(ClientMappedBuffer \| Command)` / `ShaderStorage \| ClientMappedBuffer \| Command` on every indirect draw, range draw, or zero-readback bucket. | `HybridRenderingManager.DispatchRenderIndirect`, `DispatchRenderIndirectRange`, `DispatchRenderIndirectCountBucket` | Nsight barrier count; A/B with batched barrier. |
| 3 | `RenderTraditionalBatched` rebinds material program per batch (~L3214–L3350). | `HybridRenderingManager.cs` | Nsight `glUseProgram` count; profiler scope. |
| 4 | `CoalesceContiguousBatches` allocates a fresh list every frame, including `[.. batches]` when there is one batch, and reruns when command list is unchanged. | `HybridRenderingManager.CoalesceContiguousBatches` | Profiler scope; Report-NewAllocations; batch version counter. |
| 5 | Zero-readback material-slot lookup and empty-bucket submission overhead. Active-bucket/material-table variants intentionally add readback. | `GPURenderPassCollection.IndirectAndMaterials`, zero-readback render methods | CPU profiler scopes per `EZeroReadbackMaterialDrawPath`; active bucket count; GPU readback byte counter. |

### 5.3 New Path A overhead

| # | Suspect | Location | Evidence required |
| --- | --- | --- | --- |
| A0 | Path A may not activate because GPU-only bounds success returns empty positions, but `ApplySkinnedBoundsResult` treats empty positions as failure before registration. | `SkinnedMeshBoundsCalculator.TryComputeFromPrepassOutput`, `RenderableMesh.ProcessSkinnedBoundsRefresh` | Counter/log proves registration and `RefreshAllSkinnedAabbs` happen after warmup. |
| A1 | `WriteCommandAabbSentinel` does `SetDataRawAtIndex` + `PushSubData(commandIndex*32, 32)` per command per refresh: many tiny `glBufferSubData` calls. | `GPUScene.WriteCommandAabbSentinel` | Driver call count; Path A sentinel counter; profiler scope. |
| A2 | `RefreshAllSkinnedAabbs` allocates a fresh `RenderableMesh[]` snapshot every call. | `SkinnedMeshBoundsCalculator.RefreshAllSkinnedAabbs` | Report-NewAllocations entry. |
| A3 | One full vertex reduce dispatch per command slot, even when multiple command slots refer to the same renderer/submesh bounds. | `SkinnedMeshBoundsCalculator.DispatchPathADirectWrite` | Nsight dispatch count; vertex count * slot count estimate; A/B against reduce-once/scatter-to-slots. |
| A4 | `VPRC_BuildAccelerationStructure` calls `RefreshAllSkinnedAabbs` before `PrepareBvhForCulling`, even when the BVH may not need rebuild/refit. | `VPRC_BuildAccelerationStructure.Execute` | Compare Path A refresh count to BVH build/refit count. |

### 5.4 P1 baseline findings (2026-05-10)

First three measurement-preset runs against the two-Sponza scene
(`xrengine_2026-05-10_20-25-04_pid93568` CpuDirect,
`_20-27-52_pid94104` GpuIndirectInstrumented,
`_20-29-57_pid105100` GpuIndirectZeroReadback,
`_20-37-37_pid102412` GpuIndirectInstrumented with single Sponza).

- **CpuDirect (both Sponzas):** ran to steady state without issue. Confirms
  the regression is GPU-path-specific.
- **GpuIndirectInstrumented (both Sponzas):** rendered only the Uber/forward
  Sponza, all black (textures never bound), then froze. Render-thread queue
  grew unboundedly: `queuedRender=7650`, individual jobs waited 3.5 s before
  execution, with `[ModelRenderDiag] emittedCommands=53`.
- **GpuIndirectZeroReadback (both Sponzas):** both Sponzas rendered, texture
  streaming started, then froze. Queue depth monotonically grew
  `1097 → 1551 → 2263 → 2575 → 2850 → 3934 → 4348 → 5466 → 7449`, with
  `Invoke:GLDataBuffer.PushSubData waited 7331.4 ms` / `11773.2 ms`.
  Secondary `source-link-completion-poll-stuck-nudge` on programIds 340/342
  is the known mitigation from
  [opengl-shared-context-worker-blocking-status-query](../../../../memory/repo/opengl-shared-context-worker-blocking-status-query.md)
  firing under load — symptom, not cause.
- **GpuIndirectInstrumented (single Sponza, 28 commands):** healthy.
  `queuedRender` oscillates 85 ↔ 330 and drains each cycle; PushSubData jobs
  1.4–5.1 ms; no stuck-nudge.

Conclusion: **per-frame `Invoke:GLDataBuffer.PushSubData` traffic scales
super-linearly with command count and saturates the render thread above some
threshold between 28 and 53 commands.** Path A is OFF
(`SkinnedBoundsGpuDirectAabbWrite=false`) and no skinned meshes are present,
so this is the **base** GPU-indirect path's per-command upload, not Path A.
Once the PushSubData queue saturates, texture-upload PushSubData jobs sit
behind it indefinitely → black materials → apparent freeze.

This promotes §5.2 #1 (`GPUScene.SwapCommandBuffers` whole-buffer copy each
frame) and #4 (`CoalesceContiguousBatches` re-allocation) to the top of the
GPU-path priority. It also implies an unmeasured contributor: **per-command
buffer uploads that fire every frame even when the command payload did not
change**. Next instrumentation step before further optimization: a per-buffer
PushSubData breakdown (caller / target buffer / bytes / call count, dumped
once per second when `XRE_PUSHSUBDATA_BREAKDOWN=1` is set) to attribute the
flood to a specific GPUScene buffer.

### 5.5 P1.5 attribution results (2026-05-10, two Sponzas)

With `XRE_PUSHSUBDATA_BREAKDOWN=1` and instance-name labels
(`<attribute>|<Name-or-id>|target=<EBufferTarget>`), two ~2-minute captures
attributed the flood unambiguously to the **dynamic mesh atlas**.

Session `xrengine_2026-05-10_21-03-38_pid68492`, log `log_opengl.txt`. Three
representative dump windows during mesh streaming:

| dump line | calls/sec | `Tangent` bytes | `Position` | `Normal` | `UV0` | `Indices` |
|---|---:|---:|---:|---:|---:|---:|
| 485 (initial growth) | 1 | 2.0 MB | 1.5 MB | 1.5 MB | 1.0 MB | — |
| 604 (peak A) | 28 | 316 MB | 237 MB | 237 MB | 158 MB | 58 MB |
| 692 (peak B) | 21 | 620 MB | 465 MB | 465 MB | 310 MB | 108 MB |

Every burst row resolved to the **same single buffer instance** —
`MeshAtlas_Dynamic_Tangents`, `MeshAtlas_Dynamic_Positions`,
`MeshAtlas_Dynamic_Normals`, `MeshAtlas_Dynamic_UV0`, and
`MeshAtlas_Dynamic_Indices` (id=123) — confirming this is one shared atlas
buffer being re-uploaded 21–28 times per second, not many distinct per-mesh
buffers.

Steady-state dumps (after mesh streaming settles) show **zero** vertex /
index traffic; the only persistent offenders are GPUScene SSBOs:
`LodTableBuffer` ~750 calls/s / 2.3 MB/s, `MeshDataBuffer` ~441/451 KB,
`RenderCommandsBuffer` ~440/84 KB. Tracked separately for P2 follow-up.

Root cause located in
[XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs)
`RebuildAtlasIfDirty`: the post-`EnsureAtlasBuffers` block called
`state.Positions.PushSubData()` (etc.) with no arguments — pushing the
**entire** SoA arrays every time the atlas was dirty, even though
`AppendMeshToAtlas` only appends to the tail and the previous contents are
unchanged.

This **supersedes** the §5.4 §5.2 #1 hypothesis. `SwapCommandBuffers` copy
size is negligible (the GPUScene SSBOs in the steady-state dump sum to
< 4 MB/s); the GB/s flood is entirely mesh-atlas vertex SoA re-uploads
during mesh streaming.

## 6. Optimization Plan

Each item lists: predicted win (qualitative), prerequisites, change summary, validation.

### 6.1 P0 correctness and measurement hygiene

#### P0-1. Fix Path A activation contract

- **Win:** prevents optimizing an inactive or partially active path.
- **Change:** represent GPU-only bounds success explicitly instead of using an empty positions array as an implicit signal. Options:
  - Add a `Result.HasCpuPositions` / `Result.HasBounds` shape and let `ApplySkinnedBoundsResult` update culling bounds even when CPU positions are absent.
  - Or split Path A command-AABB registration from CPU skinned-vertex-position caching.
- **Validation:** B2 logs/counters show nonzero registered Path A meshes, refreshed meshes, command slots, and command-AABB writes after warmup.

#### P0-2. Add the measurement preset and counters

- **Win:** prevents debug readbacks/logging from polluting every capture.
- **Change:** add a measurement preset or script that sets and logs all indirect debug switches, mesh submission strategy, zero-readback draw path, `UseGpuBvh`, `SkinnedBoundsGpuDirectAabbWrite`, and vendor upscale flags. Add counters listed in section 4.0.
- **Validation:** every baseline audit includes the preset output and a single counter table.

#### P0-3. Capture after warmup in a performance build

- **Win:** avoids shader warmup, one-shot indirect dumps, and Debug-only instrumentation dominating the result.
- **Change:** document/run a Release/profile launch path for perf captures. Discard warmup frames before calculating medians.
- **Validation:** audit notes include build configuration, warmup duration, and captured frame range.

### 6.2 Quick wins (lowest risk, ship first)

#### O-1. Reuse Path A registry snapshot buffer

- **Win:** removes one `RenderableMesh[]` allocation per frame.
- **Change:** add `private RenderableMesh[] _refreshScratch = Array.Empty<RenderableMesh>(); private int _refreshScratchCount;` to `SkinnedMeshBoundsCalculator`. In `RefreshAllSkinnedAabbs`, grow geometrically (`Math.Max(_refreshScratch.Length * 2, count)`), `CopyTo` into it, iterate `0..count`. Treat as render-thread-only state (it is).
- **Validation:** Report-NewAllocations diff shows the entry gone.

#### O-2. Skip Path A work when nothing dirty

- **Win:** zero Path A overhead for paused skinned scenes; meaningful in B2-paused, not B1.
- **Change:** track per-mesh Path A dirty state or revision when registered renderers' bones/root transforms move. `RefreshAllSkinnedAabbs` should skip clean meshes while still refreshing active animation.
- **Risk:** a single global dirty flag can either over-dispatch or accidentally skip a mesh after another mesh clears it; prefer per-mesh revision/state.
- **Validation:** B2-paused shows zero Path A dispatches after the first clean frame; animated B2 still updates bounds.

#### O-3. Gate Path A refresh behind BVH need

- **Win:** avoids refreshing command AABBs when no BVH build/refit will consume them.
- **Change:** expose a cheap `GPUScene` query for whether `PrepareBvhForCulling` will rebuild or refit for the current primitive count, and call `RefreshAllSkinnedAabbs` only when needed.
- **Risk:** if GPU culling consumes updated leaf bounds outside the BVH prepare path, the gate must account for that consumer too.
- **Validation:** Path A refresh count matches BVH build/refit count.

### 6.3 Medium changes (test first)

#### O-4. Reduce once per renderer, scatter to command slots

- **Win:** removes duplicate full-vertex reductions when one renderer owns multiple command slots/submeshes.
- **Change:** run the reduce once into a per-renderer or scratch bounds slot, then copy/scatter the final world-space AABB into every command index owned by that renderer. If submesh bounds must differ, reduce per submesh, not per command slot.
- **Risk:** must preserve correctness for multi-submesh meshes if submesh-specific bounds are required for culling quality.
- **Validation:** Nsight reduce dispatch count drops from command-slot count to renderer/submesh count; visual culling parity holds.

#### O-5. Batch contiguous sentinel writes

- **Win:** collapses N tiny `glBufferSubData(32B)` calls into one per contiguous run.
- **Change:** in the call sites that loop sentinels (per-renderer indices list), sort/scan command indices, write sentinels into a CPU staging span, then issue one `PushSubData(firstSlot*32, count*32)` per contiguous range.
- **Risk:** code duplication of sentinel byte pattern; needs unit test for boundary cases (single, two adjacent, gap).
- **Validation:** Nsight `glBufferSubData` call count drops; profiler scope drops.

#### O-6. Skip `GPUScene.SwapCommandBuffers` payload copy when neither copied buffer mutated

- **Win:** removes the dominant per-frame buffer cost in static GPU-indirect frames.
- **Change:** version-stamp `_updatingCommandsBuffer` and `_updatingTransparencyMetadataBuffer` on each `Add`/`Remove`/`TryUpdateMeshCommand` write. In `SwapCommandBuffers`, if versions match the last swapped versions, update counts/state but skip `Memory.Move` and `PushSubData` for unchanged payloads.
- **Risk:** `_commandAabbBuffer` is not part of the current swap copy and should not be folded into the same version gate. AABB mutations need a separate dirty/refit signal.
- **Validation:** B1 profiler scope on `SwapCommandBuffers` drops near zero between mutations.

#### O-7. Coalesce `MemoryBarrier` calls in indirect submission

- **Win:** reduces driver-side serialization for multi-pass frames.
- **Change:** issue one appropriate barrier at the start of indirect submission per render pass/material-bucket phase instead of per draw, unless interleaved compute writes the consumed buffers between draws.
- **Risk:** correctness regression if a future compute path interleaves; gate behind explicit `RequiresPerDrawIndirectBarrier` per-batch flag (default false).
- **Validation:** Nsight barrier count drop; visual diff vs baseline frame.

### 6.4 Larger changes (design before code)

#### O-8. CPU-direct: build a frame-stable submission plan

- Build a frame-stable submission plan (`SubmissionEntry[]` with resolved renderer/material/render-options pointers and sort key) at command-collection commit time. Iterate the array in `RenderCPU`. Keep the design honest: current `RenderCPU` already avoids holding `_lock` across rendering, so the win is lower collection/sort/enumeration overhead and better state grouping, not lock removal.

#### O-9. GPU path: cache `CoalesceContiguousBatches` output by command-list version

- Re-coalesce only when the underlying command list version changes. Persist `BatchPlan` between frames.

#### O-10. Sentinel seeding via compute clear shader

- Replace CPU sentinel writes entirely with a tiny compute kernel that writes the sentinel pattern to the slots in `_commandAabbBuffer` for registered Path A renderers. One dispatch per acceleration-structure refresh max.

#### O-11. MeshAtlas subrange-append push (LANDED 2026-05-10)

- **Win:** eliminates 2–3 GB/s of redundant vertex SoA re-uploads during mesh
  streaming; root cause of GPU-path freeze per §5.5.
- **Change:** in
  [`GPUScene.RebuildAtlasIfDirty`](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs),
  track `LastUploadedVertexCount` and `LastUploadedIndexCount` per
  `AtlasTierState`. Push only the appended tail
  `[LastUploaded .. VertexCount)` via `PushSubData(offset, length)` instead
  of `PushSubData()` (no args = full buffer). Reset the high-water marks to
  0 whenever a buffer is `Resize`d (forces a single full PushData on grow),
  destroyed, fully cleared, or compacted via `SlideDown` during mesh
  removal. Indices: same idea — restart the triangle-write loop at
  `LastUploadedIndexCount / 3` and push only the new index byte range.
  New static helper `PushVertexRange(buffer, first, newCount)` centralises
  the offset/length math.
- **Telemetry follow-up:** extended the env-gated breakdown with per-call
  `min`/`max`/`avg` byte sizes so future bursts can be classified
  (full-buffer vs tail-append) at a glance.
- **Validation:** re-run `Measurement-PushSubDataBreakdown-GpuIndirectInstrumented`
  with both Sponzas; expect MeshAtlas vertex labels either absent from
  steady-state dumps or showing `max == single-mesh size` rather than full
  atlas size. Two-Sponza GPU paths should no longer freeze under streaming
  load.

## 7. Phased Execution

| Phase | Items | Gate |
| --- | --- | --- |
| P0 — Sanity & tooling | P0-1, P0-2, P0-3. | Path A activation proven or disproven; debug flags/preset logged; counters available. |
| P1 — Instrument & measure | §4 setup, capture B1+B2+B2-paused baselines for all 3 strategies. | Baseline numbers committed to a `docs/work/audit/render-perf-<date>.md`. **Partial 2026-05-10: GPU paths freeze above ~28 commands due to PushSubData flood — see §5.4.** |
| P1.5 — PushSubData attribution | Add gated per-buffer PushSubData breakdown (caller, buffer label, bytes, count, dumped 1 Hz when `XRE_PUSHSUBDATA_BREAKDOWN=1`). Re-run GpuIndirectInstrumented with both Sponzas. | Single dominant buffer (or pair) identified; numbers committed to audit doc. **Done 2026-05-10: see §5.5; root cause = MeshAtlas full re-upload.** |
| P2 — GPU indirect unblock | **O-11** (MeshAtlas subrange-append push) — first based on §5.5; landed 2026-05-10, awaiting validation. Then **O-6** (skip `SwapCommandBuffers` copy when clean), O-9, O-7. Re-measure between each. Investigate residual `LodTableBuffer`/`MeshDataBuffer` per-frame churn (750/441 calls/s) once O-11 is validated. | GPU paths run both Sponzas without queue-saturation freeze. |
| P3 — Path A cleanup | O-1, O-2, O-3, then O-4/O-5 if counters justify. Only meaningful once skinned/B2 scene runs cleanly. | B2/B2-paused Path A overhead removed or quantified. |
| P4 — CPU-direct/larger design | O-8, then O-10 if Path A still needs it. | Both paths pass perf bar on B1+B2. |

## 8. Risks

- Path A activation fix must not silently break consumers that still need CPU skinned vertex positions for BVH/raycast workflows. Mitigation: make GPU-only bounds success explicit in the result type or call path.
- Per-mesh dirty tracking can produce stale culling if a bone/root transform change is missed. Mitigation: DEBUG asserts/counters that compare dirty revisions to dispatched revisions.
- Version-stamp logic on `GPUScene` (O-6) must include every copied payload buffer, or stale frames render. Mitigation: central increment helpers for command and transparency metadata mutations; DEBUG asserts around direct buffer writes.
- Do not include `_commandAabbBuffer` in the command-buffer swap version gate. It has a separate lifetime and feeds BVH build/refit.
- Barrier coalescing (O-7) is a correctness footgun; default-off behind per-batch/per-phase opt-in.
- Compute sentinel seeding (O-10) needs to run before BVH refresh and after any compute that writes positions; ordering must be encoded in `VPRC_BuildAccelerationStructure`.

## 9. Validation Checklist

- [ ] P0 activation audit shows whether Path A registration, refresh, sentinel writes, and reduce dispatches are nonzero in B2.
- [ ] Measurement preset output records build configuration, strategy, zero-readback draw path, debug flags, `UseGpuBvh`, Path A setting, and vendor upscale flags.
- [ ] Captures are taken after shader/program warmup; first warmup frames are excluded from medians.
- [ ] `profiler-fps-drops.log` HotPath delta vs baseline captured for each phase.
- [ ] Report-NewAllocations diff committed showing zero new hot-path allocations.
- [ ] Nsight frame: `MultiDrawElementsIndirect` count, barrier count, reduce dispatch count, `glUseProgram` count, `glBindVertexArray` count, and `glBufferSubData` count vs baseline.
- [ ] Strict zero-readback validation uses `FullBucketScan`; readback-assisted variants are labeled diagnostic.
- [ ] `Test-VulkanPhase3-Regression` and `Test-SurfelGi` still green.
- [ ] Visual parity: golden-image diff of B1 + B2 vs pre-Path-A baseline.
- [ ] DefaultRenderPipeline notes ([docs/architecture/rendering/default-render-pipeline-notes.md](../../../architecture/rendering/default-render-pipeline-notes.md)) updated if invariants change.

## 10. Open Questions

- Should `SkinnedBoundsGpuDirectAabbWrite` remain default-off until P0 activation and O-1/O-4 land? (Recommend yes; it already defaults off.)
- Is there a future need for partial `SwapCommandBuffers` (range-only blit when N slots mutated)? Defer until O-6 measurements are in.
- Do we want the measurement preset to live in a new `UnitTestingPerfPreset.jsonc` rather than mutating `UnitTestingWorldSettings.jsonc`? (Recommend yes; keeps it generated.)
- Should `ActiveBucketList`, `MaterialTable`, and `BindlessMaterialTable` be excluded from strict zero-readback performance gates? (Recommend yes; keep them as diagnostics.)
